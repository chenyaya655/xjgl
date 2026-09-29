<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import {
  describeExportProgress,
  describeTruncation,
  exportTaskApi,
  provisionApi,
  orgApi,
  saveBlob
} from '@/api'
import type { CreatePredefineUser, ExportTask, OrgDto, OrgTreeNode, PredefineUser } from '@/api/types'
import { ExportTaskKinds } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'

/**
 * 用户预置与激活（管理员侧）。
 *
 * 为什么不让管理员直接建账号、而是"预置 + 本人激活"：
 *   全省教师要建几万个账号，管理员既不可能知道每个人的密码，
 *   也不该知道 —— 让管理员代设密码，等于所有账号的初始口令都在管理员手里。
 *   改成：管理员只预置"姓名 + 手机号 + 所属学校 + 角色"，
 *   系统生成激活码；教师本人凭手机号 + 姓名 + 激活码设置自己的密码。
 *
 * 因此本页最重要的两个信息是：**激活码**与**有效期** ——
 * 界面上直接给出复制按钮，续期也要一步可达（教师一直没激活是常态）。
 */
const auth = useAuthStore()

const canCreate = computed(() => auth.can(MetadataPermissions.Sys.UserProvision.Create))
const canImport = computed(() => auth.can(MetadataPermissions.Sys.UserProvision.Import))
const canUpdate = computed(() => auth.can(MetadataPermissions.Sys.UserProvision.Update))
const canDelete = computed(() => auth.can(MetadataPermissions.Sys.UserProvision.Delete))

/**
 * 导出预置名单。
 *
 * 后端这个导出种类要求 `Sys.UserProvision.Export` **且** `Sys.UserProvision.Query`
 * （导出复用了列表查询，好让数据范围与手机号脱敏只有一处实现）。
 * 前端仍然只按 Export 显隐：再叠一个 Query 判断，只会让"两条权限都给了却看不到按钮"
 * 变成一个没人能解释的问题 —— 而后端会在提交任务时给出明确原因。
 */
const canExport = computed(() => auth.can(MetadataPermissions.Sys.UserProvision.Export))

const statusOptions = [
  { value: 'Pending', label: '待激活' },
  { value: 'Activated', label: '已激活' },
  { value: 'Expired', label: '已过期' },
  { value: 'Revoked', label: '已吊销' }
]

const loading = ref(false)
const rows = ref<PredefineUser[]>([])
const total = ref(0)

const query = reactive({
  keyword: '',
  status: undefined as string | undefined,
  maxResultCount: 20
})

const page = ref(1)

async function load(): Promise<void> {
  loading.value = true

  try {
    const result = await provisionApi.getList({
      keyword: query.keyword.trim() || undefined,
      status: query.status,
      skipCount: (page.value - 1) * query.maxResultCount,
      maxResultCount: query.maxResultCount,
      sorting: 'CreationTime DESC'
    })

    rows.value = result.items
    total.value = result.totalCount
  } finally {
    loading.value = false
  }
}

function search(): void {
  page.value = 1
  void load()
}

function statusTagType(status: string): 'success' | 'danger' | 'warning' | 'info' {
  switch (status) {
    case 'Activated':
      return 'success'
    case 'Pending':
      return 'warning'
    case 'Expired':
    case 'Revoked':
      return 'danger'
    default:
      return 'info'
  }
}

async function copyCode(row: PredefineUser): Promise<void> {
  try {
    await navigator.clipboard.writeText(row.activationCode)
    ElMessage.success(`激活码已复制：${row.activationCode}`)
  } catch {
    // 浏览器不支持剪贴板 API（或非 https）时，退化成直接显示
    ElMessage.info(`激活码：${row.activationCode}`)
  }
}

// ---------------------------------------------------------------------------
// 新增预置
// ---------------------------------------------------------------------------

const dialogVisible = ref(false)
const saving = ref(false)

const form = reactive({
  orgId: '',
  realName: '',
  phoneNumber: '',
  idCardNumber: '',
  roleNames: 'Teacher',
  position: '',
  remark: '',
  activationValidDays: 30
})

