<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import { exportTaskApi, orgApi, photoApi, saveBlob, describeTruncation, studentApi, PhotoStatuses } from '@/api'
import type {
  MissingPhotoStudent,
  OrgDto,
  PhotoDto,
  PhotoStorageHealth,
  PhotoUploadItemResult,
  PhotoUploadResult
} from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import SchoolSelect from '@/components/SchoolSelect.vue'
import { CodeCategories, useCodeOptions } from '@/composables/useCodeOptions'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'
import { flattenSchools } from '@/utils/org'

/**
 * 照片管理。
 *
 * 三个页签对应三类真实工作场景：
 *   1) 台账 —— 查某张照片的状态（正常 / 降级待补偿 / 失败），删错传的照片；
 *   2) 缺照片名单 —— 学校最常用的功能：看还差哪些学生没交照片；
 *   3) 批量上传 —— 按「文件名 = 学籍辅号」批量匹配后上传。
 *
 * 上传交互上的两个关键决定：
 *   - **先匹配再上传**：文件与学生的对应关系在界面上让用户确认一遍，
 *     避免「传完了才发现张冠李戴」——照片装错人是很难发现的错误；
 *   - **失败逐条反馈**：后端返回每一张的结果（成功 / 秒传 / 降级 / 失败），
 *     界面照实展示，绝不把「降级暂存」说成「上传成功」。
 */
const auth = useAuthStore()

const activeTab = ref('ledger')
const loading = ref(false)
const schools = ref<OrgDto[]>([])
const health = ref<PhotoStorageHealth | null>(null)

const canUpload = computed(() => auth.can(MetadataPermissions.Pho.Photo.Upload))
const canDelete = computed(() => auth.can(MetadataPermissions.Pho.Photo.Delete))
const canExport = computed(() => auth.can(MetadataPermissions.Pho.Photo.Export))

// ---------------------------------------------------------------------------
// 打包导出
//
// 导出的是"当前筛选条件下你看到的这批"（沿用台账的筛选条件），
// 压缩包里除了照片还有一份清单 CSV：哪张有、哪张在台账里有但存储里读不到。
// 读不到的照片**必须出现在清单里**，否则老师会以为"照片都齐了"。
// ---------------------------------------------------------------------------

const exportDialogVisible = ref(false)
const exporting = ref(false)
const exportOptions = reactive({
  useThumbnail: false,
  includeMissingList: true,
  maxCount: 500
})

/** 页面卸载后停止轮询（打包上千张照片可能要几十秒，用户很可能等不及就切走了）。 */
let disposed = false
onUnmounted(() => {
  disposed = true
  // 顺带释放原图与缩略图的对象 URL：不释放的话切页后这些 Blob 还占着内存
  releaseOriginal()
  Object.values(thumbUrls.value).forEach((url) => URL.revokeObjectURL(url))
  thumbUrls.value = {}
})

async function exportPhotos(): Promise<void> {
  exporting.value = true

  try {
    // 照片打包要读上千个文件再压缩，同步做会把请求线程占满、zip 还全在内存里。
    // 改成任务式后由后台生成，这里只等通知并下载。
    const task = await exportTaskApi.run(
      {
        kind: 'PhotoZip',
        photoQuery: {
          status: ledgerQuery.status,
          keyword: ledgerQuery.keyword || undefined,
          useThumbnail: exportOptions.useThumbnail,
          includeMissingList: exportOptions.includeMissingList,
          maxCount: exportOptions.maxCount
        }
      },
      { shouldStop: () => disposed }
    )

    saveBlob(await exportTaskApi.download(task.id), task.fileName || `学生照片_${new Date().toISOString().slice(0, 10)}.zip`)

    exportDialogVisible.value = false

    // 缺照片张数必须说出来：压缩包里那份清单写了，但用户不一定会打开看，
    // 而"以为照片都齐了"是这里最不能接受的误解。
    const truncation = describeTruncation(task)

    if (truncation) {
      ElMessage.warning(truncation)
    } else {
      ElMessage.success(task.message ?? '已开始下载')
    }
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '导出失败，请稍后重试')
  } finally {
    exporting.value = false
  }
}
const canCompensate = computed(() => auth.can(MetadataPermissions.Pho.Photo.Compensate))

