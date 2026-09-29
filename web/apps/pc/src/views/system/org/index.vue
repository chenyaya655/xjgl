<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import type { TreeInstance } from 'element-plus'

import {
  describeExportProgress,
  describeTruncation,
  exportTaskApi,
  organizationImportApi,
  orgApi,
  saveBlob
} from '@/api'
import type {
  ExportTask,
  ImportColumn,
  ImportOrganizationsResult,
  OrgDto,
  OrgQuery,
  OrgTreeNode
} from '@/api/types'
import { ExportTaskKinds, OrgTypes } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import { CodeCategories, useCodeOptions } from '@/composables/useCodeOptions'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'
import { probeCsvEncoding } from '@/utils/csvEncoding'

/**
 * 组织管理：省厅 → 市局 → 县局 → 学校 四级树。
 *
 * 这个页面是所有其它模块的前提：没有组织树，就无法建用户、无法分配数据范围、
 * 学生的"学校"字段也无处可选。所以它必须支持**大组织树**的打开方式 ——
 * 全省有上万所学校，一次性拉整棵树（接口返回 1MB+）在县区网络上会明显卡顿，
 * 因此这里用 el-tree 的**懒加载**：展开哪个节点才去取哪个节点的下级。
 *
 * 另一处刻意的设计：**新增下级时自动推断组织类型**。
 * 省厅下面只能建地市、地市下面只能建县区、县区下面只能建学校 ——
 * 让用户手选类型既容易选错，也要多一次交互。
 */
const auth = useAuthStore()

/** 组织层级中文名**来自码表**（ORG_TYPE），不手写中文。 */
const orgTypeCodes = useCodeOptions(CodeCategories.OrgType)

const canCreate = computed(() => auth.can(MetadataPermissions.Sys.Org.Create))
const canUpdate = computed(() => auth.can(MetadataPermissions.Sys.Org.Update))
const canDelete = computed(() => auth.can(MetadataPermissions.Sys.Org.Delete))

/**
 * 导入 / 导出各自一个权限码（后端 `OrganizationImportExportAppService` 与
 * `ExportTaskPermissions` 用的是同两个码）。
 *
 * 为什么不像学生那样把按钮只挂在"能查"上：组织是**参考数据** ——
 * 能查组织不等于可以往库里灌一万所学校，也不等于可以把全省名录落盘带走。
 */
const canImport = computed(() => auth.can(MetadataPermissions.Sys.Org.Import))
const canExport = computed(() => auth.can(MetadataPermissions.Sys.Org.Export))

/** 换 key 强制重建树（见 refreshTree 的说明）。 */
const treeKey = ref(0)
const selected = ref<OrgDto | null>(null)
const loadingDetail = ref(false)

/** 各级组织的下级类型；学校没有下级。键用 `OrgTypes` 常量，不写裸字符串。 */
const childTypeOf: Record<string, string | undefined> = {
  [OrgTypes.Province]: OrgTypes.City,
  [OrgTypes.City]: OrgTypes.County,
  [OrgTypes.County]: OrgTypes.School,
  [OrgTypes.School]: undefined
}

const selectedChildType = computed(() =>
  selected.value ? childTypeOf[selected.value.orgType] : OrgTypes.Province
)

const canAddChild = computed(() => !!selectedChildType.value)

// ---------------------------------------------------------------------------
// 树（懒加载）
// ---------------------------------------------------------------------------

/**
 * el-tree 的懒加载回调。
 *
 * 根节点用「按类型过滤的列表接口」取省厅，其余层级用 children 接口。
 * 注意不要用 `/org/children/{parentId}` 传空来取根 —— 空路由段会 404。
 */
async function loadNode(node: { level: number; data: OrgDto | OrgTreeNode }, resolve: (data: unknown[]) => void) {
  try {
    if (node.level === 0) {
      const roots = await orgApi.getRoots()
      resolve(roots.map((o) => ({ ...o, leaf: false })))

      return
    }

    const children = await orgApi.getChildren(node.data.id)

    resolve(children.map((c) => ({ ...c, leaf: !c.hasChildren })))
  } catch {
    resolve([])
  }
}

async function selectNode(data: OrgDto | OrgTreeNode): Promise<void> {
  loadingDetail.value = true

  try {
    const detail = await orgApi.get(data.id)
    selected.value = detail
    form.parentId = detail.parentId ?? null
    form.orgType = detail.orgType
    form.code = detail.code
    form.name = detail.name
    form.shortName = detail.shortName ?? ''
    form.sort = detail.sort
    form.isEnabled = detail.isEnabled
    form.remark = detail.remark ?? ''

    // 上级名称必须跟详情一起刷新：移动节点之后它变了，
    // 而这里以前只在「新增下级」时取过一次 —— 换一个节点看详情，
    // 描述里的"上级"还留着上一个节点的，读起来像是移动没生效。
    await resolveParentName(detail.parentId ?? null)
  } finally {
    loadingDetail.value = false
  }
}

/** 上级组织名称（用物化路径的后缀反查太绕，这里直接查一次父节点）。 */
const parentName = ref<string>('—')

async function resolveParentName(parentId?: string | null): Promise<void> {
  if (!parentId) {
    parentName.value = '（根节点）'

    return
  }

  try {
    parentName.value = (await orgApi.get(parentId)).name
  } catch {
    parentName.value = '—'
  }
}

function reload(): void {
  selected.value = null
  parentName.value = '—'
  refreshTree()
}

