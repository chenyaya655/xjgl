<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'

import { changeFlowApi, ChangeTaskStatuses, MetadataPermissions } from '@xjgl/api'
import type { ChangeApply, ChangeApplyListItem } from '@xjgl/api'
import { useAuthStore } from '@/stores/auth'

/**
 * 我的待办（移动端的主场景）。
 *
 * 移动端与 PC 端的**产品差异**在这里体现得最明显：
 *   - PC 端是"检索 + 批量操作"（管理员一坐一上午，处理几十条）；
 *   - 移动端是"看一眼 + 点两下"（教师在走廊里、课间处理一条）。
 *   因此这里只做三件事：默认只拉前若干条、卡片式大按钮、审完即从列表移除，
 *   不做筛选器、不做批量选择、不做表格。
 *
 * 审核意见的规则与 PC 端一致：**通过可留空，驳回必填** ——
 * 不写理由学校不知道该补什么材料，会反复打电话。
 */
const auth = useAuthStore()

const canApprove = computed(() => auth.can(MetadataPermissions.Chg.Audit.Approve))
const canQuery = computed(() => auth.can(MetadataPermissions.Chg.Audit.Query))

const loading = ref(false)
const rows = ref<ChangeApplyListItem[]>([])
const total = ref(0)

const query = reactive({ skipCount: 0, maxResultCount: 10 })

async function load(reset = false): Promise<void> {
  if (!canQuery.value) {
    return
  }

  loading.value = true

  try {
    if (reset) {
      query.skipCount = 0
      rows.value = []
    }

    const result = await changeFlowApi.getTodoList({
      skipCount: query.skipCount,
      maxResultCount: query.maxResultCount,
      sorting: 'ApplyDate ASC'
    })

    // 追加而不是替换：移动端"加载更多"是连续的
    rows.value = query.skipCount === 0 ? result.items : [...rows.value, ...result.items]
    total.value = result.totalCount
  } finally {
    loading.value = false
  }
}

function loadMore(): void {
  query.skipCount += query.maxResultCount
  void load()
}

const hasMore = computed(() => rows.value.length < total.value)

// ---------------------------------------------------------------------------
// 审核
// ---------------------------------------------------------------------------

const auditVisible = ref(false)
const auditing = ref(false)
const target = ref<ChangeApplyListItem | null>(null)
const auditForm = reactive({ decision: 'approve' as 'approve' | 'reject', opinion: '' })

function openAudit(row: ChangeApplyListItem, decision: 'approve' | 'reject'): void {
  target.value = row
  auditForm.decision = decision
  auditForm.opinion = ''
  auditVisible.value = true
}

async function doAudit(): Promise<void> {
  if (!target.value) {
    return
  }

  if (auditForm.decision === 'reject' && !auditForm.opinion.trim()) {
    ElMessage.warning('驳回必须填写意见。')

    return
  }

  auditing.value = true

  try {
    const payload = { applyId: target.value.id, opinion: auditForm.opinion.trim() || null }

    const updated =
      auditForm.decision === 'approve'
        ? await changeFlowApi.approve(payload)
        : await changeFlowApi.reject(payload)

    ElMessage.success(
      auditForm.decision === 'approve' ? `已通过（${updated.statusName}）` : '已驳回'
    )

    auditVisible.value = false

    // 审完直接把这条从列表里去掉：教师不需要看到自己刚处理过的单据，
    // 让它留在列表里反而会让人怀疑"是不是没点上"
    rows.value = rows.value.filter((r) => r.id !== target.value!.id)
    total.value = Math.max(0, total.value - 1)
  } finally {
    auditing.value = false
  }
}

// ---------------------------------------------------------------------------
// 详情
// ---------------------------------------------------------------------------

const detailVisible = ref(false)
const detailLoading = ref(false)
const detail = ref<ChangeApply | null>(null)

async function openDetail(row: ChangeApplyListItem): Promise<void> {
  detailVisible.value = true
  detailLoading.value = true
  detail.value = null

  try {
    detail.value = await changeFlowApi.getApply(row.id)
  } finally {
    detailLoading.value = false
  }
}

function formatTime(value?: string | null): string {
  return value ? value.replace('T', ' ').slice(5, 16) : '—'
}

onMounted(() => {
  void load(true)
})
</script>

