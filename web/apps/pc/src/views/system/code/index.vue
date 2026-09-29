<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import {
  codeApi,
  codeImportApi,
  describeExportProgress,
  describeTruncation,
  exportTaskApi,
  saveBlob
} from '@/api'
import type {
  CodeCategory,
  CodeExportQuery,
  CodeItem,
  ExportTask,
  ImportCodesResult,
  ImportColumn
} from '@/api/types'
import { ExportTaskKinds } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'
import { probeCsvEncoding } from '@/utils/csvEncoding'

/**
 * 微代码（码表）管理。
 *
 * 学籍系统里有大量国标码值（性别、民族、政治面貌、身份证件类型、学籍状态…），
 * 它们的共同点是：**会被业务页面频繁读取，但很少变化**。因此：
 *   - 查询接口对所有登录用户开放（下拉框都要用），写操作才要权限；
 *   - `cacheable` 的码表走后端缓存，改完码值需要失效缓存（本页提供手动入口）。
 *
 * 两处界面上的判断：
 *   1) **系统内置码表（isSystem）不给改** —— 服务端本来就会拒绝，
 *      界面上直接禁用比让用户点了再报错体验好；
 *   2) **树形码表按树渲染** —— `isTree` 为真时用 el-table 的树形数据展示，
 *      否则学籍系统的行政区划这类层级码表会全部平铺，根本看不出从属关系。
 */
const auth = useAuthStore()

const canCreate = computed(() => auth.can(MetadataPermissions.Sys.Code.Create))
const canUpdate = computed(() => auth.can(MetadataPermissions.Sys.Code.Update))
const canDelete = computed(() => auth.can(MetadataPermissions.Sys.Code.Delete))

/**
 * 导入 / 导出各自一个权限码（后端 `CodeImportExportAppService` 的三个接口都用
 * `Sys.Code.Import`；导出走 `ExportTaskKinds.CodeCsv`，要求 `Sys.Code.Export`，
 * 见 `ExportTaskPermissions.GetRequiredPermissions`）。
 *
 * 为什么不像本页其它按钮那样只挂在 Create / Update 上：码表是**参考数据**，
 * 能被别处引用的值 —— 而且它还是"环境搬运"的对象。能改一条码值
 * 不等于可以把整表灌进来，也不等于可以把全部码表落盘带走。
 * 少了这层判断，按钮点得动而接口回 403 —— 那是最难向用户解释的失败。
 */
const canImport = computed(() => auth.can(MetadataPermissions.Sys.Code.Import))
const canExport = computed(() => auth.can(MetadataPermissions.Sys.Code.Export))

const loading = ref(false)
const categories = ref<CodeCategory[]>([])
const keyword = ref('')
const selected = ref<CodeCategory | null>(null)

const itemsLoading = ref(false)
const items = ref<CodeItem[]>([])

async function loadCategories(): Promise<void> {
  loading.value = true

  try {
    categories.value = await codeApi.getCategories(keyword.value.trim() || undefined)

    // 保持选中项（重新加载后对象会变），否则右侧会忽然空掉
    if (selected.value) {
      selected.value = categories.value.find((c) => c.id === selected.value!.id) ?? null
    }
  } finally {
    loading.value = false
  }
}

async function selectCategory(category: CodeCategory): Promise<void> {
  selected.value = category
  itemsLoading.value = true

  try {
    // 树形码表要求后端组装成树，平铺表格看不出层级
    items.value = await codeApi.getItems(category.id, false, category.isTree)
  } finally {
    itemsLoading.value = false
  }
}

/**
 * 重新取左右两栏的数据（类别列表 + 当前类别的码项）。
 *
 * 刻意复用页面上已有的两个取数函数，而不是另写一套查询：两套查询迟早会在
 * "要不要带 keyword、要不要带 onlyEnabled"上不一致 —— 那种不一致的表现是
 * "刷新之后列表和刚才是两回事"，排查起来毫无线索。
 *
 * 注意它**不碰后端缓存**：缓存失效在 `refreshAfterImport` 里单独交代
 * （本页的手工写入路径同样不刷缓存，因为后端写操作已自行失效）。
 */
