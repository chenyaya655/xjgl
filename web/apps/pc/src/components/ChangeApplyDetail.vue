<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import { changeAttachmentApi, changeFlowApi, saveBlob } from '@/api'
import type { ChangeApply, ChangeAttachment } from '@/api/types'
import { ChangeApplyStatuses, ChangeTaskStatuses } from '@/api/types'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'

/**
 * 异动申请详情：申请信息 + 证明材料 + 流程时间线 + 操作日志。
 *
 * 抽成组件的理由：申请页与审核页都要看同一份详情，
 * 而"流程走到哪一步、哪几步被合并了"是用户最关心也最容易看错的信息，
 * 只应该有一个实现。证明材料更是如此：**审核人要看得见材料才能批**，
 * 两处各写一遍迟早在其中一处漏掉"缺材料"的提示。
 */
const props = defineProps<{
  applyId?: string | null
}>()

const auth = useAuthStore()

const loading = ref(false)
const apply = ref<ChangeApply | null>(null)

const canRevoke = () => auth.can(MetadataPermissions.Chg.Audit.RevokeApprove)

/**
 * 读附件的权限口径与后端**逐字对齐**：申请查询 **或** 审核查询，任一即可。
 *
 * 为什么不是只看 `Chg.Apply.Query`：审核员手上可能只有 `Chg.Audit.*`，
 * 而他要打开材料才能批 —— 后端为此把读权限做成了"任一"
 * （见 `IChangeApplyAccessGuard.EnsureReadPermissionAsync`），
 * 前端若照旧只认一个，就会出现"接口本来允许、界面却不给看"的怪事。
 */
const canReadAttachments = computed(
  () => auth.can(MetadataPermissions.Chg.Apply.Query) || auth.can(MetadataPermissions.Chg.Audit.Query)
)

/** 传/删附件属于"改申请单的材料"，与 `Chg.Apply.Update` 对齐（审核人不改别人的材料）。 */
const canEditAttachments = computed(() => auth.can(MetadataPermissions.Chg.Apply.Update))

const attachments = ref<ChangeAttachment[]>([])
const attachmentsLoading = ref(false)
const uploading = ref(false)

async function loadAttachments(id: string): Promise<void> {
  if (!canReadAttachments.value) {
    attachments.value = []

    return
  }

  attachmentsLoading.value = true

  try {
    attachments.value = await changeAttachmentApi.getList(id)
  } finally {
    attachmentsLoading.value = false
  }
}

watch(
  () => props.applyId,
  async (id) => {
    if (!id) {
      apply.value = null
      attachments.value = []

      return
    }

    loading.value = true

    try {
      apply.value = await changeFlowApi.getApply(id)
      await loadAttachments(id)
    } finally {
      loading.value = false
    }
  },
  { immediate: true }
)

/**
 * 上传成功后要**把详情重新拉一次**：
 * "材料已齐"这件事是后端算出来的（`attachmentRequirementSatisfied`），
 * 前端自己把提示抹掉就成了两份判断 —— 而两份判断迟早会不一致。
 */
async function refresh(id: string): Promise<void> {
  apply.value = await changeFlowApi.getApply(id)
  await loadAttachments(id)
}

async function uploadAttachment(file: File): Promise<void> {
  if (!props.applyId) {
    return
  }

  uploading.value = true

  try {
    // 大小/格式一律由后端判：白名单与 10MB 上限都写在 ChangeAttachmentConventions，
    // 前端再抄一份就是"两份事实"（改了一边另一边不报错，只是拦不住）。
    // 后端给的原因是可照着改的中文（"附件大小 12.3 MB 超过上限 10 MB"）。
    await changeAttachmentApi.upload(props.applyId, file)
    ElMessage.success('证明材料已上传')
    await refresh(props.applyId)
  } finally {
    uploading.value = false
  }
}

/** `el-upload` 的默认行为是它自己发请求，这里改成走统一的 axios 实例（带令牌、带错误提示）。 */
function handleUpload(options: { file: File }): void {
  void uploadAttachment(options.file as File)
}

async function downloadAttachment(row: ChangeAttachment): Promise<void> {
  // 走 axios 取 Blob 再另存：接口要 Bearer 令牌，`<a href>` 直接指过去会拿到登录页
  const blob = await changeAttachmentApi.download(row.id)

  saveBlob(blob, row.fileName)
}

