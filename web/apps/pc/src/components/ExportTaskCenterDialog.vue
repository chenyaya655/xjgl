<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import { ExportTaskStatuses, describeExportProgress, describeTruncation, exportTaskApi, saveBlob } from '@xjgl/api'
import type { ExportTask } from '@xjgl/api'
import AppIcon from '@/components/AppIcon.vue'

/**
 * 「我的导出任务」对话框。
 *
 * 【为什么必须有这个入口】
 * 导出/打印已经改成「提交任务 → 后台生成 → 下载产物」（见 `SysExportTask` 的注释），
 * 但此前前端只能在自己那个页面里干等：用户切走页面、等到超时、或者任务失败之后，
 * **没有任何地方能看到自己发起过什么、跑到哪一步、产物还在不在**——
 * 因此 `exportTaskApi.list / cancel / remove` 三个接口一直没有调用点。
 * 产物有保留期（默认 7 天，过期由后台清理），过了就只能重新导出一遍。
 *
 * 【为什么不做轮询】
 * 任务是"回看与收尾"用的：打开时拉一次 + 手动刷新就够。
 * 自动轮询会让只是打开看一眼的用户一直打接口，而他真正关心的变化
 * （几十秒后任务完成）手动刷一下就能看到。
 */
const visible = defineModel<boolean>({ default: false })

/** 一页取多少条：这里不做分页，只显示最近这些（后端恒定按发起人过滤）。 */
const PAGE_SIZE = 20

const tasks = ref<ExportTask[]>([])
const totalCount = ref(0)
const loading = ref(false)
/** 正在下载/取消/删除的任务 Id：把 loading 精确到行，避免整表转圈。 */
const actingId = ref<string | null>(null)

/**
 * 状态的中文标签与颜色。
 *
 * ⚠ 任务状态**不是码表**：`CodeDataSeedContributor` 里没有对应类别，
 *   后端 `ExportTaskDto` 也只回英文 `Status`（没有 `StatusName`），
 *   所以这里不能用 `useCodeOptions`。值一律用 `ExportTaskStatuses` 常量而不是裸字符串 ——
 *   后端加新状态时这里会漏掉一个键（回落成显示码值本身），而不是悄悄显示成空白。
 */
interface StatusMeta {
  label: string
  type: 'primary' | 'success' | 'info' | 'warning' | 'danger'
}

const STATUS_META: Record<string, StatusMeta> = {
  [ExportTaskStatuses.Pending]: { label: '排队中', type: 'info' },
  [ExportTaskStatuses.Running]: { label: '执行中', type: 'warning' },
  [ExportTaskStatuses.Succeeded]: { label: '已完成', type: 'success' },
  [ExportTaskStatuses.Failed]: { label: '失败', type: 'danger' },
  [ExportTaskStatuses.Canceled]: { label: '已取消', type: 'info' },
  [ExportTaskStatuses.Expired]: { label: '已过期', type: 'info' }
}

function statusLabel(status: string): string {
  return STATUS_META[status]?.label ?? status
}

function statusTagType(status: string): StatusMeta['type'] {
  return STATUS_META[status]?.type ?? 'info'
}

/** 终态：与后端 `ExportTaskStatuses.IsTerminal` 一致（终态任务不再被 worker 领取）。 */
function isTerminal(status: string): boolean {
  return (
    status === ExportTaskStatuses.Succeeded ||
    status === ExportTaskStatuses.Failed ||
    status === ExportTaskStatuses.Canceled ||
    status === ExportTaskStatuses.Expired
  )
}

/** 进度 / 说明。失败时优先显示后端给的原因，否则用户只看到"失败"两个字。 */
function progressText(task: ExportTask): string {
  const parts: string[] = []

  if (task.status === ExportTaskStatuses.Failed) {
    parts.push(task.failureReason ?? task.message ?? '任务执行失败')
  } else if (task.message) {
    parts.push(task.message)
  } else if (task.status === ExportTaskStatuses.Pending) {
    parts.push('排队等待后台执行')
  }

  // 执行中叠一句过程文案（含真百分比）：只说"执行中"用户无从判断还要等多久。
  // 走的是与八个导出入口**同一个**函数 —— 这里曾经自己实现了一遍"已处理 x / y"，
  // 而"进度文案有两处实现"正是债 23 要消灭的那类重复（改一处忘一处，两处会不一致）。
  // 终态不叠：成功/失败/取消/过期时 message 已经把话说完了。
  if (task.status === ExportTaskStatuses.Running) {
    parts.push(describeExportProgress(task))
  }

  return parts.length > 0 ? parts.join(' · ') : '—'
}

function formatTime(value?: string | null): string {
  return value ? value.replace('T', ' ').slice(0, 19) : '—'
}

/**
 * 列表说明。
 *
 * 超出 PAGE_SIZE 时必须写出来："只显示最近 20 条"和"你一共只有 20 条"
 * 在界面上长得一模一样，而用户会照着后者去找一条三个月前的任务。
 */
const countText = computed(() =>
  totalCount.value > PAGE_SIZE
    ? `共 ${totalCount.value} 条，只显示最近 ${PAGE_SIZE} 条`
    : `共 ${totalCount.value} 条`
)

/**
 * 拉取任务列表。
 *
 * 失败时**不弹自己的提示**：拦截器已经弹过了（见 `http.ts` 的 4 号约定）。
 * 这里的 catch 只负责把列表清空 —— 否则查询失败后界面上还挂着上一次的结果，
 * 用户会以为自己看到的是最新的。
 */
async function load(): Promise<void> {
  loading.value = true

  try {
    const result = await exportTaskApi.list({ maxResultCount: PAGE_SIZE })

    tasks.value = result.items
    totalCount.value = result.totalCount
  } catch {
    tasks.value = []
    totalCount.value = 0
  } finally {
    loading.value = false
  }
}

