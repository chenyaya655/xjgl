<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import {
  changeFlowApi,
  changeTypeApi,
  describeExportProgress,
  describeTruncation,
  exportTaskApi,
  saveBlob
} from '@/api'
import type { ChangeApplyListItem, ChangeType, ExportTask } from '@/api/types'
import { ChangeApplyStatuses, ExportTaskKinds } from '@/api/types'
import ChangeApplyDetail from '@/components/ChangeApplyDetail.vue'
import AppIcon from '@/components/AppIcon.vue'
import { CodeCategories, useCodeOptions } from '@/composables/useCodeOptions'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'

/**
 * 异动审核：待办办理 + 全体申请查看 + 撤销审核。
 *
 * 设计上刻意把"待办"放在第一页签：审核人员的日常动作是
 * 「打开系统 → 看有没有轮到我 → 处理掉」，而不是"检索申请"。
 *
 * 另一个要点：**审核意见不是可选项**。驳回必须写理由
 * （否则学校不知道要补什么材料，会反复打电话），
 * 因此驳回时前端强制要求填写意见。
 */
const auth = useAuthStore()

const canApprove = computed(() => auth.can(MetadataPermissions.Chg.Audit.Approve))
const canRevoke = computed(() => auth.can(MetadataPermissions.Chg.Audit.RevokeApprove))
const canQuery = computed(() => auth.can(MetadataPermissions.Chg.Audit.Query))
const canExport = computed(() => auth.can(MetadataPermissions.Chg.Audit.Export))

const activeTab = ref(canQuery.value ? 'todo' : 'todo')

const types = ref<ChangeType[]>([])

// ---------------------------------------------------------------------------
// 待办
// ---------------------------------------------------------------------------

const todoLoading = ref(false)
const todoRows = ref<ChangeApplyListItem[]>([])
const todoTotal = ref(0)
const todoPage = ref(1)
const todoQuery = reactive({ maxResultCount: 20 })

async function loadTodo(): Promise<void> {
  todoLoading.value = true

  try {
    const result = await changeFlowApi.getTodoList({
      skipCount: (todoPage.value - 1) * todoQuery.maxResultCount,
      maxResultCount: todoQuery.maxResultCount,
      sorting: 'ApplyDate ASC'
    })

    todoRows.value = result.items
    todoTotal.value = result.totalCount
  } finally {
    todoLoading.value = false
  }
}

// ---------------------------------------------------------------------------
// 全部申请
// ---------------------------------------------------------------------------

const allLoading = ref(false)
const allRows = ref<ChangeApplyListItem[]>([])
const allTotal = ref(0)
const allPage = ref(1)
const allQuery = reactive({
  changeTypeId: undefined as string | undefined,
  status: undefined as string | undefined,
  keyword: '',
  maxResultCount: 20
})

/**
 * 申请状态下拉项**来自码表**（CHANGE_APPLY_STATUS），不在前端硬编码。
 *
 * 这里原来写的是 5 条手写字面量，漏掉了「草稿」与「已归档」——
 * 而码表里有 7 条。手写清单与码表**必然会漂移**，而漏项的后果是
 * "这个状态永远筛不出来"，看起来像后端没数据。现在统一走 useCodeOptions，
 * 后端加了新状态前端自动就有（与"学籍状态过滤"那次是同一类缺陷，见债 12）。
 */
const statusCodes = useCodeOptions(CodeCategories.ChangeApplyStatus)

async function loadAll(): Promise<void> {
  allLoading.value = true

  try {
    const result = await changeFlowApi.getApplies({
      changeTypeId: allQuery.changeTypeId,
      status: allQuery.status,
      keyword: allQuery.keyword || undefined,
      skipCount: (allPage.value - 1) * allQuery.maxResultCount,
      maxResultCount: allQuery.maxResultCount,
      sorting: 'ApplyDate DESC'
    })

    allRows.value = result.items
    allTotal.value = result.totalCount
  } finally {
    allLoading.value = false
  }
}