async function removeAttachment(row: ChangeAttachment): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定删除证明材料「${row.fileName}」吗？删除后不可恢复。`,
      '删除确认',
      { type: 'warning' }
    )
  } catch {
    return
  }

  await changeAttachmentApi.remove(row.id)
  ElMessage.success('证明材料已删除')

  if (props.applyId) {
    await refresh(props.applyId)
  }
}

function formatSize(bytes: number): string {
  return bytes >= 1024 * 1024
    ? `${(bytes / 1024 / 1024).toFixed(2)} MB`
    : `${(bytes / 1024).toFixed(0)} KB`
}

function statusTagType(status: string): 'success' | 'danger' | 'warning' | 'info' {
  switch (status) {
    case ChangeApplyStatuses.Approved:
      return 'success'
    case ChangeApplyStatuses.Rejected:
      return 'danger'
    case ChangeTaskStatuses.Pending:
    case ChangeApplyStatuses.InProgress:
    case ChangeApplyStatuses.Submitted:
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

  const index = apply.value.tasks.findIndex((t) => t.status === ChangeTaskStatuses.Pending)

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

      <!-- ---------------- 证明材料 ---------------- -->
      <template v-if="canReadAttachments">
        <el-divider content-position="left">
          证明材料（{{ attachments.length }} 份）
        </el-divider>

        <!--
          "必传但还没有"要在**打开详情的第一眼**就说清楚：
          审核人看到这句话才知道"现在批会被拒、该催材料"，
          而不是点了"通过"再收到一条错误提示。
        -->
        <el-alert
          v-if="apply.requireAttachment && !apply.attachmentRequirementSatisfied"
          type="warning"
          :closable="false"
          show-icon
          title="该异动类型要求上传证明材料，目前一份都没有 —— 补齐之前无法批准。"
          class="apply-detail__alert"
        />

        <el-alert
          v-else-if="apply.requireAttachment"
          type="success"
          :closable="false"
          show-icon
          title="该异动类型要求上传证明材料，已满足。"
          class="apply-detail__alert"
        />

        <el-table v-loading="attachmentsLoading" :data="attachments" size="small" border>
          <el-table-column prop="fileName" label="文件名" min-width="220" show-overflow-tooltip />
          <el-table-column label="大小" width="100" align="right">
            <template #default="{ row }">{{ formatSize(row.fileSize) }}</template>
          </el-table-column>
          <el-table-column prop="uploadedByName" label="上传人" width="120">
            <template #default="{ row }">{{ row.uploadedByName || '—' }}</template>
          </el-table-column>
          <el-table-column label="上传时间" width="160" align="center">
            <template #default="{ row }">{{ formatTime(row.uploadedAt) }}</template>
          </el-table-column>
          <el-table-column label="操作" width="140" align="center">
            <template #default="{ row }">
              <el-button link type="primary" @click="downloadAttachment(row)">下载</el-button>
              <el-button v-if="canEditAttachments" link type="danger" @click="removeAttachment(row)">
                删除
              </el-button>
            </template>
          </el-table-column>
        </el-table>

        <div v-if="canEditAttachments" class="apply-detail__upload">
          <el-upload
            :show-file-list="false"
            :http-request="handleUpload"
            :disabled="uploading"
            accept=".jpg,.jpeg,.png,.bmp,.pdf,.doc,.docx,.xls,.xlsx"
          >
            <el-button :loading="uploading">上传证明材料</el-button>
          </el-upload>
          <span class="apply-detail__muted">支持图片 / PDF / Office 文档，单份不超过 10 MB。</span>
        </div>

        <p v-else class="apply-detail__muted">
          证明材料只能由申请单位的经办人上传；审核人只读，不能替换材料。
        </p>
      </template>

      <el-divider content-position="left">审核流程</el-divider>

      <el-steps
        direction="vertical"
        :active="activeStep()"
        :process-status="apply.status === ChangeApplyStatuses.Rejected ? 'error' : 'process'"
        :finish-status="apply.status === ChangeApplyStatuses.Approved ? 'success' : 'finish'"
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

.apply-detail__alert {
  margin-bottom: 8px;
}

.apply-detail__upload {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-top: 8px;
}
</style>
