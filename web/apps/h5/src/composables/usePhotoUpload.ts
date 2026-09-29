import { onBeforeUnmount, ref } from 'vue'
import type { Ref } from 'vue'
import { ElMessage } from 'element-plus'

import { photoApi } from '@xjgl/api'
import type { MissingPhotoStudent, PhotoUploadItemResult, StudentListItem } from '@xjgl/api'

/**
 * 拍照上传的**共用逻辑**：「按学籍辅号拍」与「按缺照片名单拍」两条入口共用。
 *
 * 【为什么必须抽出来，而不是在名单页再抄一遍】
 * 名单页要的是"点一个学生直接拍"。如果那段"确认姓名 → 选图 → 上传 → 如实显示结果"
 * 在名单页里复制一份，两条入口就会慢慢长出差：一边提示了"降级暂存"、另一边忘了提示，
 * 或者一边限制了 10MB、另一边没有 —— 而这类差异在这个业务里的代价最高
 * （拍错人很难发现、谎报成功会让老师以为照片已经进 NAS）。所以逻辑只有一份，
 * 页面只负责回答"这个学生是谁"。
 *
 * 【PhotoUploadTarget 为什么不是直接用 StudentListItem】
 * 两个入口拿到的**不是同一个 DTO**：
 *   - 按学籍辅号查 → `StudentListItem`（有性别、生僻字标记、是否已有照片）；
 *   - 缺照片名单 → `MissingPhotoStudent`（学籍状态名由后端翻译，且必然没有照片）。
 * 拿其中一个当参数类型，另一个就得伪造缺失字段，于是"确认姓名"那一屏会显示
 * 看起来像真的、其实是编出来的信息。因此统一成一个只含**展示 + 上传所需字段**
 * 的视图模型，由两个映射函数各自如实填写；缺失的字段留空，界面显示"—"。
 */
export interface PhotoUploadTarget {
  id: string
  studentNo?: string | null
  name?: string | null
  genderName?: string | null
  schoolName?: string | null
  gradeName?: string | null
  className?: string | null
  /** 学籍状态的中文名（两条入口的字段名不同，这里统一）。 */
  statusName?: string | null
  hasRareCharacter?: boolean
  /** 是否已有照片；缺照片名单入口按定义恒为 false。 */
  hasPhoto?: boolean
}

/** 搜索入口（`StudentListItem`）→ 上传目标。 */
export function toPhotoTarget(item: StudentListItem): PhotoUploadTarget {
  return {
    id: item.id,
    studentNo: item.studentNo,
    name: item.name,
    genderName: item.genderName,
    schoolName: item.schoolName,
    gradeName: item.gradeName,
    className: item.className,
    statusName: item.studentStatusName ?? item.studentStatusCode,
    hasRareCharacter: item.hasRareCharacter,
    hasPhoto: item.hasPhoto
  }
}

/** 缺照片名单入口（`MissingPhotoStudent`）→ 上传目标。 */
export function missingToPhotoTarget(item: MissingPhotoStudent): PhotoUploadTarget {
  return {
    id: item.studentId,
    studentNo: item.studentNo,
    name: item.name,
    schoolName: item.schoolName,
    gradeName: item.gradeName,
    className: item.className,
    statusName: item.statusName ?? item.status,
    // 名单是后端按"学生主表 PhotoPath 为空"筛出来的，所以"没有照片"是这份名单的
    // 语义本身，不是前端猜的；性别与生僻字标记确实拿不到，留空由界面显示"—"
    hasPhoto: false
  }
}

/**
 * 拍照 → 上传的状态机（不含"怎么找到这个学生"）。
 *
 * 上传成功后**不自动重置**：结果要留在屏幕上让老师自己确认（尤其是"降级暂存"）。
 * 状态的重置由调用方"点继续上传下一个学生 → 卸载本面板"完成 —— 卸载时下面
 * `onBeforeUnmount` 会把预览图的 objectURL 释放掉。
 *
 * `inputEl` 由调用方（组件）持有：隐藏的 `<input type="file">` 必须用模板 ref 绑定，
 * 而模板 ref 只能绑到组件自己的顶层变量上 —— 从组合式函数里解构出来的 ref
 * 在 `ref="x"` 上既不好读、vue-tsc 也会判成"未使用"（`noUnusedLocals`）。
 */
export function usePhotoUpload(inputEl: Ref<HTMLInputElement | null>) {
  const file = ref<File | null>(null)
  const previewUrl = ref<string | null>(null)
  const uploading = ref(false)
  const percent = ref(0)
  const result = ref<PhotoUploadItemResult | null>(null)

  function pickFile(): void {
    inputEl.value?.click()
  }

  function onFilePicked(event: Event): void {
    const input = event.target as HTMLInputElement
    const picked = input.files?.[0]

    // 必须立刻清空 input：否则"同一个文件再选一次"不会再触发 change，
    // 老师看到的是"点了选照片但什么都没发生"
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

  /** 上传给指定学生；返回这一条的结果（失败时为 null），调用方据此决定去留。 */
  async function upload(studentId: string): Promise<PhotoUploadItemResult | null> {
    if (!file.value) {
      ElMessage.warning('请先选择照片。')

      return null
    }

    uploading.value = true
    percent.value = 0

    try {
      const batch = await photoApi.upload(
        [file.value],
        [studentId],
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

      return result.value
    } finally {
      uploading.value = false
    }
  }

  // 预览图是 createObjectURL 出来的：面板被卸载（"继续上传下一个学生"就会卸载它）
  // 时必须释放，否则连着拍几十个学生会一路占着内存 —— 在老安卓机上就是越来越卡
  onBeforeUnmount(clearFile)

  return {
    file,
    previewUrl,
    uploading,
    percent,
    result,
    pickFile,
    onFilePicked,
    upload
  }
}