async function reload(): Promise<void> {
  await loadCategories()

  // loadCategories 已按 Id 把 selected 换成新对象（对象变了，右侧表格要靠它重建）
  const target = selected.value ?? categories.value[0]

  if (target) {
    await selectCategory(target)
  } else {
    // 一个类别都没有（例如搜索无结果）：右侧要清空，否则会留着上一次的码项
    selected.value = null
    items.value = []
  }
}

/** 系统内置码表不允许改（服务端也会拒绝，这里提前拦住）。 */
const readonly = computed(() => selected.value?.isSystem ?? false)

// ---------------------------------------------------------------------------
// 码类别
// ---------------------------------------------------------------------------

const catDialogVisible = ref(false)
const catSaving = ref(false)
const editingCatId = ref<string | null>(null)

const catForm = reactive({
  code: '',
  name: '',
  stdSource: '',
  isTree: false,
  isEnabled: true,
  cacheable: true,
  sort: 0,
  remark: ''
})

function openCategory(row?: CodeCategory): void {
  editingCatId.value = row?.id ?? null
  catForm.code = row?.code ?? ''
  catForm.name = row?.name ?? ''
  catForm.stdSource = row?.stdSource ?? ''
  catForm.isTree = row?.isTree ?? false
  catForm.isEnabled = row?.isEnabled ?? true
  catForm.cacheable = row?.cacheable ?? true
  catForm.sort = row?.sort ?? 0
  catForm.remark = row?.remark ?? ''
  catDialogVisible.value = true
}

async function saveCategory(): Promise<void> {
  if (!catForm.code.trim() || !catForm.name.trim()) {
    ElMessage.warning('编码与名称都不能为空。')

    return
  }

  catSaving.value = true

  try {
    const payload = {
      code: catForm.code.trim(),
      name: catForm.name.trim(),
      stdSource: catForm.stdSource?.trim() || null,
      isTree: catForm.isTree,
      isEnabled: catForm.isEnabled,
      cacheable: catForm.cacheable,
      sort: catForm.sort,
      remark: catForm.remark?.trim() || null
    }

    if (editingCatId.value) {
      await codeApi.updateCategory(editingCatId.value, payload)
      ElMessage.success('码类别已更新')
    } else {
      await codeApi.createCategory(payload)
      ElMessage.success('码类别已创建')
    }

    catDialogVisible.value = false
    await loadCategories()
  } finally {
    catSaving.value = false
  }
}