/**
 * 重新挂载树。
 *
 * 这里刻意不用 el-tree 的内部实例方法（store.setData / updateKeyChildren）：
 * 那些是内部实现，升级 Element Plus 时可能被改掉，而且类型上也拿不到。
 * 换 key 强制重建是官方支持的用法，代价只是重新加载一次根节点。
 */
function refreshTree(): void {
  treeKey.value += 1
}

// ---------------------------------------------------------------------------
// 表单
// ---------------------------------------------------------------------------

const form = reactive({
  parentId: null as string | null,
  // `as string` 是必要的：`OrgTypes` 是 as const，直接用会把字段推断成字面量
  // "Province"，之后赋任何别的层级都会报类型错。
  orgType: OrgTypes.Province as string,
  code: '',
  name: '',
  shortName: '',
  sort: 0,
  isEnabled: true,
  remark: ''
})

function toPayload() {
  return {
    parentId: form.parentId,
    orgType: form.orgType,
    code: form.code.trim(),
    name: form.name.trim(),
    shortName: form.shortName?.trim() || null,
    sort: form.sort,
    isEnabled: form.isEnabled,
    remark: form.remark?.trim() || null
  }
}

async function save(): Promise<void> {
  if (!selected.value) {
    return
  }

  if (!form.code.trim() || !form.name.trim()) {
    ElMessage.warning('组织编码与名称都不能为空。')

    return
  }

  await orgApi.update(selected.value.id, toPayload())
  ElMessage.success('已保存')

  await selectNode(selected.value)
}

async function remove(): Promise<void> {
  if (!selected.value) {
    return
  }

  try {
    await ElMessageBox.confirm(
      `确定删除「${selected.value.name}」吗？` +
        '若该组织下还有下级组织或学生，服务端会拒绝删除 —— 这是有意为之，避免产生孤儿数据。',
      '删除确认',
      { type: 'warning' }
    )
  } catch {
    return
  }

  await orgApi.remove(selected.value.id)
  ElMessage.success('已删除')
  reload()
}

// ---------------------------------------------------------------------------
// 新增下级
// ---------------------------------------------------------------------------

const createVisible = ref(false)
const creating = ref(false)
const createForm = reactive({
  parentName: '',
  parentId: null as string | null,
  orgType: OrgTypes.Province as string,
  code: '',
  name: '',
  shortName: '',
  sort: 0,
  remark: ''
})

function openCreate(): void {
  createForm.parentId = selected.value?.id ?? null
  createForm.parentName = selected.value
    ? `${selected.value.name}（${orgTypeCodes.nameOf(selected.value.orgType) ?? ''}）`
    : '（根节点）'
  createForm.orgType = selectedChildType.value ?? OrgTypes.Province
  createForm.code = ''
  createForm.name = ''
  createForm.shortName = ''
  createForm.sort = 0
  createForm.remark = ''
  createVisible.value = true
}

async function doCreate(): Promise<void> {
  if (!createForm.code.trim() || !createForm.name.trim()) {
    ElMessage.warning('组织编码与名称都不能为空。')

    return
  }

  creating.value = true

  try {
    const created = await orgApi.create({
      parentId: createForm.parentId,
      orgType: createForm.orgType,
      code: createForm.code.trim(),
      name: createForm.name.trim(),
      shortName: createForm.shortName?.trim() || null,
      sort: createForm.sort,
      isEnabled: true,
      remark: createForm.remark?.trim() || null
    })

    ElMessage.success(`已创建「${created.name}」`)
    createVisible.value = false

    // 重建树后再把新节点显示在右侧（不再多查一次接口）
    refreshTree()
    selected.value = created
    form.parentId = created.parentId ?? null
    form.orgType = created.orgType
    form.code = created.code
    form.name = created.name
    form.shortName = created.shortName ?? ''
    form.sort = created.sort
    form.isEnabled = created.isEnabled
    form.remark = created.remark ?? ''
    await resolveParentName(created.parentId)
  } finally {
    creating.value = false
  }
}

// ---------------------------------------------------------------------------
// 移动到…（改上级组织）
// ---------------------------------------------------------------------------

/**
 * 【为什么需要这个对话框】
 * 组织详情里的「上级」是只读的，而 `orgApi.move` 一直没有界面入口 ——
 * 机构改革（学校由 A 县划到 B 县）时只能去改库。而物化路径是数据权限、
 * 层级判断、学籍辅号前缀的共同依据：手工改库时漏掉子树重写，
 * 下级组织会"凭空消失"（前缀查不到了），而且**不报任何错**。
 */
const moveVisible = ref(false)
const moving = ref(false)

/** 换 key 强制重建对话框里的树：可选项依赖被移动的节点，每次打开都得是新的。 */
const moveTreeKey = ref(0)

/** 选中的新上级：`undefined` = 还没选；`null` = 根节点；字符串 = 某组织的 Id。 */
const moveTargetId = ref<string | null | undefined>(undefined)
const moveTargetName = ref('（未选择）')

/** 移动对话框里的树实例（只在"点到了不可选的节点"时用来把高亮拨回去）。 */
const moveTreeRef = ref<TreeInstance | null>(null)

/** 「根节点」伪节点的 Id：不是 Guid，因此不会与真实组织 Id 相撞。 */
const ROOT_OPTION_ID = '__root__'