/**
 * 组织选择用**懒加载的树选择器**：
 * 全省上万所学校，一次拉整棵树要 1MB+，而这里只需要选一个节点。
 * 懒加载的写法与「组织管理」页一致（根节点取省厅，逐层展开）。
 */
async function loadOrgNode(
  node: { level: number; data: OrgDto | OrgTreeNode },
  resolve: (data: unknown[]) => void
): Promise<void> {
  try {
    if (node.level === 0) {
      resolve((await orgApi.getRoots()).map((o) => ({ ...o, leaf: false })))

      return
    }

    resolve((await orgApi.getChildren(node.data.id)).map((c) => ({ ...c, leaf: !c.hasChildren })))
  } catch {
    resolve([])
  }
}

function openCreate(): void {
  // 默认落在当前登录用户所属组织：管理员在自己单位下建人是绝大多数场景
  form.orgId = auth.profile?.orgId ?? ''
  form.realName = ''
  form.phoneNumber = ''
  form.idCardNumber = ''
  form.roleNames = 'Teacher'
  form.position = ''
  form.remark = ''
  form.activationValidDays = 30
  dialogVisible.value = true
}

async function doCreate(): Promise<void> {
  if (!form.orgId) {
    ElMessage.warning('请选择所属组织（学校/教育局）。')

    return
  }

  if (!form.realName.trim() || !form.phoneNumber.trim()) {
    ElMessage.warning('姓名与手机号都不能为空（激活时要靠它们核对身份）。')

    return
  }

  saving.value = true

  try {
    const created = await provisionApi.create({
      realName: form.realName.trim(),
      phoneNumber: form.phoneNumber.trim(),
      idCardNumber: form.idCardNumber?.trim() || null,
      orgId: form.orgId,
      roleNames: form.roleNames?.trim() || null,
      position: form.position?.trim() || null,
      remark: form.remark?.trim() || null,
      activationValidDays: form.activationValidDays
    })

    ElMessage.success(`已预置。激活码：${created.activationCode}（${form.activationValidDays} 天内有效）`)
    dialogVisible.value = false
    await load()
  } finally {
    saving.value = false
  }
}

// ---------------------------------------------------------------------------
// 批量预置（从 Excel 复制粘贴）
// ---------------------------------------------------------------------------

const importVisible = ref(false)
const importing = ref(false)
const importText = ref('')
const importResult = ref<{ succeeded: number; total: number; message: string } | null>(null)

function openImport(): void {
  importText.value = ''
  importResult.value = null
  importVisible.value = true
}

/**
 * 解析粘贴的表格文本。
 *
 * 为什么用"从 Excel 复制粘贴"而不是做文件上传：
 *   学籍管理员手里的名单本来就是 Excel，选中一片区域 Ctrl+C 再粘进文本框
 *   比"导出 CSV→上传→等解析"少三步，而且不用引入 Excel 解析库。
 *   格式：每行 `姓名,手机号[,身份证号][,岗位]`（逗号或制表符分隔，两种都兼容）。
 */
function parseImport(): CreatePredefineUser[] | null {
  const lines = importText.value
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l.length > 0)

  if (lines.length === 0) {
    ElMessage.warning('请先粘贴名单。')

    return null
  }

  const users: CreatePredefineUser[] = []

  for (const line of lines) {
    const cells = line.split(/[,\t]/).map((c) => c.trim())

    if (cells.length < 2 || !cells[0] || !cells[1]) {
      ElMessage.warning(`这一行格式不对（至少要有姓名和手机号）：${line}`)

      return null
    }

    users.push({
      realName: cells[0],
      phoneNumber: cells[1],
      idCardNumber: cells[2] || null,
      orgId: auth.profile?.orgId ?? '',
      roleNames: 'Teacher',
      position: cells[3] || null,
      remark: null,
      activationValidDays: 30
    })
  }

  return users
}