async function removeCategory(row: CodeCategory): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定删除码类别「${row.name}」吗？其下 ${row.itemCount} 条码项会一并处理，` +
        '已被业务数据引用的码值删除后会导致历史数据无法翻译。',
      '删除确认',
      { type: 'warning' }
    )
  } catch {
    return
  }

  await codeApi.removeCategory(row.id)
  ElMessage.success('已删除')

  if (selected.value?.id === row.id) {
    selected.value = null
    items.value = []
  }

  await loadCategories()
}

// ---------------------------------------------------------------------------
// 码项
// ---------------------------------------------------------------------------

const itemDialogVisible = ref(false)
const itemSaving = ref(false)
const editingItemId = ref<string | null>(null)

const itemForm = reactive({
  code: '',
  name: '',
  stdCode: '',
  sort: 0,
  isEnabled: true,
  remark: ''
})

function openItem(row?: CodeItem): void {
  editingItemId.value = row?.id ?? null
  itemForm.code = row?.code ?? ''
  itemForm.name = row?.name ?? ''
  itemForm.stdCode = row?.stdCode ?? ''
  itemForm.sort = row?.sort ?? 0
  itemForm.isEnabled = row?.isEnabled ?? true
  itemForm.remark = row?.remark ?? ''
  itemDialogVisible.value = true
}

async function saveItem(): Promise<void> {
  if (!selected.value) {
    return
  }

  if (!itemForm.code.trim() || !itemForm.name.trim()) {
    ElMessage.warning('码值与名称都不能为空。')

    return
  }

  itemSaving.value = true

  try {
    const payload = {
      categoryId: selected.value.id,
      code: itemForm.code.trim(),
      name: itemForm.name.trim(),
      parentId: null,
      stdCode: itemForm.stdCode?.trim() || null,
      sort: itemForm.sort,
      isEnabled: itemForm.isEnabled,
      extJson: null,
      remark: itemForm.remark?.trim() || null
    }

    if (editingItemId.value) {
      await codeApi.updateItem(editingItemId.value, payload)
      ElMessage.success('码项已更新')
    } else {
      await codeApi.createItem(payload)
      ElMessage.success('码项已创建')
    }

    itemDialogVisible.value = false
    await Promise.all([selectCategory(selected.value), loadCategories()])
  } finally {
    itemSaving.value = false
  }
}

async function removeItem(row: CodeItem): Promise<void> {
  try {
    await ElMessageBox.confirm(`确定删除码值「${row.name}（${row.code}）」吗？`, '删除确认', {
      type: 'warning'
    })
  } catch {
    return
  }

  await codeApi.removeItem(row.id)
  ElMessage.success('已删除')

  if (selected.value) {
    await Promise.all([selectCategory(selected.value), loadCategories()])
  }
}

async function refreshCache(): Promise<void> {
  await codeApi.refreshCache()
  ElMessage.success('码表缓存已刷新')
}

// ---------------------------------------------------------------------------
// 导入（码表 / 码项）
// ---------------------------------------------------------------------------

/**
 * 码表用 CSV 导入，是**环境搬运**的主要方式：测试环境调好的码项要搬到生产、
 * 生产新增的码项要同步回测试，靠界面一条条点必然漏 —— 而"少一个码值"的表现
 * 只是某处下拉框少一个选项，要等业务人员报"这里选不到"才会被发现。
 *
 * 这里刻意与组织导入对话框长得一样，理由不是"统一风格"，而是**同一批坑**：
 *   1) 必须先把文件按字节探一次编码，否则 GBK 文件会被解码成乱码送去后端，
 *      换回一句"缺少必需列"——用户会去反复检查列名；
 *   2) 失败明细必须逐行展示（`rowIndex` 是 Excel 行号），
 *      只弹一句"导入失败"等于让用户拿几百行数据自己找问题。
 *
 * ⚠ 文件里**一行 = 一个码项**，类别编码与类别名称在该类别的每一行里**重复写**
 *   （CSV 表达不了嵌套，后端的模板也是这么给的，见 index.ts 里 codeImportApi 的说明）。
 */
const importVisible = ref(false)
const importing = ref(false)
const importColumns = ref<ImportColumn[]>([])
const importResult = ref<ImportCodesResult | null>(null)
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
      importColumns.value = await codeImportApi.getColumns()
    } catch {
      // 拦截器已提示
      importColumns.value = []
    }
  }
}

async function downloadTemplate(): Promise<void> {
  try {
    const csv = await codeImportApi.getTemplate()

    // 模板里的 BOM 由后端 `Csv.WriteForExcel` 加上（Excel 不认无 BOM 的 UTF-8），
    // 这里**不再自己补一个**：补两次会在文件开头多出一个空字符/空行。
    saveBlob(new Blob([csv], { type: 'text/csv;charset=utf-8' }), '微代码导入模板.csv')
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
    // ⚠ 先按**字节**探测编码，再决定要不要送给后端（与组织/学生导入同一处坑）。
    //
    // 直接用 `file.text()` 是错的：它按 UTF-8 解码，GBK 文件会变成一串替换字符，
    // 于是后端只回一句「导入文件缺少必需列：类别编码、类别名称…」——
    // 用户看到的提示与真正原因（编码）毫无关系，会去反复检查列名。
    const probe = probeCsvEncoding(await file.arrayBuffer())

    encodingNote.value = probe.detected

    if (!probe.ok) {
      // 明确拒绝并给出可执行的做法，而不是把乱码送去后端换回一句莫名其妙的报错
      ElMessage.error(probe.reason ?? '文件编码不是 UTF-8，无法导入。')

      return
    }

    importResult.value = await codeImportApi.import(probe.text)

    // 后端的 message 里带**新建类别数**（码表导入会顺带建类别），原样透出
    if (importResult.value.failed === 0) {
      ElMessage.success(importResult.value.message)
    } else {
      ElMessage.warning(importResult.value.message)
    }

    // 部分成功也要刷新：成功的那部分**已经落库**了
    if (importResult.value.succeeded > 0) {
      await refreshAfterImport()
    }
  } catch {
    // 错误提示由 http 拦截器统一弹出（含后端 reason，如"同类别下码值已存在"），
    // 这里**不重复弹**：同一个失败弹两次，用户会以为是两个不同的问题。
  } finally {
    importing.value = false

    // 必须清空：否则连续选同一个文件不会再触发 change，用户会以为"点了没反应"
    input.value = ''
  }
}

/**
 * 导入成功后的收尾：失效码表缓存 → 重新取本页两栏数据。
 *
 * 【缓存这块实际查到的情况（也回答了"本页 reload 要不要负责失效"）】
 *   导入内部逐行复用 `CodeAppService.CreateItemAsync`，而那个方法**每写一条就调一次
 *   `RemoveLookupCacheAsync(category.Code)`**，所以"码项写进去了、后端缓存还是旧的"
 *   这一路本来就不会发生；本页的手工写入路径（saveItem / removeItem）也正因此从不刷缓存。
 *   真正的缺口在**新建类别**这一路：`CreateCategoryAsync` **没有任何失效动作**，
 *   而导入恰好是"先建类别（Cacheable 默认 true）、再往里写码项"的组合 ——
 *   这里补一次 `refreshCache`（后端把所有 cacheable 类别逐条失效，幂等、只一个请求）
 *   作为兜底：它覆盖不到的那种"某处下拉框少一个选项"的静默不一致，
 *   是只能靠人工才能发现的。
 *
 * ⚠ 它管不到**前端** `useCodeOptions` 的模块级内存缓存（那是各业务页面自己持有的，
 *   只有整页刷新或调用它的 `refresh()` 才会更新）。本页够不着那份缓存 ——
 *   这是既有问题（手工新增码值同样如此），不在本次改动范围内。
 */
async function refreshAfterImport(): Promise<void> {
  try {
    await codeApi.refreshCache()
  } catch {
    // 拦截器已提示。缓存失效是兜底动作，**不能**因为它的失败把下面的列表刷新一起带走 ——
    // 码项已经落库了，用户马上要看到的是刷新后的列表。
  }

  await reload()
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
  /**
   * 类别：值是**类别编码**（后端 `CodeExportQueryDto.CategoryCode`），不是类别 Id。
   * 因此这里存的是 `category.code`，而选项来自后端（见 loadExportCategories）。
   */
  categoryCode: undefined as string | undefined,
  keyword: '',
  /**
   * 只导出启用的码项。
   *
   * 默认**关**（= 连已停用的一起导出）：与后端 `CodeExportQueryDto.OnlyEnabled`
   * 的默认值一致，也与这个功能的用途一致 —— 导出是"搬运/备份"，
   * 漏掉停用的那几条只会在某处下拉框少一个选项时才发现。
   */
  onlyEnabled: false
})

/** 导出对话框里类别下拉的选项（全量、不受左侧搜索框影响）。 */
const exportCategoryOptions = ref<CodeCategory[]>([])

/**
 * 取导出对话框的类别选项。
 *
 * ⚠ 选项**来自后端**（`codeApi.getCategories()`，与本页左侧表格同一个接口），
 *   前端不写死任何类别编码或码值：写死一份的下场是"后端新增了类别，
 *   导出对话框里选不到"，而且不会报任何错。
 *
 * 这里**刻意不带 keyword** 再取一次全量：左侧列表会被搜索框过滤，
 * 直接拿它当选项就会变成"搜过什么就只能导出什么"——那种静默限制
 * 用户根本想不到。取失败也不阻塞：回落到左侧已加载的列表（拦截器已提示）。
 */
async function loadExportCategories(): Promise<void> {
  try {
    exportCategoryOptions.value = await codeApi.getCategories()
  } catch {
    // 拦截器已提示
    exportCategoryOptions.value = categories.value
  }
}

function openExport(): void {
  exportForm.categoryCode = undefined
  exportForm.keyword = ''
  exportForm.onlyEnabled = false
  exportVisible.value = true

  void loadExportCategories()
}

/**
 * 提交导出任务并等它完成，然后下载（两个入口共用这一条路径）。
 *
 * 码表导出走**后台任务**，与组织名单、学生名单、照片打包同一条链路：
 * 提交 → 轮询 → 下载 → 提示。同步在请求线程里拼几千行 CSV 会让浏览器先超时、
 * 服务端还在跑 —— 用户看到"失败"、文件其实已生成，他再点一次，于是白干两遍。
 */
async function submitExport(codeQuery: CodeExportQuery): Promise<void> {
  exporting.value = true
  exportProgressText.value = '排队中…'

  try {
    const task = await exportTaskApi.run(
      { kind: ExportTaskKinds.CodeCsv, codeQuery },
      {
        shouldStop: () => disposed,
        onProgress: (t: ExportTask) => {
          // 微代码导出结构上给不出百分比（一次性序列化），`describeExportProgress`
          // 会如实退回"正在生成…"而不是显示一个卡在 0% 的进度。
          exportProgressText.value = describeExportProgress(t)
        }
      }
    )

    const blob = await exportTaskApi.download(task.id)

    // 文件名以**后端给的**为准（带了日期等有用信息），后端没给才用兜底名
    saveBlob(blob, task.fileName || `微代码_${new Date().toISOString().slice(0, 10)}.csv`)

    // 截断必须显式提示。码表导出**永远不截断**（见 ExportTaskKinds 的说明），
    // 但仍然照常走这一步：这是共用约定，而且真触到上限时更不能静默少几条码值。
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
 * 一键导出：不带任何筛选，导出**全部码表（含已停用的码项）**。
 *
 * ⚠ 这里显式写 `onlyEnabled: false` 不是为了补一个会被漏掉的字段
 *   （后端 `CodeExportQueryDto.OnlyEnabled` 默认**就是** false），
 *   而是为了让读代码的人不必去翻 DTO —— 注意它与**组织导出正好相反**
 *   （`OrgQueryDto.OnlyEnabled` 默认 true，那边不写 false 就会少一半数据）：
 *   照抄组织那段注释与写法，会把人带到"以为这里也得显式传"的思路上。
 */
async function exportAll(): Promise<void> {
  await submitExport({ onlyEnabled: false })
}

/** 按条件导出（对话框里的类别 / 关键字 / 是否含停用码项）。 */
async function exportFiltered(): Promise<void> {
  await submitExport({
    categoryCode: exportForm.categoryCode,
    keyword: exportForm.keyword.trim() || undefined,
    onlyEnabled: exportForm.onlyEnabled
  })
}

onMounted(async () => {
  await loadCategories()

  if (categories.value.length > 0) {
    await selectCategory(categories.value[0])
  }
})
</script>

<template>
  <div class="code">
    <el-row :gutter="16">
      <!-- ---------------- 左：码类别 ---------------- -->
      <el-col :span="9">
        <el-card shadow="never" class="code__cat-card">
          <template #header>
            <div class="code__header">
              <span>码类别（{{ categories.length }}）</span>
              <div>
                <el-button size="small" @click="refreshCache">刷新缓存</el-button>
                <!--
                  导入 / 导出按权限码显隐，与后端一致：
                  导入的三个接口都要求 `Sys.Code.Import`，导出要求 `Sys.Code.Export`
                  （`ExportTaskPermissions.GetRequiredPermissions`）。
                  少了这层判断，按钮点得动而接口回 403 —— 那是最难解释的失败。
                -->
                <el-button v-if="canImport" size="small" plain @click="openImport">
                  <AppIcon name="Upload" />
                  <span>导入</span>
                </el-button>
                <!-- 「导出」直接提交任务（默认导出全部码表），「按条件导出」才开筛选对话框：
                     最常用的就是"把码表整份导出来"，不该为此多点一次对话框。 -->
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
                <el-button
                  v-if="canExport"
                  size="small"
                  plain
                  :disabled="exporting"
                  @click="openExport"
                >
                  <AppIcon name="Filter" />
                  <span>按条件导出</span>
                </el-button>
                <el-button v-if="canCreate" size="small" type="primary" @click="openCategory()">
                  <AppIcon name="Plus" />
                  <span>新增</span>
                </el-button>
              </div>
            </div>
          </template>

          <!-- 导出要等后台生成，只说"转圈"用户不知道卡在哪一步 -->
          <div v-if="exportProgressText" class="code__progress">{{ exportProgressText }}</div>

          <el-input
            v-model="keyword"
            placeholder="搜索编码 / 名称"
            clearable
            class="code__search"
            @keyup.enter="loadCategories"
          >
            <template #prefix>
              <AppIcon name="Search" />
            </template>
          </el-input>

          <el-alert
            type="info"
            :closable="false"
            show-icon
            class="code__tip"
            title="标记「内置」的码表由国标/教育行业标准定义，服务端不允许修改。"
          />

          <el-table
            v-loading="loading"
            :data="categories"
            size="small"
            highlight-current-row
            height="calc(100vh - 330px)"
            @row-click="selectCategory"
          >
            <el-table-column prop="name" label="名称" min-width="130" show-overflow-tooltip />
            <el-table-column prop="code" label="编码" width="110" />
            <el-table-column prop="itemCount" label="码值" width="60" align="center" />
            <el-table-column label="" width="70" align="center">
              <template #default="{ row }">
                <el-tag v-if="row.isSystem" size="small" type="info" effect="plain">内置</el-tag>
                <el-tag v-else-if="row.isTree" size="small" type="warning" effect="plain">树</el-tag>
              </template>
            </el-table-column>
            <el-table-column v-if="canUpdate" label="操作" width="92" align="center">
              <template #default="{ row }">
                <el-button link type="primary" :disabled="row.isSystem" @click.stop="openCategory(row)">
                  编辑
                </el-button>
                <el-button
                  v-if="canDelete"
                  link
                  type="danger"
                  :disabled="row.isSystem"
                  @click.stop="removeCategory(row)"
                >
                  删
                </el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>

      <!-- ---------------- 右：码项 ---------------- -->
      <el-col :span="15">
        <el-card shadow="never">
          <template #header>
            <div class="code__header">
              <span>
                码值
                <span v-if="selected" class="code__muted">
                  — {{ selected.name }}（{{ selected.code }}）
                  <template v-if="selected.stdSource">· 依据 {{ selected.stdSource }}</template>
                </span>
              </span>
              <div v-if="selected">
                <el-button
                  v-if="canCreate"
                  size="small"
                  type="primary"
                  :disabled="readonly"
                  @click="openItem()"
                >
                  <AppIcon name="Plus" />
                  <span>新增码值</span>
                </el-button>
              </div>
            </div>
          </template>

          <el-empty v-if="!selected" description="请在左侧选择一个码类别" :image-size="80" />

          <template v-else>
            <el-alert
              v-if="readonly"
              type="warning"
              :closable="false"
              show-icon
              class="code__tip"
              title="这是系统内置码表，只读。如需在业务上屏蔽某些码值，请停用对应码项而不是删除。"
            />

            <el-table
              v-loading="itemsLoading"
              :data="items"
              row-key="id"
              :tree-props="{ children: 'children' }"
              :default-expand-all="selected.isTree"
              border
              height="calc(100vh - 330px)"
            >
              <el-table-column prop="code" label="码值" width="130" />
              <el-table-column prop="name" label="名称" min-width="180" show-overflow-tooltip />
              <el-table-column prop="stdCode" label="标准码" width="120">
                <template #default="{ row }">{{ row.stdCode || '—' }}</template>
              </el-table-column>
              <el-table-column prop="sort" label="排序" width="70" align="center" />
              <el-table-column label="状态" width="80" align="center">
                <template #default="{ row }">
                  <el-tag size="small" :type="row.isEnabled ? 'success' : 'info'">
                    {{ row.isEnabled ? '启用' : '停用' }}
                  </el-tag>
                </template>
              </el-table-column>
              <el-table-column prop="remark" label="备注" min-width="140" show-overflow-tooltip />
              <el-table-column v-if="canUpdate" label="操作" width="110" align="center" fixed="right">
                <template #default="{ row }">
                  <el-button link type="primary" :disabled="readonly" @click="openItem(row)">编辑</el-button>
                  <el-button v-if="canDelete" link type="danger" :disabled="readonly" @click="removeItem(row)">
                    删除
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
          </template>
        </el-card>
      </el-col>
    </el-row>

    <!-- ---------------- 码类别编辑 ---------------- -->
    <el-dialog
      v-model="catDialogVisible"
      :title="editingCatId ? '编辑码类别' : '新增码类别'"
      width="560px"
    >
      <el-form label-width="110px">
        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="编码" required>
              <el-input v-model="catForm.code" :disabled="!!editingCatId" placeholder="如 NATION" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="名称" required>
              <el-input v-model="catForm.name" placeholder="如 民族码" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="标准依据">
          <el-input v-model="catForm.stdSource" placeholder="如 GB/T 3304" />
        </el-form-item>

        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="排序">
              <el-input-number v-model="catForm.sort" :min="0" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="属性">
              <el-checkbox v-model="catForm.isTree">树形码表</el-checkbox>
              <el-checkbox v-model="catForm.cacheable">可缓存</el-checkbox>
              <el-checkbox v-model="catForm.isEnabled">启用</el-checkbox>
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="备注">
          <el-input v-model="catForm.remark" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="catDialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="catSaving" @click="saveCategory">保存</el-button>
      </template>
    </el-dialog>

    <!-- ---------------- 码项编辑 ---------------- -->
    <el-dialog v-model="itemDialogVisible" :title="editingItemId ? '编辑码值' : '新增码值'" width="520px">
      <el-form label-width="100px">
        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="码值" required>
              <el-input v-model="itemForm.code" placeholder="如 01" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="名称" required>
              <el-input v-model="itemForm.name" placeholder="如 汉族" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="标准码">
              <el-input v-model="itemForm.stdCode" placeholder="国标原始码值" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="排序">
              <el-input-number v-model="itemForm.sort" :min="0" style="width: 100%" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="启用">
          <el-switch v-model="itemForm.isEnabled" />
          <span class="code__muted code__inline">停用后业务下拉框不再出现，但历史数据仍能翻译</span>
        </el-form-item>

        <el-form-item label="备注">
          <el-input v-model="itemForm.remark" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="itemDialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="itemSaving" @click="saveItem">保存</el-button>
      </template>
    </el-dialog>

    <!-- ---------------- 批量导入（码表 / 码项） ---------------- -->
    <el-dialog v-model="importVisible" title="批量导入微代码" width="820px" top="6vh">
      <el-alert
        type="info"
        :closable="false"
        show-icon
        class="code__tip"
        title="导入与手工新增走完全相同的校验（同一类别下码值唯一、必填项不能为空），并且逐行容错：个别行出错不影响其它行，失败明细里给出行号与原因。"
      />

      <!--
        这份文件唯一需要理解的约定写在界面上、而不是只在失败时弹一次消息：
        **一行 = 一个码项**，同一个类别的每一行都要重复写类别编码与类别名称
        （CSV 表达不了嵌套）。用户按"Excel 习惯"把类别合并成一格、
        或者第二行起留空，都会被后端逐行判为"类别编码与类别名称都不能为空"。
      -->
      <el-alert
        type="warning"
        :closable="false"
        show-icon
        class="code__tip"
        title="文件必须是 UTF-8 编码。Excel 的「另存为 CSV」默认用本机编码（简体中文为 GBK），中文会变成乱码并被拒绝 —— 请选择「CSV UTF-8（逗号分隔）」。另外：一行只能写一个码项，同一个类别的多行都要重复填写类别编码与类别名称。"
      />

      <el-space wrap class="code__import-actions">
        <el-button @click="downloadTemplate">
          <AppIcon name="Download" />
          <span>下载模板</span>
        </el-button>

        <el-button type="primary" :loading="importing" @click="pickFile">
          <AppIcon name="Upload" />
          <span>选择 CSV 文件并导入</span>
        </el-button>

        <span v-if="importFileName" class="code__muted">{{ importFileName }}</span>

        <el-tag v-if="encodingNote" size="small" effect="plain">编码：{{ encodingNote }}</el-tag>

        <input ref="fileInput" type="file" accept=".csv,text/csv" hidden @change="doImport" />
      </el-space>

      <!--
        列说明**全部来自后端 `getColumns()`**，前端不写死一份清单：
        列名（类别编码 / 类别名称 / 码值 / 码名称 / 排序 / 是否启用）与必需性
        以后端为准 —— 前端照抄一份，就会在后端调整列名或必需性时
        先把合法文件拦下（或漏提示）。
      -->
      <el-collapse v-if="importColumns.length > 0" class="code__columns">
        <el-collapse-item :title="`列说明（共 ${importColumns.length} 列，按表头名匹配，列顺序可调）`">
          <el-table :data="importColumns" size="small" border>
            <el-table-column prop="name" label="列名" width="120" />
            <el-table-column label="必需" width="70" align="center">
              <template #default="{ row }">
                <el-tag v-if="row.required" size="small" type="danger" effect="plain">必需</el-tag>
                <span v-else class="code__muted">可选</span>
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
          class="code__tip"
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
          <!--
            类别编码 / 码值 / 码名称都**原样回显**，不查码表翻译：这几列装的可能是
            用户写错的编码（如 `EXAM_TYPO`），翻译过的名字会与他文件里的那一行对不上 ——
            而这张表存在的唯一意义就是让他照着改文件。
          -->
          <el-table-column prop="categoryCode" label="类别编码" width="140" />
          <el-table-column prop="code" label="码值" width="110" />
          <el-table-column prop="name" label="码名称" min-width="140" />
          <el-table-column prop="message" label="失败原因" min-width="240" />
        </el-table>
      </template>

      <template #footer>
        <el-button @click="importVisible = false">关闭</el-button>
      </template>
    </el-dialog>

    <!-- ---------------- 按条件导出（后台任务） ---------------- -->
    <el-dialog v-model="exportVisible" title="按条件导出微代码" width="560px">
      <el-alert
        type="info"
        :closable="false"
        show-icon
        class="code__tip"
        title="导出列与导入模板完全一致（一行 = 一个码项，类别编码与类别名称逐行重复），因此可以直接改一改再导入 —— 这是码表在测试环境与生产环境之间搬运最省事的形态。"
      />

      <el-form label-width="110px">
        <el-form-item label="码类别">
          <!-- 选项来自后端（codeApi.getCategories），前端不写死任何类别编码 / 码值 -->
          <el-select v-model="exportForm.categoryCode" clearable placeholder="全部" style="width: 100%">
            <el-option
              v-for="c in exportCategoryOptions"
              :key="c.code"
              :label="`${c.name}（${c.code}）`"
              :value="c.code"
            />
          </el-select>
        </el-form-item>

        <el-form-item label="关键字">
          <el-input
            v-model="exportForm.keyword"
            placeholder="类别编码 / 类别名称 / 码值 / 码名称，留空为全部"
            clearable
            @keyup.enter="exportFiltered"
          />
        </el-form-item>

        <el-form-item label="只导出启用的">
          <el-switch v-model="exportForm.onlyEnabled" />
          <div class="code__muted">
            默认不勾选，即连同已停用的码项一起导出 —— 搬运/备份时漏掉那几条，
            只会表现成"某处下拉框少一个选项"。勾上则只导出启用的。
          </div>
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="exportVisible = false">取消</el-button>
        <el-button type="primary" :loading="exporting" @click="exportFiltered">导出</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.code__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.code__cat-card :deep(.el-card__body) {
  overflow: hidden;
}

.code__search {
  margin-bottom: 12px;
}

.code__tip {
  margin-bottom: 12px;
}

/* 导出任务的进行状态（排队中 / 已处理 N / M（x%） / 正在生成） */
.code__progress {
  margin-bottom: 12px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.code__import-actions {
  margin-bottom: 12px;
}

.code__columns {
  margin-bottom: 12px;
}

.code__muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.code__inline {
  margin-left: 12px;
}
</style>