async function download(task: ExportTask): Promise<void> {
  actingId.value = task.id

  try {
    const blob = await exportTaskApi.download(task.id)

    // 文件名用后端给的（它带人数/日期等有用信息）。兜底只在极端情况下生效：
    // `canDownload` 为真时后端一定已经写好了 FileName。
    saveBlob(blob, task.fileName || task.kindName)

    // 截断必须显式提示：名单/照片少人少张是最不能接受的失败方式，
    // 而后端已经如实算出"少了多少"，这里只弹"下载完成"等于把那份交代吞掉。
    const truncation = describeTruncation(task)

    if (truncation) {
      ElMessage.warning(truncation)
    } else {
      ElMessage.success(`${task.kindName}已开始下载`)
    }
  } catch {
    // 拦截器已提示（产物可能已过保留期被清理）；不关对话框，用户刷新一下就能看到真实状态
  } finally {
    actingId.value = null
  }
}

async function cancel(task: ExportTask): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定取消「${task.kindName}」吗？取消后不会再生成产物，需要的话得重新提交。`,
      '取消任务',
      { type: 'warning', confirmButtonText: '确定取消', cancelButtonText: '不取消' }
    )
  } catch {
    return // 用户取消
  }

  actingId.value = task.id

  try {
    await exportTaskApi.cancel(task.id)
    ElMessage.success('任务已取消')
  } catch {
    // 拦截器已提示（例如"任务正在执行，无法取消"）
  } finally {
    actingId.value = null
    // 无论取消成功与否都刷新：失败时也能让用户看到任务此刻的真实状态
    await load()
  }
}

async function removeTask(task: ExportTask): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定删除「${task.kindName}」这条记录吗？产物文件会一并删除，且不可恢复。`,
      '删除记录',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' }
    )
  } catch {
    return // 用户取消
  }

  actingId.value = task.id

  try {
    await exportTaskApi.remove(task.id)
    ElMessage.success('记录已删除')
  } catch {
    // 拦截器已提示
  } finally {
    actingId.value = null
    await load()
  }
}

// 每次打开都重新拉一次：任务状态在对话框关闭期间会变（后台还在跑）
watch(visible, (opened) => {
  if (opened) {
    void load()
  }
})
</script>

<template>
  <el-dialog v-model="visible" title="我的导出任务" width="1000px" top="8vh" destroy-on-close>
    <el-alert
      type="info"
      :closable="false"
      show-icon
      class="task-center__tip"
      title="这里只显示你自己发起的学生名单 / 学籍卡 / 花名册 / 照片 zip 任务。产物有保留期，过期后由后台清理，届时只能重新导出；正在执行的任务无法取消（后端不支持中途打断），请等它结束后删除记录。"
    />

    <div class="task-center__toolbar">
      <el-button :loading="loading" @click="load">
        <AppIcon name="Refresh" />
        <span>刷新</span>
      </el-button>

      <span class="task-center__muted">{{ countText }}</span>
    </div>

    <el-table
      v-loading="loading"
      :data="tasks"
      border
      stripe
      max-height="58vh"
      empty-text="你还没有发起过导出或打印任务"
    >
      <el-table-column label="类型" width="130">
        <template #default="{ row }">{{ row.kindName }}</template>
      </el-table-column>

      <el-table-column label="状态" width="100" align="center">
        <template #default="{ row }">
          <el-tag size="small" :type="statusTagType(row.status)">{{ statusLabel(row.status) }}</el-tag>
        </template>
      </el-table-column>

      <el-table-column label="进度 / 说明" min-width="260" show-overflow-tooltip>
        <template #default="{ row }">
          <span>{{ progressText(row) }}</span>
          <!-- 被上限截断的产物必须一眼看得出来，而不是混在说明文字里 -->
          <el-tag v-if="row.isTruncated" size="small" type="warning" effect="plain" class="task-center__flag">
            已截断
          </el-tag>
        </template>
      </el-table-column>

      <el-table-column label="产物" min-width="220" show-overflow-tooltip>
        <template #default="{ row }">{{ row.fileName ?? '—' }}</template>
      </el-table-column>

      <el-table-column label="创建时间" width="170">
        <template #default="{ row }">{{ formatTime(row.creationTime) }}</template>
      </el-table-column>

      <el-table-column label="操作" width="170" align="center" fixed="right">
        <template #default="{ row }">
          <!-- 用后端给的 canDownload 而不是自己按 status 推断：
               将来加了新状态，两边的判断必然会漂移 -->
          <el-button
            v-if="row.canDownload"
            link
            type="primary"
            :loading="actingId === row.id"
            @click="download(row)"
          >
            下载
          </el-button>

          <!-- 执行中不提供取消：后端是"一次跑完"，中途打断只会留下半个产物，
               所以它会把请求直接顶回来。与其放一个点了必然报错的按钮，不如说清楚。 -->
          <span v-else-if="row.status === ExportTaskStatuses.Running" class="task-center__muted">
            执行中，不可取消
          </span>

          <el-button
            v-else-if="!isTerminal(row.status)"
            link
            type="primary"
            :loading="actingId === row.id"
            @click="cancel(row)"
          >
            取消
          </el-button>

          <el-button
            v-if="isTerminal(row.status)"
            link
            type="danger"
            :loading="actingId === row.id"
            @click="removeTask(row)"
          >
            删除记录
          </el-button>
        </template>
      </el-table-column>
    </el-table>
  </el-dialog>
</template>

<style scoped>
.task-center__tip {
  margin-bottom: 12px;
}

.task-center__toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 12px;
}

.task-center__muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.task-center__flag {
  margin-left: 8px;
  transform: scale(0.9);
}
</style>