<template>
  <div class="h5-page">
    <div class="todo__head">
      <h2 class="h5-title">我的待办</h2>
      <el-button size="small" :loading="loading" @click="load(true)">刷新</el-button>
    </div>

    <el-alert
      v-if="!canQuery"
      type="warning"
      :closable="false"
      show-icon
      title="您没有异动审核的查询权限，本页不可用。"
    />

    <el-empty
      v-else-if="!loading && rows.length === 0"
      description="没有需要您处理的申请"
      :image-size="80"
    />

    <el-card
      v-for="row in rows"
      :key="row.id"
      shadow="never"
      class="todo__card"
      @click="openDetail(row)"
    >
      <div class="todo__line1">
        <span class="todo__type">{{ row.changeTypeName }}</span>
        <el-tag size="small" type="warning" effect="plain">{{ row.statusName }}</el-tag>
      </div>

      <div class="todo__student">
        {{ row.studentName }}
        <span class="h5-muted">{{ row.studentNo }}</span>
      </div>

      <div class="todo__schools">
        {{ row.sourceSchoolName || '—' }}
        <span v-if="row.targetSchoolName"> → {{ row.targetSchoolName }}</span>
      </div>

      <div class="todo__node">
        第 {{ row.currentSeq }} 步 · {{ row.currentNodeName }}
        <span class="h5-muted">（{{ row.currentOrgName }}）</span>
      </div>

      <div class="todo__footer">
        <span class="h5-muted">{{ row.applyNo }} · {{ formatTime(row.applyDate) }}</span>

        <div v-if="canApprove" class="todo__actions" @click.stop>
          <el-button size="small" type="danger" plain @click="openAudit(row, 'reject')">驳回</el-button>
          <el-button size="small" type="primary" @click="openAudit(row, 'approve')">通过</el-button>
        </div>
      </div>
    </el-card>

    <div v-if="canQuery && hasMore" class="todo__more">
      <el-button text type="primary" :loading="loading" @click="loadMore">
        加载更多（还有 {{ total - rows.length }} 条）
      </el-button>
    </div>

    <!-- ---------------- 审核 ---------------- -->
    <el-dialog
      v-model="auditVisible"
      :title="auditForm.decision === 'approve' ? '审核通过' : '驳回申请'"
      width="92%"
    >
      <el-descriptions v-if="target" :column="1" size="small" border>
        <el-descriptions-item label="申请单号">{{ target.applyNo }}</el-descriptions-item>
        <el-descriptions-item label="学生">{{ target.studentName }}</el-descriptions-item>
        <el-descriptions-item label="我审的环节">
          第 {{ target.currentSeq }} 步 · {{ target.currentNodeName }}
        </el-descriptions-item>
      </el-descriptions>

      <el-input
        v-model="auditForm.opinion"
        type="textarea"
        :rows="3"
        class="todo__opinion"
        :placeholder="auditForm.decision === 'reject' ? '驳回必须填写理由' : '审核意见（可留空）'"
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

    <!-- ---------------- 详情 ---------------- -->
    <el-drawer v-model="detailVisible" title="申请详情" size="92%" direction="btt">
      <div v-loading="detailLoading">
        <template v-if="detail">
          <el-descriptions :column="1" size="small" border>
            <el-descriptions-item label="申请单号">{{ detail.applyNo }}</el-descriptions-item>
            <el-descriptions-item label="异动类型">{{ detail.changeTypeName }}</el-descriptions-item>
            <el-descriptions-item label="学生">
              {{ detail.studentName }}（{{ detail.studentNo }}）
            </el-descriptions-item>
            <el-descriptions-item label="学校变动">
              {{ detail.sourceSchoolName || '—' }}
              <span v-if="detail.targetSchoolName"> → {{ detail.targetSchoolName }}</span>
            </el-descriptions-item>
            <el-descriptions-item label="申请理由">{{ detail.reason || '—' }}</el-descriptions-item>
          </el-descriptions>

          <h3 class="todo__section">审核流程</h3>
          <el-steps
            direction="vertical"
            :active="detail.tasks.findIndex((t) => t.status === ChangeTaskStatuses.Pending)"
          >
            <el-step v-for="task in detail.tasks" :key="task.id" :title="`第 ${task.seq} 步 · ${task.nodeName}`">
              <template #description>
                <div class="todo__step">
                  {{ task.orgName }} · {{ task.statusName }}
                  <span
                    v-if="task.mergedFromSeqs && task.mergedFromSeqs.length > 0"
                    class="todo__merge"
                  >
                    ⤶ 已与第 {{ task.mergedFromSeqs.join('、') }} 步合并（同一单位只审一次）
                  </span>
                  <div v-if="task.handledByName" class="h5-muted">
                    {{ task.handledByName }} · {{ formatTime(task.handledTime) }}
                    <span v-if="task.opinion">：{{ task.opinion }}</span>
                  </div>
                </div>
              </template>
            </el-step>
          </el-steps>
        </template>
      </div>
    </el-drawer>
  </div>
</template>

<style scoped>
.todo__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.todo__card {
  margin-bottom: 10px;
  border-radius: 10px;
}

.todo__line1 {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 6px;
}

.todo__type {
  font-weight: 600;
}

.todo__student {
  font-size: 16px;
  margin-bottom: 4px;
}

.todo__schools,
.todo__node {
  font-size: 13px;
  color: var(--el-text-color-regular);
  line-height: 1.6;
}

.todo__footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: 10px;
}

.todo__actions {
  display: flex;
  gap: 8px;
}

.todo__more {
  text-align: center;
  padding: 12px 0;
}

.todo__opinion {
  margin-top: 14px;
}

.todo__section {
  font-size: 15px;
  margin: 18px 0 10px;
}

.todo__step {
  line-height: 1.8;
  font-size: 13px;
}

.todo__merge {
  margin-left: 6px;
  color: var(--el-color-warning);
  font-size: 12px;
}
</style>