function searchAll(): void {
  allPage.value = 1
  void loadAll()
}

// ---------------------------------------------------------------------------
// 审核
// ---------------------------------------------------------------------------

const auditVisible = ref(false)
const auditing = ref(false)
const auditTarget = ref<ChangeApplyListItem | null>(null)
const auditForm = reactive({
  decision: 'approve' as 'approve' | 'reject',
  opinion: ''
})

function openAudit(row: ChangeApplyListItem, decision: 'approve' | 'reject' = 'approve'): void {
  auditTarget.value = row
  auditForm.decision = decision
  auditForm.opinion = ''
  auditVisible.value = true
}

async function doAudit(): Promise<void> {
  if (!auditTarget.value) {
    return
  }

  // 驳回必须写理由：不写理由学校不知道该补什么，会反复打电话
  if (auditForm.decision === 'reject' && !auditForm.opinion.trim()) {
    ElMessage.warning('驳回必须填写意见。')

    return
  }

  auditing.value = true

  try {
    const payload = { applyId: auditTarget.value.id, opinion: auditForm.opinion.trim() || null }

    const updated =
      auditForm.decision === 'approve'
        ? await changeFlowApi.approve(payload)
        : await changeFlowApi.reject(payload)

    ElMessage.success(
      auditForm.decision === 'approve'
        ? `已通过。当前状态：${updated.statusName}`
        : '已驳回'
    )

    auditVisible.value = false
    await Promise.all([loadTodo(), loadAll()])
  } finally {
    auditing.value = false
  }
}

async function revoke(row: ChangeApplyListItem): Promise<void> {
  let opinion = ''

  try {
    const result = await ElMessageBox.prompt(
      '撤销审核会把学籍状态回滚到申请前，并对相关单位重新产生待办。请填写撤销原因：',
      `撤销审核 —— ${row.applyNo}`,
      {
        type: 'warning',
        inputType: 'textarea',
        inputPlaceholder: '如：材料不齐，需重新核实',
        confirmButtonText: '确认撤销',
        cancelButtonText: '取消'
      }
    )

    opinion = result.value ?? ''
  } catch {
    return
  }

  await changeFlowApi.revokeApproval({ applyId: row.id, opinion: opinion || null })
  ElMessage.success('已撤销审核')
  await Promise.all([loadTodo(), loadAll()])
}

// ---------------------------------------------------------------------------
// 详情
// ---------------------------------------------------------------------------

const detailVisible = ref(false)
const detailId = ref<string | null>(null)

function openDetail(row: ChangeApplyListItem): void {
  detailId.value = row.id
  detailVisible.value = true
}

function statusTagType(status: string): 'success' | 'danger' | 'warning' | 'info' {
  switch (status) {
    case ChangeApplyStatuses.Approved:
      return 'success'
    case ChangeApplyStatuses.Rejected:
      return 'danger'
    case ChangeApplyStatuses.Submitted:
    case ChangeApplyStatuses.InProgress:
      return 'warning'
    default:
      return 'info'
  }
}

// ---------------------------------------------------------------------------
// 导出审核流水
// ---------------------------------------------------------------------------

/**
 * 导出**审核流水台账**（一个审批节点一行），不是申请单清单。
 *
 * 两份产物分开是有意的：一份申请在流水里是多行（可能有驳回重提、多级审核），
 * 所以"清单"与"台账"的行数对不上是正常的；复核与纪检要的是后者
 * （谁、什么时候、批了什么意见）。清单那份在申请页，权限也分开。
 *
 * 筛选条件沿用「全部申请」页签的当前条件 —— 台账通常就是"把这个范围的流水打出来"。
 */
const exporting = ref(false)
const progressText = ref('')

/**
 * 页面卸载后停止轮询（否则切走之后定时器还会每 1.5 秒打到超时）。
 */
let disposed = false
onUnmounted(() => {
  disposed = true
})

