<script setup lang="ts">
import { computed, ref } from 'vue'
import { ElMessage } from 'element-plus'

import { MetadataPermissions, photoApi, studentApi } from '@xjgl/api'
import type { PhotoUploadItemResult, StudentListItem } from '@xjgl/api'
import { useAuthStore } from '@/stores/auth'

/**
 * 拍照上传（移动端最有价值的场景）。
 *
 * 与 PC 端的批量上传**刻意不同**：PC 端是"一次拖 50 个文件、按文件名批量匹配"，
 * 移动端是"对着一个学生拍一张、立刻确认是不是他"。
 * 两者的差异不是技术限制，而是使用场景：手机在学校现场，一次只处理一个学生。
 *
 * 因此流程被设计成三步，且第二步必须人工确认：
 *   1) 输入学籍辅号 → 查到学生；
 *   2) **确认学生姓名**（照片装错人是很难发现的错误，必须让老师看一眼）；
 *   3) 拍照/选图 → 上传 → 如实显示结果（含"降级暂存"）。
 */
const auth = useAuthStore()

const canUpload = computed(() => auth.can(MetadataPermissions.Pho.Photo.Upload))

const studentNo = ref('')
const searching = ref(false)
const student = ref<StudentListItem | null>(null)
const notFound = ref(false)

const fileInput = ref<HTMLInputElement | null>(null)
const file = ref<File | null>(null)
const previewUrl = ref<string | null>(null)

const uploading = ref(false)
const percent = ref(0)
const result = ref<PhotoUploadItemResult | null>(null)

async function search(): Promise<void> {
  const keyword = studentNo.value.trim()

  if (!keyword) {
    ElMessage.warning('请输入学籍辅号。')

    return
  }

  searching.value = true
  notFound.value = false
  student.value = null
  result.value = null
  clearFile()

  try {
    const found = await studentApi.getList({ keyword, maxResultCount: 10 })

    // 必须精确匹配学籍辅号：keyword 也会命中姓名，模糊匹配会让老师拍错人
    const exact = found.items.find((s) => s.studentNo === keyword)

    if (exact) {
      student.value = exact
    } else {
      notFound.value = true
    }
  } finally {
    searching.value = false
  }
}

function pickFile(): void {
  fileInput.value?.click()
}

function onFilePicked(event: Event): void {
  const input = event.target as HTMLInputElement
  const picked = input.files?.[0]

  input.value = ''

  if (!picked) {
    return
  }

  if (picked.size > 10 * 1024 * 1024) {
    ElMessage.warning('照片不能超过 10MB，请压缩后再上传。')

    return
  }

  clearFile()
  file.value = picked
  previewUrl.value = URL.createObjectURL(picked)
  result.value = null
}

function clearFile(): void {
  if (previewUrl.value) {
    URL.revokeObjectURL(previewUrl.value)
  }

  previewUrl.value = null
  file.value = null
}

async function upload(): Promise<void> {
  if (!student.value || !file.value) {
    ElMessage.warning('请先查到学生并选择照片。')

    return
  }

  uploading.value = true
  percent.value = 0

  try {
    const batch = await photoApi.upload(
      [file.value],
      [student.value.id],
      crypto.randomUUID(),
      (p) => (percent.value = p)
    )

    result.value = batch.items[0] ?? null

    if (batch.failed > 0) {
      ElMessage.error('上传失败，请重试')
    } else if (batch.degraded > 0) {
      // 如实告知"暂存"，不要谎报成功 —— 教师有权知道照片还没进 NAS
      ElMessage.warning('存储暂不可用，照片已暂存，系统会自动补传')
    } else if (batch.skipped > 0) {
      ElMessage.success('该照片已存在（内容未变），无需重复上传')
    } else {
      ElMessage.success('上传成功')
    }
  } finally {
    uploading.value = false
  }
}

function reset(): void {
  studentNo.value = ''
  student.value = null
  notFound.value = false
  result.value = null
  clearFile()
}
</script>