/**
 * 照片状态下拉项**来自码表**（PHOTO_STATUS），不再手写。
 *
 * 原来这里是 4 行硬编码的中文标签。手写码值的风险不是"打错字"，而是
 * **两端各自漂移且互不报错**：码表改了中文名、或后端加了新状态，
 * 这里照样显示旧的、新状态在下拉里根本选不到。
 * 与学籍状态、异动状态统一走 `useCodeOptions`（债 12）。
 */
const photoStatusCodes = useCodeOptions(CodeCategories.PhotoStatus)
const statusOptions = photoStatusCodes.options

// ---------------------------------------------------------------------------
// 台账
// ---------------------------------------------------------------------------

const ledgerQuery = reactive({
  keyword: '',
  status: undefined as string | undefined,
  maxResultCount: 20
})

const ledgerPage = ref(1)
const ledgerRows = ref<PhotoDto[]>([])
const ledgerTotal = ref(0)
const ledgerSelection = ref<PhotoDto[]>([])

async function loadLedger(): Promise<void> {
  loading.value = true

  try {
    const result = await photoApi.getList({
      keyword: ledgerQuery.keyword || undefined,
      status: ledgerQuery.status,
      skipCount: (ledgerPage.value - 1) * ledgerQuery.maxResultCount,
      maxResultCount: ledgerQuery.maxResultCount,
      sorting: 'UploadedTime DESC'
    })

    ledgerRows.value = result.items
    ledgerTotal.value = result.totalCount

    void loadThumbnails(result.items)
  } finally {
    loading.value = false
  }
}

// ---------------------------------------------------------------------------
// 缩略图
//
// 为什么要自己取 Blob 而不是给 <img> 一个 URL：
//   照片接口要求 Bearer 令牌，浏览器发图片请求不会带 Authorization 头。
//   因此用 axios 取回二进制 → createObjectURL 交给 <el-image>。
//
// 释放很重要：不 revoke 的话，翻十几页就会一直占着几十 MB 内存。
// ---------------------------------------------------------------------------

const thumbUrls = ref<Record<string, string>>({})

function thumbUrl(studentId: string): string | undefined {
  return thumbUrls.value[studentId]
}

/** 只拉取状态正常的照片（失败/待处理的没有可看的图）。 */
async function loadThumbnails(rows: PhotoDto[]): Promise<void> {
  const wanted = rows.filter(
    (r) => r.status === PhotoStatuses.Normal || r.status === PhotoStatuses.FallbackPending
  )

  // 换页时先释放上一页的对象 URL
  Object.values(thumbUrls.value).forEach((url) => URL.revokeObjectURL(url))
  thumbUrls.value = {}

  const next: Record<string, string> = {}

  await Promise.all(
    wanted.map(async (row) => {
      try {
        const blob = await photoApi.getThumbnail(row.studentId)

        next[row.studentId] = URL.createObjectURL(blob)
      } catch {
        // 单张取不到不影响整页（例如照片文件已从存储里消失）
      }
    })
  )

  thumbUrls.value = next
}

// ---------------------------------------------------------------------------
// 看原图
// ---------------------------------------------------------------------------

/**
 * 原图预览。
 *
 * 【为什么不能直接用缩略图放大】
 * 台账里的图是 480px 缩略图，放大后糊成一片 —— 而"这张照片是不是这个学生"
 * 恰恰要靠细节（五官、校服校徽）判断。所以单独拉一次原图。
 *
 * 与缩略图同一条约束：**不能用 `<img src="/api/...">` 直接指后端**，
 * 照片接口要求 Bearer 令牌，浏览器发图片请求不会带 Authorization 头，
 * 必然 401（表现为图片裂开）。因此统一 axios 取 Blob → createObjectURL。
 */