async function exportAuditTrail(): Promise<void> {
  exporting.value = true
  progressText.value = '排队中…'

  try {
    const task = await exportTaskApi.run(
      {
        kind: ExportTaskKinds.ChangeAuditCsv,
        // 刻意**不带分页参数**：条件被快照进任务，分页累积由后端按 Xjgl:Export:MaxRows 收口
        changeQuery: {
          changeTypeId: allQuery.changeTypeId,
          status: allQuery.status,
          keyword: allQuery.keyword || undefined,
          sorting: 'ApplyDate DESC'
        }
      },
      {
        shouldStop: () => disposed,
        onProgress: (t: ExportTask) => {
          // 审核流水导出是分页累积的，**有**真百分比（已处理 N / M（x%））；
          // 文案只有 `describeExportProgress` 一处实现（债 23）。
          progressText.value = describeExportProgress(t)
        }
      }
    )

    const blob = await exportTaskApi.download(task.id)

    // 文件名以**后端给的**为准（带了日期等有用信息），后端没给才用兜底名
    saveBlob(blob, task.fileName || `异动审核流水_${new Date().toISOString().slice(0, 10)}.csv`)

    // 截断必须显式提示：流水没有天然上限，后端按 MaxRows 截，
    // 少了几行却只弹"成功"，复核时"某个节点没审批记录"就会变成冤案。
    const truncation = describeTruncation(task)

    if (truncation) {
      ElMessage.warning(truncation)
    } else {
      ElMessage.success(
        task.message ? `${task.kindName}已完成：${task.message}` : `${task.kindName}已完成`
      )
    }
  } catch (e) {
    // 与微代码 / 异动申请导出同一处理：HTTP 失败已被拦截器弹过（带后端 reason），
    // 只有 `exportTaskApi.run` 抛的业务错误（任务 Failed / 被取消 / 等超时）
    // 不经过拦截器 —— 不提示就等于"点了导出、转圈停下、什么都没发生"。
    if (!(e as { isAxiosError?: boolean })?.isAxiosError) {
      ElMessage.error(e instanceof Error ? e.message : '导出失败，请稍后重试')
    }
  } finally {
    // 无论成功、失败还是超时都要清掉，否则那行提示会一直留在页面上
    exporting.value = false
    progressText.value = ''
  }
}

onMounted(async () => {
  // 码表与异动类型并行拉取（互不依赖，串行白等一个往返）
  const [, typeList] = await Promise.all([
    statusCodes.load(),
    changeTypeApi.getTypes(false).catch(() => [] as ChangeType[])
  ])

  types.value = typeList

  await loadTodo()
})
</script>

