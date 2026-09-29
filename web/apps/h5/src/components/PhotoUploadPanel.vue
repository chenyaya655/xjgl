<script setup lang="ts">
import { computed, ref } from 'vue'

import type { PhotoUploadItemResult } from '@xjgl/api'
import { usePhotoUpload } from '@/composables/usePhotoUpload'
import type { PhotoUploadTarget } from '@/composables/usePhotoUpload'

/**
 * 「确认学生 → 拍照 → 上传 → 如实显示结果」这一整块（两条入口共用的组件）。
 *
 * 为什么不把它做进 `usePhotoUpload` 而是留成组件：这一块的模板有 90 行左右
 * （取景区、隐藏的 file input、进度、结果提示），复制到名单页就等于把最容易出错的
 * 那段界面抄两遍。逻辑在 composable、界面在组件，两条入口各自只写"怎么找到学生"。
 *
 * `uploaded` 事件只表示**服务端说这条成功了**（含降级暂存），调用方据此决定
 * 要不要把学生从名单里去掉；`next` 表示老师点了"继续上传下一个"，由调用方决定
 * 是清空输入框还是回到名单。
 */
const props = defineProps<{ target: PhotoUploadTarget }>()

const emit = defineEmits<{
  (e: 'uploaded', item: PhotoUploadItemResult): void
  (e: 'next'): void
}>()

// 隐藏的 file input 只能绑到组件自己的顶层变量上，因此这个 ref 由组件持有、交给组合式函数使用
const fileInput = ref<HTMLInputElement | null>(null)

const {
  file,
  previewUrl,
  uploading,
  percent,
  result,
  pickFile,
  onFilePicked,
  upload
} = usePhotoUpload(fileInput)

/** 结果提示的文案与颜色：失败 / 降级暂存 / 秒传 / 成功，四种都要说清楚。 */
const resultTitle = computed(() => {
  const item = result.value

  if (!item) {
    return ''
  }

  if (!item.success) {
    return `上传失败：${item.errorMessage ?? '未知原因'}`
  }

  if (item.degraded) {
    return `已暂存（待补传）：${item.errorMessage ?? '存储暂不可用'}`
  }

  return item.skipped ? '该照片已存在（内容未变）' : '上传成功'
})

const resultType = computed<'success' | 'warning' | 'error'>(() => {
  const item = result.value

  if (!item) {
    return 'success'
  }

  if (!item.success) {
    return 'error'
  }

  return item.degraded ? 'warning' : 'success'
})

async function doUpload(): Promise<void> {
  const item = await upload(props.target.id)

  if (item) {
    emit('uploaded', item)
  }
}
</script>

<template>
  <!-- ---------------- 确认学生（照片装错人是很难发现的错误，必须让老师看一眼） ---------------- -->
  <el-card shadow="never" class="photo__card">
    <div>
      <!-- 从名单点进来的老师一个字都没输过，必须明确要求他核对一眼 -->
      <div class="h5-muted photo__confirm">请确认是这名学生</div>
      <div class="photo__student-name">
        {{ target.name || '—' }}
        <el-tag v-if="target.hasRareCharacter" size="small" type="warning" effect="plain">
          生僻字
        </el-tag>
      </div>
      <div class="h5-muted">
        {{ target.studentNo || '—' }}
        <template v-if="target.genderName"> · {{ target.genderName }}</template>
        <template v-if="target.schoolName"> · {{ target.schoolName }}</template>
      </div>
      <div class="h5-muted">
        {{ target.gradeName || '—' }} {{ target.className || '' }} ·
        {{ target.statusName || '—' }}
      </div>
      <el-tag
        class="photo__photo-state"
        size="small"
        :type="target.hasPhoto ? 'info' : 'danger'"
        effect="plain"
      >
        {{ target.hasPhoto ? '已有照片（上传将覆盖）' : '暂无照片' }}
      </el-tag>
    </div>
  </el-card>

  <!-- ---------------- 拍照并上传 ---------------- -->
  <el-card shadow="never" class="photo__card">
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
      <el-button size="large" type="primary" :loading="uploading" :disabled="!file" @click="doUpload">
        上传
      </el-button>
    </div>

    <el-progress
      v-if="uploading"
      :percentage="percent"
      :stroke-width="12"
      class="photo__progress"
    />

    <el-alert
      v-if="result"
      class="photo__tip"
      :closable="false"
      show-icon
      :type="resultType"
      :title="resultTitle"
    />

    <el-button v-if="result?.success" text type="primary" class="photo__next" @click="emit('next')">
      继续上传下一个学生
    </el-button>
  </el-card>
</template>

<style scoped>
.photo__card {
  margin-bottom: 12px;
  border-radius: 10px;
}

.photo__tip {
  margin-top: 12px;
}

.photo__confirm {
  margin-bottom: 4px;
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