<template>
  <div class="h5-page">
    <h2 class="h5-title">拍照上传学生照片</h2>

    <el-alert
      v-if="!canUpload"
      type="warning"
      :closable="false"
      show-icon
      title="您没有照片上传权限，本页不可用。"
    />

    <template v-else>
      <!-- ---------------- 第一步：查学生 ---------------- -->
      <el-card shadow="never" class="photo__card">
        <div class="photo__row">
          <el-input
            v-model="studentNo"
            size="large"
            placeholder="输入学籍辅号"
            clearable
            @keyup.enter="search"
          />
          <el-button size="large" type="primary" :loading="searching" @click="search">查找</el-button>
        </div>

        <el-alert
          v-if="notFound"
          type="warning"
          :closable="false"
          show-icon
          class="photo__tip"
          title="没有找到该学籍辅号对应的学生（注意：学籍辅号必须完全一致）。"
        />

        <!-- ---------------- 第二步：确认学生 ---------------- -->
        <div v-if="student" class="photo__student">
          <div class="photo__student-name">
            {{ student.name }}
            <el-tag v-if="student.hasRareCharacter" size="small" type="warning" effect="plain">
              生僻字
            </el-tag>
          </div>
          <div class="h5-muted">
            {{ student.studentNo }} · {{ student.genderName }} · {{ student.schoolName }}
          </div>
          <div class="h5-muted">
            {{ student.gradeName || '—' }} {{ student.className || '' }} ·
            {{ student.studentStatusName || student.studentStatusCode }}
          </div>
          <el-tag
            class="photo__photo-state"
            size="small"
            :type="student.photoPath ? 'info' : 'danger'"
            effect="plain"
          >
            {{ student.photoPath ? '已有照片（上传将覆盖）' : '暂无照片' }}
          </el-tag>
        </div>
      </el-card>

      <!-- ---------------- 第三步：拍照并上传 ---------------- -->
      <el-card v-if="student" shadow="never" class="photo__card">
        <div class="photo__preview">
          <img v-if="previewUrl" :src="previewUrl" alt="待上传照片" />
          <div v-else class="photo__placeholder" @click="pickFile">
            <el-icon :size="34"><Camera /></el-icon>
            <span>点击拍照或从相册选择</span>
          </div>
        </div>

        <!--
          capture="environment" 会直接唤起后置摄像头。
          只在移动端生效，桌面浏览器会退化成普通文件选择框 —— 这也方便在电脑上联调。
        -->
        <input
          ref="fileInput"
          type="file"
          accept="image/*"
          capture="environment"
          hidden
          @change="onFilePicked"
        />

        <div class="photo__actions">
          <el-button size="large" :disabled="uploading" @click="pickFile">
            {{ file ? '重新选择' : '选择照片' }}
          </el-button>
          <el-button
            size="large"
            type="primary"
            :loading="uploading"
            :disabled="!file"
            @click="upload"
          >
            上传
          </el-button>
        </div>

        <el-progress v-if="uploading" :percentage="percent" :stroke-width="12" class="photo__progress" />

        <el-alert
          v-if="result"
          class="photo__tip"
          :closable="false"
          show-icon
          :type="!result.success ? 'error' : result.degraded ? 'warning' : 'success'"
          :title="
            !result.success
              ? `上传失败：${result.errorMessage ?? '未知原因'}`
              : result.degraded
                ? `已暂存（待补传）：${result.errorMessage ?? '存储暂不可用'}`
                : result.skipped
                  ? '该照片已存在（内容未变）'
                  : '上传成功'
          "
        />

        <el-button v-if="result?.success" text type="primary" class="photo__next" @click="reset">
          继续上传下一个学生
        </el-button>
      </el-card>
    </template>
  </div>
</template>

<style scoped>
.photo__card {
  margin-bottom: 12px;
  border-radius: 10px;
}

.photo__row {
  display: flex;
  gap: 8px;
}

.photo__tip {
  margin-top: 12px;
}

.photo__student {
  margin-top: 14px;
}

.photo__student-name {
  font-size: 18px;
  font-weight: 600;
  display: flex;
  align-items: center;
  gap: 6px;
}

.photo__photo-state {
  margin-top: 8px;
}

.photo__preview {
  width: 100%;
  height: 260px;
  border: 1px dashed var(--el-border-color);
  border-radius: 10px;
  overflow: hidden;
  display: flex;
  align-items: center;
  justify-content: center;
  background: #fafafa;
}

.photo__preview img {
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.photo__placeholder {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.photo__actions {
  display: flex;
  gap: 8px;
  margin-top: 14px;
}

.photo__actions .el-button {
  flex: 1;
}

.photo__progress {
  margin-top: 14px;
}

.photo__next {
  margin-top: 10px;
  width: 100%;
}
</style>