const originalVisible = ref(false)
const originalLoading = ref(false)
const originalUrl = ref('')
const originalTitle = ref('')

/** 释放上一次的对象 URL（不释放的话每看一张都留一份图在内存里）。 */
function releaseOriginal(): void {
  if (originalUrl.value) {
    URL.revokeObjectURL(originalUrl.value)
    originalUrl.value = ''
  }
}

async function viewOriginal(row: PhotoDto): Promise<void> {
  originalTitle.value = `${row.studentName ?? ''}（${row.studentNo ?? ''}）`
  originalLoading.value = true
  originalVisible.value = true
  releaseOriginal()

  try {
    const blob = await photoApi.getContent(row.studentId)
    originalUrl.value = URL.createObjectURL(blob)
  } catch {
    // 读不到原图（例如文件已从存储里消失）：如实说明，别留个空框
    originalVisible.value = false
    ElMessage.error('读不到这张照片的原图，可能文件已从存储中丢失（可看「状态 / 异常说明」）。')
  } finally {
    originalLoading.value = false
  }
}

/** 关闭预览时释放（`el-dialog` 的 closed 事件）。 */
function onOriginalClosed(): void {
  releaseOriginal()
}

async function removePhotos(ids: string[]): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定删除选中的 ${ids.length} 张照片吗？NAS 上的文件会一并删除，且无法恢复。`,
      '删除确认',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' }
    )
  } catch {
    return
  }

  const deleted = await photoApi.deleteMany(ids)
  ElMessage.success(`已删除 ${deleted} 张照片`)

  await Promise.all([loadLedger(), loadMissing(), refreshHealth()])
}

// ---------------------------------------------------------------------------
// 缺照片名单
// ---------------------------------------------------------------------------

const missingQuery = reactive({
  schoolOrgId: undefined as string | undefined,
  keyword: '',
  maxResultCount: 50
})

const missingPage = ref(1)
const missingRows = ref<MissingPhotoStudent[]>([])
const missingTotal = ref(0)

async function loadMissing(): Promise<void> {
  loading.value = true

  try {
    const result = await photoApi.getMissingList({
      schoolOrgId: missingQuery.schoolOrgId,
      keyword: missingQuery.keyword || undefined,
      skipCount: (missingPage.value - 1) * missingQuery.maxResultCount,
      maxResultCount: missingQuery.maxResultCount,
      sorting: 'StudentNo ASC'
    })

    missingRows.value = result.items
    missingTotal.value = result.totalCount
  } finally {
    loading.value = false
  }
}

// ---------------------------------------------------------------------------
// 存储健康与补偿
// ---------------------------------------------------------------------------

async function refreshHealth(): Promise<void> {
  try {
    health.value = await photoApi.getStorageHealth()
  } catch {
    health.value = null
  }
}

async function compensate(): Promise<void> {
  const result = await photoApi.compensate()

  if (result.aborted) {
    ElMessage.warning(result.message)
  } else {
    ElMessage.success(result.message)
  }

  await Promise.all([loadLedger(), refreshHealth()])
}

// ---------------------------------------------------------------------------
// 批量上传
// ---------------------------------------------------------------------------

interface UploadRow {
  file: File
  /** 文件名去掉扩展名后当作学籍辅号 */
  studentNo: string
  studentId?: string
  studentName?: string
  matched: boolean
  note?: string
}

const uploadVisible = ref(false)
const uploadSchoolId = ref<string | undefined>(undefined)
const uploadRows = ref<UploadRow[]>([])
const matching = ref(false)
const uploading = ref(false)
const uploadPercent = ref(0)
const uploadResult = ref<PhotoUploadResult | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)

const matchedCount = computed(() => uploadRows.value.filter((r) => r.matched).length)

function pickFiles(): void {
  fileInput.value?.click()
}

async function onFilesPicked(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files ?? [])

  // 允许重复打开同一个文件后仍能触发 change
  input.value = ''

  if (files.length === 0) {
    return
  }

  uploadRows.value = files.map((file) => ({
    file,
    studentNo: file.name.replace(/\.[^.]+$/, '').trim(),
    matched: false
  }))

  uploadResult.value = null

  await matchStudents()
}

/**
 * 把文件名匹配到学生。
 *
 * 先一次性拉「缺照片名单」在本校范围内建索引（绝大多数上传都是补齐缺照片的学生），
 * 未命中的再按学籍辅号逐个精确查 —— 这样 50 个文件通常只需要 1 次请求，
 * 而不是 50 次。
 */
async function matchStudents(): Promise<void> {
  if (!uploadSchoolId.value) {
    ElMessage.warning('请先选择学校，再进行文件匹配。')

    return
  }

  matching.value = true

  try {
    const missing = await photoApi.getMissingList({
      schoolOrgId: uploadSchoolId.value,
      maxResultCount: 500,
      sorting: 'StudentNo ASC'
    })

    const index = new Map<string, MissingPhotoStudent>()

    for (const student of missing.items) {
      if (student.studentNo) {
        index.set(student.studentNo, student)
      }
    }

    for (const row of uploadRows.value) {
      const hit = index.get(row.studentNo)

      if (hit) {
        row.studentId = hit.studentId
        row.studentName = hit.name ?? undefined
        row.matched = true
        row.note = '缺照片名单'
        continue
      }

      row.matched = false
      row.note = '未匹配'
    }

    // 兜底：可能是「替换已有照片」，不在缺照片名单里，逐个精确查
    for (const row of uploadRows.value.filter((r) => !r.matched && r.studentNo)) {
      if (!/^\d{10,20}$/.test(row.studentNo)) {
        row.note = '文件名不是学籍辅号'

        continue
      }

      const found = await studentApi.getList({
        schoolOrgId: uploadSchoolId.value,
        keyword: row.studentNo,
        maxResultCount: 5
      })

      const exact = found.items.find((s) => s.studentNo === row.studentNo)

      if (exact) {
        row.studentId = exact.id
        row.studentName = exact.name
        row.matched = true
        row.note = '已有照片，将覆盖'
      } else {
        row.note = '本校查无此学籍辅号'
      }
    }
  } finally {
    matching.value = false
  }
}

async function doUpload(): Promise<void> {
  const ready = uploadRows.value.filter((r) => r.matched && r.studentId)

  if (ready.length === 0) {
    ElMessage.warning('没有匹配到任何学生的文件，请检查文件名是否为学籍辅号。')

    return
  }

  uploading.value = true
  uploadPercent.value = 0

  try {
    uploadResult.value = await photoApi.upload(
      ready.map((r) => r.file),
      ready.map((r) => r.studentId!),
      crypto.randomUUID(),
      (percent) => (uploadPercent.value = percent)
    )

    const result = uploadResult.value

    if (result.failed > 0) {
      ElMessage.warning(result.message)
    } else if (result.degraded > 0) {
      ElMessage.warning(result.message)
    } else {
      ElMessage.success(result.message)
    }

    await Promise.all([loadLedger(), loadMissing(), refreshHealth()])
  } finally {
    uploading.value = false
  }
}

/** 上传结果按学生 Id 索引，便于在匹配表里直接显示每张的结果。 */
function resultOf(studentId?: string): PhotoUploadItemResult | undefined {
  if (!studentId || !uploadResult.value) {
    return undefined
  }

  return uploadResult.value.items.find((i) => i.studentId === studentId)
}

function openUploadDialog(): void {
  uploadSchoolId.value = missingQuery.schoolOrgId
  uploadRows.value = []
  uploadResult.value = null
  uploadPercent.value = 0
  uploadVisible.value = true
}

// ---------------------------------------------------------------------------
// 初始化
// ---------------------------------------------------------------------------

onMounted(async () => {
  try {
    schools.value = flattenSchools(await orgApi.getTree())
  } catch {
    schools.value = []
  }

  // 码表失败不阻塞页面：下拉为空，但台账/名单/健康度照常
  await photoStatusCodes.load()

  await Promise.all([loadLedger(), loadMissing(), refreshHealth()])
})
</script>

<template>
  <div class="photo-list">
    <el-card v-if="health" shadow="never" class="photo-list__health">
      <div class="photo-list__health-row">
        <el-tag :type="health.primaryAvailable ? 'success' : 'danger'" effect="dark">
          {{ health.primaryName }}：{{ health.primaryAvailable ? '可写' : '不可写' }}
        </el-tag>

        <el-tag :type="health.circuitState === 'Closed' ? 'success' : 'warning'" effect="plain">
          {{ health.circuitStateText }}
        </el-tag>

        <el-tag v-if="health.fallbackPendingCount > 0" type="warning" effect="plain">
          降级待补传 {{ health.fallbackPendingCount }} 张
        </el-tag>

        <el-tag v-if="health.failedCount > 0" type="danger" effect="plain">
          处理失败 {{ health.failedCount }} 张
        </el-tag>

        <div class="photo-list__health-actions">
          <el-button size="small" @click="refreshHealth">
            <AppIcon name="Refresh" />
            <span>刷新状态</span>
          </el-button>

          <el-button v-if="canCompensate" size="small" type="primary" plain @click="compensate">
            <AppIcon name="Upload" />
            <span>立即补偿回 NAS</span>
          </el-button>
        </div>
      </div>
    </el-card>

    <el-card shadow="never">
      <el-tabs v-model="activeTab">
        <!-- ---------------- 台账 ---------------- -->
        <el-tab-pane label="照片台账" name="ledger">
          <div class="photo-list__toolbar">
            <el-input
              v-model="ledgerQuery.keyword"
              placeholder="学籍辅号 / 姓名"
              clearable
              style="width: 200px"
              @keyup.enter="loadLedger"
            />
            <el-select v-model="ledgerQuery.status" placeholder="全部状态" clearable style="width: 150px">
              <el-option v-for="s in statusOptions" :key="s.value" :label="s.label" :value="s.value" />
            </el-select>
            <el-button type="primary" @click="loadLedger">查询</el-button>

            <el-button
              v-if="canDelete"
              type="danger"
              plain
              :disabled="ledgerSelection.length === 0"
              @click="removePhotos(ledgerSelection.map((r) => r.id))"
            >
              删除选中（{{ ledgerSelection.length }}）
            </el-button>

            <div class="photo-list__spacer" />

            <el-button v-if="canExport" :loading="exporting" @click="exportDialogVisible = true">
              <AppIcon name="Download" />
              <span>导出照片</span>
            </el-button>

            <el-button v-if="canUpload" type="primary" @click="openUploadDialog">
              <AppIcon name="UploadFilled" />
              <span>批量上传</span>
            </el-button>
          </div>

          <el-table
            v-loading="loading"
            :data="ledgerRows"
            border
            stripe
            height="calc(100vh - 360px)"
            @selection-change="(rows: PhotoDto[]) => (ledgerSelection = rows)"
          >
            <el-table-column type="selection" width="45" />
            <el-table-column label="照片" width="76" align="center">
              <template #default="{ row }">
                <el-image
                  v-if="thumbUrl(row.studentId)"
                  :src="thumbUrl(row.studentId)"
                  :preview-src-list="[thumbUrl(row.studentId)!]"
                  fit="cover"
                  preview-teleported
                  class="photo-ledger__thumb"
                />
                <span v-else class="photo-ledger__no-thumb">—</span>
              </template>
            </el-table-column>
            <el-table-column prop="studentNo" label="学籍辅号" width="160" />
            <el-table-column prop="studentName" label="姓名" width="120" />
            <el-table-column prop="relativePath" label="存储路径" min-width="260" show-overflow-tooltip />
            <el-table-column label="大小" width="100" align="right">
              <template #default="{ row }">{{ (row.fileSize / 1024).toFixed(0) }} KB</template>
            </el-table-column>
            <el-table-column label="状态" width="120" align="center">
              <template #default="{ row }">
                <el-tag
                  size="small"
                  :type="
                    row.status === PhotoStatuses.Normal
                      ? 'success'
                      : row.status === PhotoStatuses.Failed
                        ? 'danger'
                        : 'warning'
                  "
                >
                  {{ row.statusText }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="上传时间" width="170" align="center">
              <template #default="{ row }">{{ row.uploadedTime.replace('T', ' ').slice(0, 19) }}</template>
            </el-table-column>
            <el-table-column prop="uploadedByName" label="上传人" width="110" />
            <el-table-column prop="errorMessage" label="异常说明" min-width="180" show-overflow-tooltip />
            <el-table-column label="操作" width="150" align="center" fixed="right">
              <template #default="{ row }">
                <!--
                  「看原图」：表格里显示的是**缩略图**（480px），核对照片是否张冠李戴时
                  必须看原图。缩略图直接用 el-image 预览是看不出细节的。
                -->
                <el-button link type="primary" @click="viewOriginal(row)">看原图</el-button>
                <el-button v-if="canDelete" link type="danger" @click="removePhotos([row.id])">
                  删除
                </el-button>
              </template>
            </el-table-column>
          </el-table>

          <el-pagination
            v-model:current-page="ledgerPage"
            v-model:page-size="ledgerQuery.maxResultCount"
            class="photo-list__pager"
            :total="ledgerTotal"
            layout="total, sizes, prev, pager, next"
            :page-sizes="[20, 50, 100]"
            @current-change="loadLedger"
            @size-change="loadLedger"
          />
        </el-tab-pane>

        <!-- ---------------- 缺照片名单 ---------------- -->
        <el-tab-pane name="missing">
          <template #label>
            <span>缺照片名单</span>
            <el-badge v-if="missingTotal > 0" :value="missingTotal" type="danger" class="photo-list__badge" />
          </template>

          <div class="photo-list__toolbar">
            <SchoolSelect
              v-model="missingQuery.schoolOrgId"
              :schools="schools"
              placeholder="全部学校（按权限范围）"
              style="width: 260px"
            />

            <el-input
              v-model="missingQuery.keyword"
              placeholder="姓名 / 学籍辅号"
              clearable
              style="width: 200px"
              @keyup.enter="loadMissing"
            />

            <el-button type="primary" @click="loadMissing">查询</el-button>

            <div class="photo-list__spacer" />

            <el-button v-if="canUpload" type="primary" @click="openUploadDialog">
              <AppIcon name="UploadFilled" />
              <span>批量上传</span>
            </el-button>
          </div>

          <el-alert
            type="info"
            :closable="false"
            show-icon
            title="名单按「学生主表上没有照片路径」判定，因此总数与分页是准确的（不是先分页再过滤）。"
            class="photo-list__tip"
          />

          <el-table v-loading="loading" :data="missingRows" border stripe height="calc(100vh - 400px)">
            <el-table-column type="index" label="#" width="55" align="center" />
            <el-table-column prop="studentNo" label="学籍辅号" width="170" />
            <el-table-column prop="name" label="姓名" width="130" />
            <el-table-column prop="schoolName" label="学校" min-width="200" show-overflow-tooltip />
            <el-table-column prop="gradeName" label="年级" width="100" />
            <el-table-column prop="className" label="班级" width="100" />
            <el-table-column label="学籍状态" width="110">
              <template #default="{ row }">{{ row.statusName ?? row.status ?? '—' }}</template>
            </el-table-column>
          </el-table>

          <el-pagination
            v-model:current-page="missingPage"
            v-model:page-size="missingQuery.maxResultCount"
            class="photo-list__pager"
            :total="missingTotal"
            layout="total, sizes, prev, pager, next"
            :page-sizes="[50, 100, 200]"
            @current-change="loadMissing"
            @size-change="loadMissing"
          />
        </el-tab-pane>
      </el-tabs>
    </el-card>

    <!-- ---------------- 导出对话框 ---------------- -->
    <!-- 原图预览（表格里的是 480px 缩略图，核对身份必须看原图） -->
    <el-dialog
      v-model="originalVisible"
      :title="`原图 · ${originalTitle}`"
      width="720px"
      top="6vh"
      @closed="onOriginalClosed"
    >
      <div v-loading="originalLoading" class="photo-list__original">
        <el-image
          v-if="originalUrl"
          :src="originalUrl"
          fit="contain"
          class="photo-list__original-img"
        />
      </div>
      <template #footer>
        <el-button @click="originalVisible = false">关闭</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="exportDialogVisible" title="导出照片" width="560px">      <el-alert type="info" :closable="false" class="photo-list__export-tip">
        导出范围与当前台账筛选一致（关键字：{{ ledgerQuery.keyword || '不限' }}，
        状态：{{ statusOptions.find((s) => s.value === ledgerQuery.status)?.label ?? '全部' }}）。
        压缩包内含「照片清单.csv」，读不到的照片会明确标注为「文件缺失」。
      </el-alert>

      <el-form label-width="130px" class="photo-list__export-form">
        <el-form-item label="最多导出张数">
          <el-input-number v-model="exportOptions.maxCount" :min="1" :max="2000" :step="100" />
        </el-form-item>

        <el-form-item label="图片规格">
          <el-radio-group v-model="exportOptions.useThumbnail">
            <el-radio :value="false">原图（用于存档）</el-radio>
            <el-radio :value="true">缩略图（体积小，便于核对）</el-radio>
          </el-radio-group>
        </el-form-item>

        <el-form-item label="附缺照片名单">
          <el-switch v-model="exportOptions.includeMissingList" />
          <span class="photo-list__export-hint">同时给出"还没采集照片"的学生名单，便于催收</span>
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="exportDialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="exporting" @click="exportPhotos">开始导出</el-button>
      </template>
    </el-dialog>

    <!-- ---------------- 上传对话框 ---------------- -->
    <el-dialog v-model="uploadVisible" title="批量上传学生照片" width="900px" top="6vh">
      <el-alert
        type="info"
        :closable="false"
        show-icon
        title="文件名请用学籍辅号（如 4101020001000123.jpg）。系统会先把文件与学生对上，确认无误后再上传。"
        class="photo-list__tip"
      />

      <el-form label-width="80px" class="photo-list__upload-form">
        <el-form-item label="学校">
          <SchoolSelect
            v-model="uploadSchoolId"
            :schools="schools"
            placeholder="请选择学校"
            style="width: 300px"
            @change="uploadRows.length && matchStudents()"
          />

          <el-button class="photo-list__pick" :disabled="!uploadSchoolId" @click="pickFiles">
            <AppIcon name="FolderOpened" />
            <span>选择照片文件</span>
          </el-button>

          <input
            ref="fileInput"
            type="file"
            accept=".jpg,.jpeg,.png,.bmp"
            multiple
            hidden
            @change="onFilesPicked"
          />
        </el-form-item>
      </el-form>

      <div v-if="uploadRows.length > 0" class="photo-list__match-summary">
        共选择 {{ uploadRows.length }} 个文件，已匹配 {{ matchedCount }} 个。
      </div>

      <el-table v-if="uploadRows.length > 0" v-loading="matching" :data="uploadRows" border height="300">
        <el-table-column prop="file.name" label="文件名" min-width="220" show-overflow-tooltip />
        <el-table-column prop="studentNo" label="识别到的学籍辅号" width="180" />
        <el-table-column prop="studentName" label="匹配到的学生" width="140">
          <template #default="{ row }">
            <span v-if="row.studentName">{{ row.studentName }}</span>
            <span v-else class="photo-list__unmatched">—</span>
          </template>
        </el-table-column>
        <el-table-column label="匹配结果" width="170">
          <template #default="{ row }">
            <el-tag size="small" :type="row.matched ? 'success' : 'danger'">
              {{ row.matched ? row.note : row.note || '未匹配' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="上传结果" min-width="200">
          <template #default="{ row }">
            <template v-if="resultOf(row.studentId)">
              <el-tag
                size="small"
                :type="
                  !resultOf(row.studentId)!.success
                    ? 'danger'
                    : resultOf(row.studentId)!.degraded
                      ? 'warning'
                      : 'success'
                "
              >
                {{
                  resultOf(row.studentId)!.success
                    ? resultOf(row.studentId)!.degraded
                      ? '已暂存（待补传）'
                      : resultOf(row.studentId)!.skipped
                        ? '秒传（内容未变）'
                        : '上传成功'
                    : '失败'
                }}
              </el-tag>
              <span v-if="resultOf(row.studentId)!.errorMessage" class="photo-list__upload-error">
                {{ resultOf(row.studentId)!.errorMessage }}
              </span>
            </template>
            <span v-else class="photo-list__unmatched">—</span>
          </template>
        </el-table-column>
      </el-table>

      <el-progress
        v-if="uploading"
        :percentage="uploadPercent"
        :stroke-width="14"
        class="photo-list__progress"
      />

      <el-alert
        v-if="uploadResult"
        :type="uploadResult.failed > 0 ? 'warning' : 'success'"
        :closable="false"
        show-icon
        :title="uploadResult.message"
        class="photo-list__result"
      />

      <template #footer>
        <el-button @click="uploadVisible = false">关闭</el-button>
        <el-button
          type="primary"
          :loading="uploading"
          :disabled="matchedCount === 0"
          @click="doUpload"
        >
          上传 {{ matchedCount }} 张
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.photo-list__health {
  margin-bottom: 16px;
}

.photo-list__health-row {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.photo-list__health-actions {
  margin-left: auto;
  display: flex;
  gap: 8px;
}

.photo-list__toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
  flex-wrap: wrap;
}

.photo-list__toolbar .el-button {
  margin-left: 0;
}

.photo-list__spacer {
  flex: 1;
}

.photo-list__tip {
  margin-bottom: 12px;
}

.photo-list__badge {
  margin-left: 6px;
  margin-top: -2px;
}

.photo-list__pager {
  margin-top: 16px;
  justify-content: flex-end;
}

.photo-list__upload-form {
  margin-top: 16px;
}

.photo-list__pick {
  margin-left: 12px;
}

.photo-list__match-summary {
  margin-bottom: 8px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.photo-list__unmatched {
  color: var(--el-text-color-placeholder);
}

.photo-list__upload-error {
  margin-left: 6px;
  font-size: 12px;
  color: var(--el-color-danger);
}

.photo-list__progress {
  margin-top: 16px;
}

.photo-list__result {
  margin-top: 12px;
}
<style scoped>
.photo-list__export-tip {
  margin-bottom: 12px;
}

.photo-list__export-form {
  margin-top: 8px;
}

.photo-list__export-hint {
  margin-left: 10px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.photo-ledger__thumb {
  width: 44px;
  height: 58px;
  border-radius: 2px;
  cursor: pointer;
}

.photo-ledger__no-thumb {
  color: var(--el-text-color-secondary);
}

/* 原图预览：给一个固定高度，避免大图把对话框撑到屏幕外 */
.photo-list__original {
  min-height: 360px;
  max-height: 66vh;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--el-fill-color-light);
}

.photo-list__original-img {
  max-width: 100%;
  max-height: 66vh;
}
</style>