/** 移动对话框里树的节点形状（比 OrgTreeNode 多两个只用于展示/禁用的字段）。 */
interface MovePickerNode {
  id: string
  name: string
  orgType?: string
  pathCode?: string
  leaf: boolean
  disabled: boolean
  disabledReason?: string
  isRootOption?: boolean
}

/**
 * 该节点能不能当新上级；不能则给出原因（`null` = 可以选）。
 *
 * 【两条必须挡住的，理由完全不同】
 *   1) **自己或自己的下级**：会形成环。后端 `MoveAsync` 也拦（比对物化路径前缀），
 *      但那是提交之后才报错 —— 用户白挑一遍，而且那时界面上已经看不出该怎么改；
 *   2) **层级不匹配**：省→市→县→校这条链是数据范围与统计口径的前提。
 *      把一个县局挂到学校下面，路径照样算得出来、后端**没有**这条校验，
 *      但整棵树的层级从此是脏的，而脏掉的层级要到数据权限出问题时才会被发现。
 *      判据复用页面上的 `childTypeOf`（与「新增下级」自动推断类型是同一张表，
 *      不另写一套规则，否则两处迟早不一致）。
 */
function moveRejectReason(node: { id: string; orgType?: string; pathCode?: string }): string | null {
  const moved = selected.value

  if (!moved) {
    return null
  }

  if (node.id === moved.id) {
    return '不能移到自己下面'
  }

  if (
    node.pathCode &&
    (node.pathCode === moved.pathCode || node.pathCode.startsWith(`${moved.pathCode}.`))
  ) {
    return '不能移到自己的下级'
  }

  if (node.orgType && childTypeOf[node.orgType] !== moved.orgType) {
    return '层级不匹配'
  }

  return null
}

function toPickerNode(org: OrgDto | OrgTreeNode): MovePickerNode {
  const reason = moveRejectReason(org)

  return {
    id: org.id,
    name: org.name,
    orgType: org.orgType,
    pathCode: org.pathCode,
    // 只有"确实没有下级"才标成叶子。`OrgDto`（根节点那一层用的）没有
    // `hasChildren` 字段，一律允许展开 —— 多展开一层最多多一次空请求，
    // 而错标成叶子会**藏掉可选项**。
    leaf: 'hasChildren' in org ? !org.hasChildren : false,
    disabled: reason !== null,
    disabledReason: reason ?? undefined
  }
}

/** 移动对话框里的懒加载树（与左侧组织树同一套取数方式，只在过滤规则上不同）。 */
async function loadMoveNode(
  node: { level: number; data: MovePickerNode },
  resolve: (data: unknown[]) => void
): Promise<void> {
  try {
    if (node.level === 0) {
      // 「根节点」是一个**伪节点**：el-tree 没有"清空选择"的语义，而"移到根节点"
      // （newParentId = null）是真实需求（新建的省厅、或把节点从树里摘出来），
      // 因此给它一个真实可选中的位置，而不是靠"什么都不选"来表达。
      const roots = await orgApi.getRoots()

      resolve([
        {
          id: ROOT_OPTION_ID,
          name: '（根节点 / 无上级）',
          leaf: true,
          disabled: false,
          isRootOption: true
        } satisfies MovePickerNode,
        ...roots.map(toPickerNode)
      ])

      return
    }

    const children = await orgApi.getChildren(node.data.id)

    resolve(children.map(toPickerNode))
  } catch {
    resolve([])
  }
}

function openMove(): void {
  if (!selected.value) {
    return
  }

  // 默认"未选择"而不是默认当前上级：预选中一个懒加载树里的深层节点做不到，
  // 而默认根节点又太危险（点一下确定就把节点从树里摘出去了）。
  moveTargetId.value = undefined
  moveTargetName.value = '（未选择）'
  moveTreeKey.value += 1
  moveVisible.value = true
}

function pickMoveParent(data: MovePickerNode): void {
  if (data.disabled) {
    return
  }

  if (data.isRootOption) {
    moveTargetId.value = null
    moveTargetName.value = '（根节点 / 无上级）'

    return
  }

  moveTargetId.value = data.id
  moveTargetName.value = `${data.name}（${orgTypeCodes.nameOf(data.orgType) ?? data.orgType ?? ''}）`
}

/**
 * 树上切换选中项。
 *
 * 用 `current-change` 而不是 `node-click`：el-tree 对**禁用节点**照样会改高亮
 * 并抛事件（它的 `handleClick` 只挡住了勾选），于是点一下灰色的"不能移到自己下面"，
 * 高亮会跑过去而"已选"没变 —— 看上去就像是选上了。
 * 所以这里在收到不合格的节点时，把高亮拨回上一个有效选择。
 */
function onMoveCurrentChange(data: MovePickerNode | null): void {
  if (!data) {
    return
  }

  if (data.disabled) {
    // 还没选过就清除高亮；选过就回到那个节点（根节点伪节点的 Id 在这里还原）
    moveTreeRef.value?.setCurrentKey(
      moveTargetId.value === undefined ? null : moveTargetId.value ?? ROOT_OPTION_ID
    )

    return
  }

  pickMoveParent(data)
}