<template>
  <div class="chg-audit">
    <el-card shadow="never">
      <el-tabs v-model="activeTab" @tab-change="(name: string) => (name === 'all' ? loadAll() : loadTodo())">
        <!-- ---------------- 我的待办 ---------------- -->
        <el-tab-pane name="todo">
          <template #label>
            <span>我的待办</span>
            <el-badge v-if="todoTotal > 0" :value="todoTotal" type="danger" class="chg-audit__badge" />
          </template>

          <el-alert
            v-if="!auth.profile?.orgId"
            type="warning"
            :closable="false"
            show-icon
            class="chg-audit__tip"
            title="您的账号未关联具体组织，因此这里展示的是全系统的待审任务；已绑定单位的审核员只会看到本单位待办。"
          />

          <div class="chg-audit__toolbar">
            <el-alert
              type="info"
              :closable="false"
              show-icon
              class="chg-audit__tip"
              title="只列出「当前轮到我所在单位审核」的申请。同一单位被多个节点解析到时只会出现一次 —— 这就是「同县区转学县区只审一次」。"
            />
            <el-button @click="loadTodo">
              <AppIcon name="Refresh" />
              <span>刷新</span>
            </el-button>
          </div>

          <el-table v-loading="todoLoading" :data="todoRows" border stripe height="calc(100vh - 380px)">
            <el-table-column prop="applyNo" label="申请单号" width="180" />
            <el-table-column prop="changeTypeName" label="异动类型" width="110" />
            <el-table-column label="学生" width="150">
              <template #default="{ row }">
                {{ row.studentName }}
                <span class="chg-audit__muted">{{ row.studentNo }}</span>
              </template>
            </el-table-column>
            <el-table-column label="学校变动" min-width="260">
              <template #default="{ row }">
                {{ row.sourceSchoolName || '—' }}
                <span v-if="row.targetSchoolName"> → {{ row.targetSchoolName }}</span>
              </template>
            </el-table-column>
            <el-table-column label="待我审的环节" min-width="200">
              <template #default="{ row }">
                第 {{ row.currentSeq }} 步 · {{ row.currentNodeName }}
                <div class="chg-audit__muted">{{ row.currentOrgName }}</div>
              </template>
            </el-table-column>
            <el-table-column label="申请时间" width="160" align="center">
              <template #default="{ row }">{{ row.applyDate.replace('T', ' ').slice(0, 16) }}</template>
            </el-table-column>
            <el-table-column label="操作" width="220" align="center" fixed="right">
              <template #default="{ row }">
                <el-button link type="primary" @click="openDetail(row)">详情</el-button>
                <template v-if="canApprove">
                  <el-button link type="success" @click="openAudit(row, 'approve')">通过</el-button>
                  <el-button link type="danger" @click="openAudit(row, 'reject')">驳回</el-button>
                </template>
              </template>
            </el-table-column>
          </el-table>

          <el-pagination
            v-model:current-page="todoPage"
            v-model:page-size="todoQuery.maxResultCount"
            class="chg-audit__pager"
            :total="todoTotal"
            layout="total, sizes, prev, pager, next"
            :page-sizes="[20, 50, 100]"
            @current-change="loadTodo"
            @size-change="loadTodo"
          />
        </el-tab-pane>

        <!-- ---------------- 全部申请 ---------------- -->
        <el-tab-pane label="全部申请" name="all">
          <div class="chg-audit__toolbar">
            <el-select v-model="allQuery.changeTypeId" placeholder="全部类型" clearable style="width: 170px">
              <el-option v-for="t in types" :key="t.id" :label="t.name" :value="t.id" />
            </el-select>
            <el-select v-model="allQuery.status" placeholder="全部状态" clearable style="width: 130px">
              <el-option
                v-for="s in statusCodes.options.value"
                :key="s.value"
                :label="s.label"
                :value="s.value"
              />
            </el-select>
            <el-input
              v-model="allQuery.keyword"
              placeholder="申请单号 / 学生姓名"
              clearable
              style="width: 200px"
              @keyup.enter="searchAll"
            />
            <el-button type="primary" @click="searchAll">查询</el-button>

            <el-button
              v-if="canExport"
              plain
              :loading="exporting"
              @click="exportAuditTrail"
            >
              <AppIcon name="Download" />
              <span>导出审核流水</span>
            </el-button>

            <!-- 导出改成任务式之后要等后台生成（可能几十秒），
                 只说"转圈"用户不知道卡在哪一步，所以把真实状态写出来。 -->
            <span v-if="progressText" class="chg-audit__muted">{{ progressText }}</span>
          </div>

          <el-table v-loading="allLoading" :data="allRows" border stripe height="calc(100vh - 400px)">
            <el-table-column prop="applyNo" label="申请单号" width="180" />
            <el-table-column prop="changeTypeName" label="异动类型" width="110" />
            <el-table-column prop="studentName" label="学生" width="120" />
            <el-table-column label="学校变动" min-width="240">
              <template #default="{ row }">
                {{ row.sourceSchoolName || '—' }}
                <span v-if="row.targetSchoolName"> → {{ row.targetSchoolName }}</span>
              </template>
            </el-table-column>
            <el-table-column label="状态" width="100" align="center">
              <template #default="{ row }">
                <el-tag size="small" :type="statusTagType(row.status)">{{ row.statusName }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column label="当前环节" min-width="180">
              <template #default="{ row }">
                <span v-if="row.currentSeq">第 {{ row.currentSeq }} 步 · {{ row.currentNodeName }}</span>
                <span v-else class="chg-audit__muted">—</span>
              </template>
            </el-table-column>
            <el-table-column label="操作" width="200" align="center" fixed="right">
              <template #default="{ row }">
                <el-button link type="primary" @click="openDetail(row)">详情</el-button>
                <el-button
                  v-if="
                    canRevoke &&
                    row.status !== ChangeApplyStatuses.Approved &&
                    row.status !== ChangeApplyStatuses.Rejected
                  "
                  link
                  type="warning"
                  @click="revoke(row)"
                >
                  撤销审核
                </el-button>
              </template>
            </el-table-column>
          </el-table>

          <el-pagination
            v-model:current-page="allPage"
            v-model:page-size="allQuery.maxResultCount"
            class="chg-audit__pager"
            :total="allTotal"
            layout="total, sizes, prev, pager, next"
            :page-sizes="[20, 50, 100]"
            @current-change="loadAll"
            @size-change="searchAll"
          />
        </el-tab-pane>
      </el-tabs>
    </el-card>

    <!-- ---------- 审核对话框 ---------- -->
    <el-dialog
      v-model="auditVisible"
      :title="auditForm.decision === 'approve' ? '审核通过' : '驳回申请'"
      width="560px"
    >
      <el-descriptions v-if="auditTarget" :column="1" size="small" border>
        <el-descriptions-item label="申请单号">{{ auditTarget.applyNo }}</el-descriptions-item>
        <el-descriptions-item label="学生">{{ auditTarget.studentName }}</el-descriptions-item>
        <el-descriptions-item label="异动类型">{{ auditTarget.changeTypeName }}</el-descriptions-item>
        <el-descriptions-item label="我审的环节">
          第 {{ auditTarget.currentSeq }} 步 · {{ auditTarget.currentNodeName }}
        </el-descriptions-item>
      </el-descriptions>

      <el-form label-width="80px" class="chg-audit__audit-form">
        <el-form-item label="审核意见">
          <el-input
            v-model="auditForm.opinion"
            type="textarea"
            :rows="3"
            :placeholder="auditForm.decision === 'reject' ? '驳回必须填写理由' : '可留空'"
          />
        </el-form-item>
      </el-form>

      <el-alert
        v-if="auditForm.decision === 'approve'"
        type="info"
        :closable="false"
        show-icon
        title="通过后会流转到下一步；若这是最后一步，学籍信息会按申请单内容自动更新。"
      />
      <el-alert
        v-else
        type="warning"
        :closable="false"
        show-icon
        title="驳回后整个申请终止，学生学籍信息不变。"
      />

      <template #footer>
        <el-button @click="auditVisible = false">取消</el-button>
        <el-button
          :type="auditForm.decision === 'approve' ? 'primary' : 'danger'"
          :loading="auditing"
          @click="doAudit"
        >
          {{ auditForm.decision === 'approve' ? '确认通过' : '确认驳回' }}
        </el-button>
      </template>
    </el-dialog>

    <!-- ---------- 详情 ---------- -->
    <el-drawer v-model="detailVisible" title="异动申请详情" size="52%">
      <ChangeApplyDetail :apply-id="detailId" />
    </el-drawer>
  </div>
</template>

<style scoped>
.chg-audit__toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
  flex-wrap: wrap;
}

.chg-audit__toolbar .el-button {
  margin-left: 0;
}

.chg-audit__tip {
  flex: 1;
  min-width: 320px;
}

.chg-audit__badge {
  margin-left: 6px;
  margin-top: -2px;
}

.chg-audit__muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.chg-audit__pager {
  margin-top: 16px;
  justify-content: flex-end;
}

.chg-audit__audit-form {
  margin-top: 16px;
}
</style>