async function doImport(): Promise<void> {
  const users = parseImport()

  if (!users) {
    return
  }

  importing.value = true

  try {
    const result = await provisionApi.import(users)

    importResult.value = {
      total: result.total,
      succeeded: result.succeeded,
      message:
        result.errors.length === 0
          ? `全部成功（${result.succeeded} 人），批次号 ${result.batchNo}`
          : `成功 ${result.succeeded} / ${result.total}，失败 ${result.errors.length} 行：` +
            result.errors
              .slice(0, 5)
              .map((e) => `第 ${e.rowIndex} 行 ${e.realName ?? ''}：${e.message}`)
              .join('；')
    }

    if (result.errors.length === 0) {
      ElMessage.success(importResult.value.message)
      await load()
    } else {
      ElMessage.warning(importResult.value.message)
      await load()
    }
  } finally {
    importing.value = false
  }
}

// ---------------------------------------------------------------------------
// 续期 / 吊销 / 删除
// ---------------------------------------------------------------------------

async function renew(row: PredefineUser): Promise<void> {
  const updated = await provisionApi.renew(row.id, 30)
  ElMessage.success(`已续期 30 天，新激活码：${updated.activationCode}`)
  await load()
}

async function revoke(row: PredefineUser): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定吊销「${row.realName}」的激活码吗？如果该账号已激活，账号也会被停用。`,
      '吊销确认',
      { type: 'warning' }
    )
  } catch {
    return
  }

  await provisionApi.revoke(row.id)
  ElMessage.success('已吊销')
  await load()
}

async function remove(row: PredefineUser): Promise<void> {
  try {
    await ElMessageBox.confirm(`确定删除预置记录「${row.realName}」吗？`, '删除确认', {
      type: 'warning'
    })
  } catch {
    return
  }

  await provisionApi.remove(row.id)
  ElMessage.success('已删除')
  await load()
}

// ---------------------------------------------------------------------------
// 导出（后台任务）
// ---------------------------------------------------------------------------

/** 页面卸载后停止轮询（理由同组织/用户导出：切走之后没人需要那个定时器）。 */
let disposed = false
onUnmounted(() => {
  disposed = true
})

const exporting = ref(false)
const exportProgressText = ref('')

/**
 * 导出预置名单（沿用当前关键字与状态，看到什么就导出什么）。
 *
 * ⚠ **身份证列是掩码的**（只保留后 4 位，其余 `*`）：本系统的身份证是加密存储的，
 *   导出明文等于把加密白做一遍。代价是这份文件**不能回导** ——
 *   激活校验要的是完整身份证号，所以文件第一行与任务提示里都写明了这一点。
 */
async function exportPredefines(): Promise<void> {
  exporting.value = true
  exportProgressText.value = '排队中…'

  try {
    const task = await exportTaskApi.run(
      {
        kind: ExportTaskKinds.UserProvisionCsv,
        userProvisionQuery: {
          keyword: query.keyword.trim() || undefined,
          status: query.status
        }
      },
      {
        shouldStop: () => disposed,
        onProgress: (t: ExportTask) => {
          // 预置名单导出是分页累积的，**有**真百分比（已处理 N / M（x%））；
          // 文案只有 `describeExportProgress` 一处实现（债 23）。
          exportProgressText.value = describeExportProgress(t)
        }
      }
    )

    const blob = await exportTaskApi.download(task.id)

    saveBlob(blob, task.fileName || `预置名单_${new Date().toISOString().slice(0, 10)}.csv`)

    // 预置记录会随年份增长，没有天然上限，后端按 Xjgl:Export:MaxRows 收口 ——
    // 触顶必须显式提示，否则"少了 800 个人"看起来就像"全县就这么多"。
    const truncation = describeTruncation(task)

    if (truncation) {
      ElMessage.warning(truncation)
    } else {
      ElMessage.success(
        task.message ? `${task.kindName}已完成：${task.message}` : `${task.kindName}已完成`
      )
    }
  } catch (e) {
    // 与其它导出同一处处理：HTTP 失败已由拦截器提示（且给出后端 reason），
    // 只有 `exportTaskApi.run` 抛的业务错误需要在这里补一句，
    // 否则就是"点了导出、转圈停下、什么都没发生"。
    if (!(e as { isAxiosError?: boolean })?.isAxiosError) {
      ElMessage.error(e instanceof Error ? e.message : '导出失败，请稍后重试')
    }
  } finally {
    exporting.value = false
    exportProgressText.value = ''
  }
}

onMounted(() => {
  void load()
})
</script>

<template>
  <div class="prov">
    <el-card shadow="never" class="prov__filter">
      <el-form :inline="true" @submit.prevent="search">
        <el-form-item label="关键字">
          <el-input
            v-model="query.keyword"
            placeholder="姓名 / 手机号"
            clearable
            style="width: 220px"
            @keyup.enter="search"
          >
            <template #prefix>
              <AppIcon name="Search" />
            </template>
          </el-input>
        </el-form-item>

        <el-form-item label="状态">
          <el-select v-model="query.status" placeholder="全部" clearable style="width: 130px">
            <el-option v-for="s in statusOptions" :key="s.value" :label="s.label" :value="s.value" />
          </el-select>
        </el-form-item>

        <el-form-item>
          <el-button type="primary" :loading="loading" @click="search">查询</el-button>
          <el-button @click="((query.keyword = ''), (query.status = undefined), search())">重置</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <el-card shadow="never">
      <template #header>
        <div class="prov__header">
          <span>预置名单（共 {{ total }} 人）</span>
          <div>
            <!--
              导出按 `Sys.UserProvision.Export` 显隐（后端 `ExportTaskPermissions`）：
              没有这层判断，按钮点得动而接口回 403 —— 最难解释的失败。
              位置放在「批量预置」左边：导出是"看"，批量预置是"改"，不该混在一起。
            -->
            <el-button
              v-if="canExport"
              :loading="exporting"
              :disabled="exporting"
              @click="exportPredefines"
            >
              <AppIcon name="Download" />
              <span>导出</span>
            </el-button>
            <el-button v-if="canImport" @click="openImport">
              <AppIcon name="Upload" />
              <span>批量预置</span>
            </el-button>
            <el-button v-if="canCreate" type="primary" @click="openCreate">
              <AppIcon name="Plus" />
              <span>新增预置</span>
            </el-button>
          </div>
        </div>
      </template>

      <!-- 导出要等后台生成；只说"转圈"用户不知道卡在哪一步 -->
      <div v-if="exportProgressText" class="prov__muted">{{ exportProgressText }}</div>

      <el-alert
        v-if="canExport"
        type="warning"
        :closable="false"
        show-icon
        class="prov__tip"
        title="导出的身份证号已掩码（只保留后 4 位，其余为 *）：身份证在本系统里是加密存储的，导出明文等于把加密白做一遍。代价是这份文件不能回导 —— 重新预置时必须填写完整身份证号。文件第一行也写了同样的说明。"
      />

      <el-alert
        type="info"
        :closable="false"
        show-icon
        class="prov__tip"
        title="教师本人用「手机号 + 姓名 + 激活码」自助设置密码完成激活。管理员不需要、也不应该知道任何人的密码。"
      />

      <el-table v-loading="loading" :data="rows" border stripe height="calc(100vh - 340px)">
        <el-table-column prop="realName" label="姓名" width="120" />
        <el-table-column prop="phoneNumber" label="手机号" width="140" />
        <el-table-column prop="position" label="岗位" width="120">
          <template #default="{ row }">{{ row.position || '—' }}</template>
        </el-table-column>
        <el-table-column prop="roleNames" label="角色" width="140">
          <template #default="{ row }">{{ row.roleNames || '—' }}</template>
        </el-table-column>
        <el-table-column label="激活码" width="180">
          <template #default="{ row }">
            <span class="prov__code">{{ row.activationCode }}</span>
            <el-button link type="primary" @click="copyCode(row)">复制</el-button>
          </template>
        </el-table-column>
        <el-table-column label="有效期至" width="170" align="center">
          <template #default="{ row }">
            {{ row.activationExpiresAt.replace('T', ' ').slice(0, 16) }}
          </template>
        </el-table-column>
        <el-table-column label="状态" width="100" align="center">
          <template #default="{ row }">
            <el-tag size="small" :type="statusTagType(row.status)">{{ row.statusName }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="激活时间" width="170" align="center">
          <template #default="{ row }">
            {{ row.activatedAt ? row.activatedAt.replace('T', ' ').slice(0, 16) : '—' }}
          </template>
        </el-table-column>
        <el-table-column label="操作" width="200" align="center" fixed="right">
          <template #default="{ row }">
            <el-button v-if="canUpdate" link type="primary" @click="renew(row)">续期</el-button>
            <el-button
              v-if="canUpdate && row.status !== 'Revoked'"
              link
              type="warning"
              @click="revoke(row)"
            >
              吊销
            </el-button>
            <el-button v-if="canDelete" link type="danger" @click="remove(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>

      <el-pagination
        v-model:current-page="page"
        v-model:page-size="query.maxResultCount"
        class="prov__pager"
        :total="total"
        layout="total, sizes, prev, pager, next"
        :page-sizes="[20, 50, 100]"
        @current-change="load"
        @size-change="search"
      />
    </el-card>

    <!-- ---------------- 新增 ---------------- -->
    <el-dialog v-model="dialogVisible" title="新增预置用户" width="620px">
      <el-form label-width="110px">
        <el-form-item label="所属组织" required>
          <el-tree-select
            v-model="form.orgId"
            lazy
            :load="loadOrgNode"
            :props="{ label: 'name', value: 'id', isLeaf: 'leaf' }"
            check-strictly
            filterable
            placeholder="选择学校或教育局（逐层展开）"
            style="width: 100%"
          />
        </el-form-item>

        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="姓名" required>
              <el-input v-model="form.realName" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="手机号" required>
              <el-input v-model="form.phoneNumber" placeholder="激活时用它核对身份" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="身份证号">
          <el-input v-model="form.idCardNumber" placeholder="选填；填了激活时会多核对一项" />
        </el-form-item>

        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="角色">
              <el-input v-model="form.roleNames" placeholder="多个角色用逗号分隔，如 Teacher" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="岗位">
              <el-input v-model="form.position" placeholder="如 学籍管理员" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="激活有效期">
          <el-input-number v-model="form.activationValidDays" :min="1" :max="365" />
          <span class="prov__muted prov__inline">天</span>
        </el-form-item>

        <el-form-item label="备注">
          <el-input v-model="form.remark" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="doCreate">预置</el-button>
      </template>
    </el-dialog>

    <!-- ---------------- 批量预置 ---------------- -->
    <el-dialog v-model="importVisible" title="批量预置（从 Excel 复制粘贴）" width="720px">
      <el-alert
        type="info"
        :closable="false"
        show-icon
        class="prov__tip"
        title="每行一条：姓名,手机号[,身份证号][,岗位]。逗号或制表符分隔都支持 —— 直接从 Excel 选中区域复制粘贴即可。"
      />

      <el-input
        v-model="importText"
        type="textarea"
        :rows="10"
        placeholder="张三,13800000001,410102199001011234,学籍管理员&#10;李四,13800000002"
      />

      <el-alert
        v-if="importResult"
        :type="importResult.succeeded === importResult.total ? 'success' : 'warning'"
        :closable="false"
        show-icon
        class="prov__tip"
        :title="importResult.message"
      />

      <template #footer>
        <el-button @click="importVisible = false">关闭</el-button>
        <el-button type="primary" :loading="importing" @click="doImport">开始预置</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.prov__filter {
  margin-bottom: 16px;
}

.prov__filter :deep(.el-form-item) {
  margin-bottom: 0;
}

.prov__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.prov__tip {
  margin-bottom: 12px;
}

.prov__code {
  font-family: Consolas, Monaco, monospace;
  margin-right: 6px;
}

.prov__muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.prov__inline {
  margin-left: 8px;
}

.prov__pager {
  margin-top: 16px;
  justify-content: flex-end;
}
</style>