async function doMove(): Promise<void> {
  const node = selected.value

  // `undefined` = 用户还没选。不能把它当"移到根节点"提交 ——
  // 两者只差一个字面量，但一个是"不动"，一个是把节点摘出整棵树。
  if (!node || moveTargetId.value === undefined) {
    return
  }

  moving.value = true

  try {
    await orgApi.move(node.id, moveTargetId.value)

    ElMessage.success('已移动，整棵子树的物化路径已重写')
    moveVisible.value = false

    // 移动之后物化路径、层级、上级都变了：重建树，再按 Id 重新取一次详情
    refreshTree()
    await selectNode(node)
  } catch {
    // 拦截器已提示（例如"不能把组织移动到它自己的下级"）。
    // 这里**不关**对话框、也不重复弹一遍同样的错：让用户改选一个上级直接再试。
  } finally {
    moving.value = false
  }
}

// ---------------------------------------------------------------------------
// 导入（省 / 市 / 县 / 校）
// ---------------------------------------------------------------------------

/**
 * 组织名录用 CSV 导入，是**部署前置条件**：全省上万所学校靠界面一个个点
 * 是不现实的（见后端 `OrganizationImportExportDtos` 的说明）。
 *
 * 这里刻意与学生导入对话框长得一样，理由不是"统一风格"，而是**同一批坑**：
 *   1) 必须先把文件按字节探一次编码，否则 GBK 文件会被解码成乱码送去后端，
 *      换回一句"缺少必需列"——用户会去反复检查列名；
 *   2) 失败明细必须逐行展示（`rowIndex` 是 Excel 行号），
 *      只弹一句"导入失败"等于让用户拿几百行数据自己找问题。
 */
const importVisible = ref(false)
const importing = ref(false)
const importColumns = ref<ImportColumn[]>([])
const importResult = ref<ImportOrganizationsResult | null>(null)
const importFileName = ref('')
/** 本次选中文件的编码判定结果，回显在对话框里（便于"为什么被拒"当场看清）。 */
const encodingNote = ref('')
const fileInput = ref<HTMLInputElement | null>(null)

async function openImport(): Promise<void> {
  importVisible.value = true
  importResult.value = null
  importFileName.value = ''
  encodingNote.value = ''

  // 列说明只在第一次打开时拉（它是模板的一部分，运行期不变）。
  // 拉失败也不阻塞导入：列说明是提示，真正的校验在后端 ——
  // 因为一个提示接口挂掉就禁用整个导入功能，是本末倒置。
  if (importColumns.value.length === 0) {
    try {
      importColumns.value = await organizationImportApi.getColumns()
    } catch {
      // 拦截器已提示
      importColumns.value = []
    }
  }
}

async function downloadTemplate(): Promise<void> {
  try {
    const csv = await organizationImportApi.getTemplate()

    // 模板里的 BOM 由后端 `Csv.WriteForExcel` 加上（Excel 不认无 BOM 的 UTF-8），
    // 这里**不再自己补一个**：补两次会在文件开头多出一个空字符/空行。
    saveBlob(new Blob([csv], { type: 'text/csv;charset=utf-8' }), '组织导入模板.csv')
  } catch {
    // 拦截器已提示
  }
}

function pickFile(): void {
  fileInput.value?.click()
}

async function doImport(): Promise<void> {
  const input = fileInput.value

  if (!input?.files?.length) {
    ElMessage.warning('请先选择要导入的 CSV 文件。')
    return
  }

  const file = input.files[0]
  importFileName.value = file.name
  encodingNote.value = ''
  importResult.value = null
  importing.value = true

  try {
    // ⚠ 先按**字节**探测编码，再决定要不要送给后端（与学生导入同一处坑）。
    //
    // 直接用 `file.text()` 是错的：它按 UTF-8 解码，GBK 文件会变成一串替换字符，
    // 于是后端只回一句「导入文件缺少必需列：组织类型、组织编码…」——
    // 用户看到的提示与真正原因（编码）毫无关系，会去反复检查列名。
    const probe = probeCsvEncoding(await file.arrayBuffer())

    encodingNote.value = probe.detected

    if (!probe.ok) {
      // 明确拒绝并给出可执行的做法，而不是把乱码送去后端换回一句莫名其妙的报错
      ElMessage.error(probe.reason ?? '文件编码不是 UTF-8，无法导入。')

      return
    }

    importResult.value = await organizationImportApi.import(probe.text)

    if (importResult.value.failed === 0) {
      ElMessage.success(importResult.value.message)
    } else {
      ElMessage.warning(importResult.value.message)
    }

    // 部分成功也要刷新树：成功的那部分**已经落库**了。
    // 用 refreshTree()（换 key 重建）而不是重取某个节点：导入可能新增了任意层级，
    // 而懒加载的树只有重建才会重新取根节点，否则新导入的省厅根本不会出现。
    if (importResult.value.succeeded > 0) {
      refreshTree()
    }
  } catch {
    // 错误提示由 http 拦截器统一弹出（含后端 reason，如"找不到上级组织编码「…」"），
    // 这里**不重复弹**：同一个失败弹两次，用户会以为是两个不同的问题。
  } finally {
    importing.value = false

    // 必须清空：否则连续选同一个文件不会再触发 change，用户会以为"点了没反应"
    input.value = ''
  }
}

// ---------------------------------------------------------------------------
// 导出（后台任务）
// ---------------------------------------------------------------------------

/**
 * 页面卸载后停止轮询。
 *
 * 少了这个，用户点了导出再立刻切走，定时器仍会每 1.5 秒打一次接口 ——
 * 直到 5 分钟超时为止，而那时他早就不在这个页面了。
 */
let disposed = false
onUnmounted(() => {
  disposed = true
})

