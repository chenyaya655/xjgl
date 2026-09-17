<script setup lang="ts">
import { ref, watch } from 'vue'

import { changeFlowApi } from '@/api'
import type { ChangeApply } from '@/api/types'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'

/**
 * 异动申请详情：申请信息 + 流程时间线 + 操作日志。
 *
 * 抽成组件的理由：申请页与审核页都要看同一份详情，
 * 而"流程走到哪一步、哪几步被合并了"是用户最关心也最容易看错的信息，
 * 只应该有一个实现。
 */
const props = defineProps<{
  applyId?: string | null
}>()

const auth = useAuthStore()

const loading = ref(false)
const apply = ref<ChangeApply | null>(null)

const canRevoke = () => auth.can(MetadataPermissions.Chg.Audit.RevokeApprove)

watch(
  () => props.applyId,
  async (id) => {
    if (!id) {
      apply.value = null

      return
    }

    loading.value = true

    try {
      apply.value = await changeFlowApi.getApply(id)
    } finally {
      loading.value = false
    }
  },
  { immediate: true }
)

function statusTagType(status: string): 'success' | 'danger' | 'warning' | 'info' {
  switch (status) {
    case 'Approved':
      return 'success'
    case 'Rejected':
      return 'danger'
    case 'Pending':
    case 'InProgress':
    case 'Submitted':
      return 'warning'
    default:
      return 'info'
  }
}

/** 当前活动步骤：第一个待处理任务的位置，用于高亮时间线。 */
function activeStep(): number {
  if (!apply.value) {
    return 0
  }

  const index = apply.value.tasks.findIndex((t) => t.status === 'Pending')

  return index < 0 ? apply.value.tasks.length : index
}

function formatTime(value?: string | null): string {
  return value ? value.replace('T', ' ').slice(0, 19) : '—'
}
</script>

<template>
  <div v-loading="loading" class="apply-detail">
    <el-empty v-if="!apply" description="请选择一条申请" :image-size="60" />

    <template v-else>
      <el-descriptions :column="3" size="small" border>
        <el-descriptions-item label="申请单号">{{ apply.applyNo }}</el-descriptions-item>
        <el-descriptions-item label="异动类型">{{ apply.changeTypeName }}</el-descriptions-item>
        <el-descriptions-item label="状态">
          <el-tag size="small" :type="statusTagType(apply.status)">{{ apply.statusName }}</el-tag>
        </el-descriptions-item>

        <el-descriptions-item label="学生">
          {{ apply.studentName }}
          <span class="apply-detail__muted">（{{ apply.studentNo || '无学籍辅号' }}）</span>
        </el-descriptions-item>
        <el-descriptions-item label="原学校">{{ apply.sourceSchoolName || '—' }}</el-descriptions-item>
        <el-descriptions-item label="目标学校">{{ apply.targetSchoolName || '—' }}</el-descriptions-item>

        <el-descriptions-item label="申请时间">{{ formatTime(apply.applyDate) }}</el-descriptions-item>
        <el-descriptions-item label="流程模板版本">v{{ apply.flowTemplateVersion }}</el-descriptions-item>
        <el-descriptions-item label="终审通过时间">
          {{ formatTime(apply.finalApprovedTime) }}
        </el-descriptions-item>

        <el-descriptions-item label="申请理由" :span="3">
          {{ apply.reason || '—' }}
        </el-descriptions-item>
        <el-descriptions-item v-if="apply.remark" label="备注" :span="3">
          {{ apply.remark }}
        </el-descriptions-item>
      </el-descriptions>

      <el-divider content-position="left">审核流程</el-divider>

      <el-steps
        direction="vertical"
        :active="activeStep()"
        :process-status="apply.status === 'Rejected' ? 'error' : 'process'"
        :finish-status="apply.status === 'Approved' ? 'success' : 'finish'"
        class="apply-detail__steps"
      >
        <el-step
          v-for="task in apply.tasks"
          :key="task.id"
          :title="`第 ${task.seq} 步 · ${task.nodeName}`"
        >
          <template #description>
            <div class="apply-detail__step">
              <el-tag size="small" effect="plain">{{ task.orgName }}</el-tag>
              <el-tag size="small" type="info" effect="plain">{{ task.approveLevelName }}</el-tag>
              <el-tag size="small" :type="statusTagType(task.status)">{{ task.statusName }}</el-tag>

              <!--
                mergedFromSeqs 装的是「被合并进来的步骤序号」，
                所以长度大于 0 就说明这一步合并了别的步骤（长度 > 1 的判断会让
                最常见的同县区场景不显示任何提示）。
              -->
              <span
                v-if="task.mergedFromSeqs && task.mergedFromSeqs.length > 0"
                class="apply-detail__merge"
              >
                ⤶ 已与第 {{ task.mergedFromSeqs.join('、') }} 步合并（同一单位只审一次）
              </span>

              <div v-if="task.handledByName" class="apply-detail__meta">
                {{ task.handledByName }} · {{ formatTime(task.handledTime) }}
                <span v-if="task.opinion">：{{ task.opinion }}</span>
              </div>
            </div>
          </template>
        </el-step>
      </el-steps>

      <el-divider content-position="left">操作日志</el-divider>

      <el-timeline>
        <el-timeline-item
          v-for="log in apply.logs"
          :key="log.id"
          :timestamp="formatTime(log.actionTime)"
          placement="top"
        >
          <b>{{ log.actionName || log.action }}</b>
          <span v-if="log.operatorName"> · {{ log.operatorName }}</span>
          <span v-if="log.seq"> · 第 {{ log.seq }} 步</span>
          <div v-if="log.opinion" class="apply-detail__meta">{{ log.opinion }}</div>
        </el-timeline-item>
      </el-timeline>

      <p v-if="canRevoke()" class="apply-detail__muted">
        提示：撤销审核会把学籍状态回滚到申请前，并对相关单位重新产生待办。
      </p>
    </template>
  </div>
</template>

<style scoped>
.apply-detail__muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.apply-detail__steps {
  height: auto;
  max-height: 360px;
  overflow: auto;
}

.apply-detail__step {
  line-height: 1.8;
}

.apply-detail__merge {
  margin-left: 8px;
  font-size: 12px;
  color: var(--el-color-warning);
}

.apply-detail__meta {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}
</style>