const exporting = ref(false)
/** 任务的真实阶段（排队中 / 已处理 N / M（x%） / 正在生成）：只说"转圈"用户不知道卡在哪一步。 */
const exportProgressText = ref('')

const exportVisible = ref(false)
const exportForm = reactive({
  /** 组织类型走**码表**（ORG_TYPE），不在前端写死 Province / City / County / School。 */
  orgType: undefined as string | undefined,
  keyword: '',
  /**
   * 只导出启用的组织。
   *
   * 默认**关**（= 导出全部组织），与「一键导出」按钮一致 ——
   * 见 `exportAll` 里关于 `onlyEnabled` 的说明：默认漏掉停用组织是静默的数据丢失。
   */
  onlyEnabled: false
})

function openExport(): void {
  exportForm.orgType = undefined
  exportForm.keyword = ''
  exportForm.onlyEnabled = false
  exportVisible.value = true
}

/**
 * 提交导出任务并等它完成，然后下载（两个入口共用这一条路径）。
 *
 * 组织导出走**后台任务**，不是同步下载接口：上万所学校的 CSV 在请求线程里拼
 * 会把线程占满，而且浏览器先超时、服务端还在跑 —— 用户看到"失败"，
 * 文件其实已经生成，他再点一次，于是白干两遍。
 *
 * 与学生名单页共用同一条路径：提交 → 轮询 → 下载 → 提示。
 */
async function submitExport(orgQuery: OrgQuery): Promise<void> {
  exporting.value = true
  exportProgressText.value = '排队中…'

  try {
    const task = await exportTaskApi.run(
      { kind: ExportTaskKinds.OrganizationCsv, orgQuery },
      {
        shouldStop: () => disposed,
        onProgress: (t: ExportTask) => {
          // 文案只有 `describeExportProgress` 一处实现：组织导出结构上给不出百分比
          // （一次性序列化），它会如实退回"正在生成…"而不是显示卡住的 0%。
          exportProgressText.value = describeExportProgress(t)
        }
      }
    )

    const blob = await exportTaskApi.download(task.id)

    // 文件名以**后端给的**为准（带了日期与条数等有用信息），后端没给才用兜底名
    saveBlob(blob, task.fileName || `组织名单_${new Date().toISOString().slice(0, 10)}.csv`)

    // 截断必须显式提示。组织导出**永远不截断**（见 ExportTaskKinds 的说明），
    // 但仍然照常走这一步：这是共用约定，而且真触到上限时更不能静默少人。
    const truncation = describeTruncation(task)

    if (truncation) {
      ElMessage.warning(truncation)
    } else {
      ElMessage.success(
        task.message ? `${task.kindName}已完成：${task.message}` : `${task.kindName}已完成`
      )
    }

    exportVisible.value = false
  } catch (e) {
    // 这里的提示**不能省**，但也不能重复弹：
    //   · 入队/轮询/下载的 HTTP 失败已被拦截器弹过（且给出的是后端 reason），
    //     再弹一次就会变成"一句话说两遍"；
    //   · 而任务本身 Failed / 被取消 / 前端等超时是 `exportTaskApi.run` 抛的
    //     **业务错误**，它不经过拦截器 —— 不提示就等于"点了导出、转圈停下、
    //     什么都没发生"，用户只会以为功能坏了。
    // 用 `isAxiosError` 这个 axios 自己打的标记把两者分开。
    if (!(e as { isAxiosError?: boolean })?.isAxiosError) {
      ElMessage.error(e instanceof Error ? e.message : '导出失败，请稍后重试')
    }
  } finally {
    // 无论成功、失败还是超时都要清掉，否则那行提示会一直留在页面上
    exporting.value = false
    exportProgressText.value = ''
  }
}

/**
 * 一键导出：不带任何筛选，导出**全部组织（含已停用）**。
 *
 * ⚠ `onlyEnabled: false` 必须**显式**写出来：后端 `OrgQueryDto.OnlyEnabled`
 *   默认是 **true**（只导启用的）。漏传这一个字段不会报任何错，文件也照样生成成功，
 *   只是所有停用过的组织都不在里面 —— 而这份文件最常见的用途是
 *   "换一套环境重建组织树"，少掉的组织要到树建歪了才会被发现。
 */
async function exportAll(): Promise<void> {
  await submitExport({ onlyEnabled: false })
}

/** 按条件导出（对话框里的组织类型 / 关键字 / 是否含停用）。 */
async function exportFiltered(): Promise<void> {
  await submitExport({
    orgType: exportForm.orgType,
    keyword: exportForm.keyword.trim() || undefined,
    onlyEnabled: exportForm.onlyEnabled
  })
}

onMounted(() => {
  refreshTree()

  // 组织层级的中文名走码表（ORG_TYPE）。之前是共享包里一份手写的
  // `OrgTypeNames`，与码表各写一遍 —— 改了一边另一边不会报错。
  // 失败不阻塞：回落到显示码值本身（见模板里的 `?? data.orgType`）。
  void orgTypeCodes.load()
})</script>

<template>
  <div class="org">
    <el-row :gutter="16">
      <!-- ---------------- 左：组织树 ---------------- -->
      <el-col :span="9">
        <el-card shadow="never" class="org__tree-card">
          <template #header>
            <div class="org__header">
              <span>组织树</span>
              <div>
                <el-button size="small" @click="reload">
                  <AppIcon name="Refresh" />
                  <span>刷新</span>
                </el-button>
                <!--
                  导入 / 导出按权限码显隐，与后端一致：
                  导入要 `Sys.Org.Import`（三个接口都是），导出要 `Sys.Org.Export`
                  （`ExportTaskPermissions.GetRequiredPermissions`）。
                  少了这层判断，按钮点得动而接口回 403 —— 那是最难解释的失败。
                -->
                <el-button v-if="canImport" size="small" plain @click="openImport">
                  <AppIcon name="Upload" />
                  <span>导入</span>
                </el-button>
                <!-- 「导出」直接提交任务（默认导出全部组织），「按条件导出」才开筛选对话框：
                     最常用的就是"把名录整份导出来"，不该为此多点一次对话框。 -->
                <el-button
                  v-if="canExport"
                  size="small"
                  plain
                  :loading="exporting"
                  @click="exportAll"
                >
                  <AppIcon name="Download" />
                  <span>导出</span>
                </el-button>
                <el-button v-if="canExport" size="small" plain :disabled="exporting" @click="openExport">
                  <AppIcon name="Filter" />
                  <span>按条件导出</span>
                </el-button>
                <el-button
                  v-if="canCreate"
                  size="small"
                  type="primary"
                  :disabled="!canAddChild"
                  @click="openCreate"
                >
                  <AppIcon name="Plus" />
                  <span>{{ !selected ? '新增省厅' : canAddChild ? '新增下级' : '学校无下级' }}</span>
                </el-button>
              </div>
            </div>
          </template>

          <el-alert
            type="info"
            :closable="false"
            show-icon
            class="org__tip"
            title="逐层加载：展开哪个节点才去取它的下级。全省上万所学校时，一次性拉整棵树会明显卡顿。"
          />

          <!-- 导出要等后台生成（上万所学校可能几十秒），只说"转圈"用户不知道卡在哪一步 -->
          <div v-if="exportProgressText" class="org__progress">{{ exportProgressText }}</div>

          <el-tree
            :key="treeKey"
            lazy
            node-key="id"
            :load="loadNode"
            :props="{ label: 'name', isLeaf: 'leaf' }"
            highlight-current
            :expand-on-click-node="false"
            @node-click="selectNode"
          >
            <template #default="{ data }">
              <span class="org__node">
                <el-tag size="small" effect="plain" :type="data.orgType === OrgTypes.School ? 'success' : 'info'">
                  {{ orgTypeCodes.nameOf(data.orgType) ?? data.orgType }}
                </el-tag>
                <span>{{ data.name }}</span>
                <el-tag v-if="!data.isEnabled" size="small" type="danger" effect="plain">停用</el-tag>
              </span>
            </template>
          </el-tree>
        </el-card>
      </el-col>

      <!-- ---------------- 右：详情表单 ---------------- -->
      <el-col :span="15">
        <el-card v-loading="loadingDetail" shadow="never">
          <template #header>
            <div class="org__header">
              <span>{{ selected ? `编辑：${selected.name}` : '组织详情' }}</span>
              <div v-if="selected">
                <el-button v-if="canUpdate" size="small" @click="openMove">移动到…</el-button>
                <el-button v-if="canDelete" size="small" type="danger" plain @click="remove">删除</el-button>
                <el-button v-if="canUpdate" size="small" type="primary" @click="save">保存</el-button>
              </div>
            </div>
          </template>

          <el-empty
            v-if="!selected"
            description="请在左侧选择一个组织节点"
            :image-size="80"
          />

          <template v-else>
            <el-descriptions :column="3" size="small" border class="org__meta">
              <el-descriptions-item label="上级">{{ parentName }}</el-descriptions-item>
              <el-descriptions-item label="层级">
                {{ orgTypeCodes.nameOf(selected.orgType) ?? selected.orgType }}
              </el-descriptions-item>
              <el-descriptions-item label="物化路径">
                <span class="org__path">{{ selected.pathCode || '—' }}</span>
              </el-descriptions-item>
            </el-descriptions>

            <el-form label-width="110px" class="org__form">
              <el-row :gutter="16">
                <el-col :span="12">
                  <el-form-item label="组织编码" required>
                    <el-input v-model="form.code" placeholder="学校填 10 位学校标识码" />
                    <div class="org__hint">
                      学校的编码同时是学籍辅号的前 10 位，务必与教育事业统计口径一致。
                    </div>
                  </el-form-item>
                </el-col>
                <el-col :span="12">
                  <el-form-item label="组织名称" required>
                    <el-input v-model="form.name" />
                  </el-form-item>
                </el-col>
              </el-row>

              <el-row :gutter="16">
                <el-col :span="12">
                  <el-form-item label="简称">
                    <el-input v-model="form.shortName" />
                  </el-form-item>
                </el-col>
                <el-col :span="6">
                  <el-form-item label="排序">
                    <el-input-number v-model="form.sort" :min="0" style="width: 100%" />
                  </el-form-item>
                </el-col>
                <el-col :span="6">
                  <el-form-item label="启用">
                    <el-switch v-model="form.isEnabled" />
                  </el-form-item>
                </el-col>
              </el-row>

              <el-form-item label="备注">
                <el-input v-model="form.remark" type="textarea" :rows="3" />
              </el-form-item>
            </el-form>
          </template>
        </el-card>
      </el-col>
    </el-row>

    <!-- ---------------- 新增对话框 ---------------- -->
    <el-dialog v-model="createVisible" title="新增组织" width="600px">
      <el-form label-width="110px">
        <el-form-item label="上级组织">
          <el-input :model-value="createForm.parentName" disabled />
        </el-form-item>

        <el-form-item label="组织类型">
          <el-radio-group v-model="createForm.orgType" :disabled="!!createForm.parentId">
            <el-radio-button
              v-for="t in orgTypeCodes.options.value"
              :key="t.value"
              :value="t.value"
            >
              {{ t.label }}
            </el-radio-button>
          </el-radio-group>
          <div v-if="createForm.parentId" class="org__hint">
            类型由上级自动推断（{{ orgTypeCodes.nameOf(createForm.orgType) ?? createForm.orgType }}），一般不需要改。
          </div>
        </el-form-item>

        <el-form-item label="组织编码" required>
          <el-input v-model="createForm.code" />
        </el-form-item>

        <el-form-item label="组织名称" required>
          <el-input v-model="createForm.name" />
        </el-form-item>

        <el-form-item label="简称">
          <el-input v-model="createForm.shortName" />
        </el-form-item>

        <el-form-item label="排序">
          <el-input-number v-model="createForm.sort" :min="0" />
        </el-form-item>

        <el-form-item label="备注">
          <el-input v-model="createForm.remark" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="createVisible = false">取消</el-button>
        <el-button type="primary" :loading="creating" @click="doCreate">创建</el-button>
      </template>
    </el-dialog>

    <!-- ---------------- 批量导入（省 / 市 / 县 / 校） ---------------- -->
    <el-dialog v-model="importVisible" title="批量导入组织" width="820px" top="6vh">
      <el-alert
        type="info"
        :closable="false"
        show-icon
        class="org__tip"
        title="导入与手工新增走完全相同的校验（编码唯一、层级必须是省 → 市 → 县 → 校），并且逐行容错：个别行出错不影响其它行，失败明细里给出行号与原因。"
      />

      <!--
        两条要求都写在界面上、而不是只在失败时弹一次消息：
          · **编码**：Excel「另存为 CSV」默认是 GBK，是本功能唯一的高频失败原因，
            用户第一次就会被挡下，之后每次回来都该看得见怎么另存；
          · **10 位学校标识码**：学校编码同时是学籍辅号的前 10 位，
            而导入是一次写定的 —— 填错要改的不只是一行组织数据。
        这两句与「列说明」里的内容重复是**有意的**：用户通常是先点"选择文件"，
        不会主动展开列说明。
      -->
      <el-alert
        type="warning"
        :closable="false"
        show-icon
        class="org__tip"
        title="文件必须是 UTF-8 编码。Excel 的「另存为 CSV」默认用本机编码（简体中文为 GBK），中文会变成乱码并被拒绝 —— 请选择「CSV UTF-8（逗号分隔）」。学校行的「组织编码」必须填 10 位学校标识码（学籍辅号的前 10 位就是它）。"
      />

      <el-space wrap class="org__import-actions">
        <el-button @click="downloadTemplate">
          <AppIcon name="Download" />
          <span>下载模板</span>
        </el-button>

        <el-button type="primary" :loading="importing" @click="pickFile">
          <AppIcon name="Upload" />
          <span>选择 CSV 文件并导入</span>
        </el-button>

        <span v-if="importFileName" class="org__hint">{{ importFileName }}</span>

        <el-tag v-if="encodingNote" size="small" effect="plain">编码：{{ encodingNote }}</el-tag>

        <input ref="fileInput" type="file" accept=".csv,text/csv" hidden @change="doImport" />
      </el-space>

      <!--
        列说明**全部来自后端 `getColumns()`**，前端不写死一份类型清单：
        导入模板的「组织类型」列在后端由 `OrgTypes.TryParse` 解析，接受中文别名，
        前端照抄一份清单就会在"后端改别名"时先把合法文件拦下。
      -->
      <el-collapse v-if="importColumns.length > 0" class="org__columns">
        <el-collapse-item :title="`列说明（共 ${importColumns.length} 列，按表头名匹配，列顺序可调）`">
          <el-table :data="importColumns" size="small" border>
            <el-table-column prop="name" label="列名" width="120" />
            <el-table-column label="必需" width="70" align="center">
              <template #default="{ row }">
                <el-tag v-if="row.required" size="small" type="danger" effect="plain">必需</el-tag>
                <span v-else class="org__hint">可选</span>
              </template>
            </el-table-column>
            <el-table-column prop="remark" label="说明" min-width="280" />
          </el-table>
        </el-collapse-item>
      </el-collapse>

      <template v-if="importResult">
        <el-alert
          :type="importResult.failed === 0 ? 'success' : 'warning'"
          :closable="false"
          show-icon
          class="org__tip"
          :title="importResult.message"
        />

        <!-- 逐行明细必须展示：只说"导入失败"，用户就得拿着几百行文件自己找问题 -->
        <el-table
          v-if="importResult.errors.length > 0"
          :data="importResult.errors"
          size="small"
          border
          max-height="300"
        >
          <el-table-column prop="rowIndex" label="行号" width="70" align="center" />
          <el-table-column label="机构" min-width="220">
            <template #default="{ row }">
              <!--
                组织类型**原样回显**，不查码表翻译：这一列装的可能是用户写错的
                「学挍」，翻译过的名字会与他自己文件里的那一行对不上 ——
                而这张表存在的唯一意义就是让他照着改文件。
              -->
              <el-tag v-if="row.orgType" size="small" effect="plain" type="info">
                {{ row.orgType }}
              </el-tag>
              <span>{{ row.name }}</span>
            </template>
          </el-table-column>
          <el-table-column prop="code" label="组织编码" width="130" />
          <el-table-column prop="message" label="失败原因" min-width="260" />
        </el-table>
      </template>

      <template #footer>
        <el-button @click="importVisible = false">关闭</el-button>
      </template>
    </el-dialog>

    <!-- ---------------- 按条件导出（后台任务） ---------------- -->
    <el-dialog v-model="exportVisible" title="按条件导出组织名单" width="560px">
      <el-alert
        type="info"
        :closable="false"
        show-icon
        class="org__tip"
        title="导出列与导入模板完全一致，因此可以直接改一改再导入，也可以用来换一套环境重建组织树。能看到哪些组织由当前账号的数据范围决定。"
      />

      <el-form label-width="130px">
        <el-form-item label="组织类型">
          <!-- 选项来自码表（ORG_TYPE），不写死 Province / City / County / School -->
          <el-select v-model="exportForm.orgType" clearable placeholder="全部" style="width: 100%">
            <el-option
              v-for="t in orgTypeCodes.options.value"
              :key="t.value"
              :label="t.label"
              :value="t.value"
            />
          </el-select>
        </el-form-item>

        <el-form-item label="关键字">
          <el-input
            v-model="exportForm.keyword"
            placeholder="组织名称 / 组织编码，留空为全部"
            clearable
            @keyup.enter="exportFiltered"
          />
        </el-form-item>

        <el-form-item label="只导出启用的">
          <el-switch v-model="exportForm.onlyEnabled" />
          <div class="org__hint">
            默认不勾选，即连同已停用的组织一起导出 —— 换环境重建组织树时需要它们，
            否则停用过的组织会凭空少掉。勾上则只导出启用的（后端的默认口径）。
          </div>
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="exportVisible = false">取消</el-button>
        <el-button type="primary" :loading="exporting" @click="exportFiltered">导出</el-button>
      </template>
    </el-dialog>

    <!-- ---------------- 移动到…（改上级） ---------------- -->
    <el-dialog v-model="moveVisible" title="移动到…" width="620px" top="6vh" destroy-on-close>
      <el-alert
        type="warning"
        :closable="false"
        show-icon
        class="org__tip"
        title="移动会级联重写整棵子树的物化路径（层级、数据范围、学籍辅号前缀都由它推导）。子树里的学校与学生数据范围会随之改变，请确认影响范围后再操作。"
      />

      <el-form label-width="110px">
        <el-form-item label="当前组织">
          <el-input
            :model-value="selected ? `${selected.name}（${orgTypeCodes.nameOf(selected.orgType) ?? selected.orgType}）` : ''"
            disabled
          />
        </el-form-item>

        <el-form-item label="新上级">
          <div class="org__picker">
            <div class="org__picked">已选：{{ moveTargetName }}</div>

            <el-tree
              ref="moveTreeRef"
              :key="moveTreeKey"
              lazy
              node-key="id"
              :load="loadMoveNode"
              :props="{ label: 'name', isLeaf: 'leaf', disabled: 'disabled' }"
              highlight-current
              :expand-on-click-node="false"
              @current-change="onMoveCurrentChange"
            >
              <template #default="{ data }">
                <span class="org__node">
                  <el-tag
                    v-if="!data.isRootOption"
                    size="small"
                    effect="plain"
                    :type="data.orgType === OrgTypes.School ? 'success' : 'info'"
                  >
                    {{ orgTypeCodes.nameOf(data.orgType) ?? data.orgType }}
                  </el-tag>
                  <span>{{ data.name }}</span>
                  <span v-if="data.disabledReason" class="org__hint">{{ data.disabledReason }}</span>
                </span>
              </template>
            </el-tree>

            <div class="org__hint">
              灰色的节点不可选：它自己与它自己的下级（会形成环），以及上下层级不匹配的节点
              （省 → 市 → 县 → 校）。
            </div>
          </div>
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="moveVisible = false">取消</el-button>
        <el-button
          type="primary"
          :loading="moving"
          :disabled="moveTargetId === undefined"
          @click="doMove"
        >
          确定移动
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.org__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.org__tree-card :deep(.el-card__body) {
  max-height: calc(100vh - 220px);
  overflow: auto;
}

.org__tip {
  margin-bottom: 12px;
}

/* 导出任务的进行状态（排队中 / 已处理 N / M（x%） / 正在生成） */
.org__progress {
  margin-bottom: 12px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.org__import-actions {
  margin-bottom: 12px;
}

.org__columns {
  margin-bottom: 12px;
}

.org__node {
  display: flex;
  align-items: center;
  gap: 6px;
}

.org__meta {
  margin-bottom: 16px;
}

.org__path {
  font-family: Consolas, Monaco, monospace;
  font-size: 12px;
}

.org__hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.5;
}

/* 移动对话框里的"选新上级"树：给一个固定高度，否则展开几层后
   底部的确定按钮会被挤到视口外 */
.org__picker {
  width: 100%;
}

.org__picked {
  margin-bottom: 8px;
  font-size: 13px;
}

.org__picker :deep(.el-tree) {
  max-height: 300px;
  overflow: auto;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 4px;
  padding: 6px;
}
</style>
