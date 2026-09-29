<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { useRouter } from 'vue-router'

import { MetadataPermissions, photoApi, studentApi } from '@xjgl/api'
import PhotoUploadPanel from '@/components/PhotoUploadPanel.vue'
import { toPhotoTarget } from '@/composables/usePhotoUpload'
import type { PhotoUploadTarget } from '@/composables/usePhotoUpload'
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
 * 第 2、3 步在 `PhotoUploadPanel` 里（缺照片名单那条入口也用它，逻辑只有一份）。
 *
 * 【本页有两个入口，为什么"缺照片名单"是子页面而不是第四个页签】
 *   输入学籍辅号的前提是老师**已经知道谁缺照片**，而这恰恰是他不知道的：
 *   现实里他拿着纸质名册一个个对。所以名单是这条业务链上更自然的起点。
 *   但名单不该变成一个页签：
 *     - 四个页签在 320px 宽下会把「拍照上传」挤成两行，而它只是"把照片补齐"
 *       这一个任务的队列，不是应用的一级功能区；
 *     - 本页原有的"按学籍辅号拍"依然要保留（教师手上就是辅号时它更快）。
 *   于是名单做成子页面 `/#/photo/missing`：页签结构不变，名单与拍摄各占一整屏，
 *   "返回"有确定落点。名单入口放在本页最上方并带总人数 —— 与页签角标同一个道理：
 *   老师不用点进去就知道"今天还有多少人要拍"。
 */
const auth = useAuthStore()
const router = useRouter()

const canUpload = computed(() => auth.can(MetadataPermissions.Pho.Photo.Upload))
const canQuery = computed(() => auth.can(MetadataPermissions.Pho.Photo.Query))

/**
 * 缺照片人数。
 *
 * 只在有查询权限时才请求：普通教师没有 Pho.Photo.Query，硬请求会得到一个 403 弹窗，
 * 属于自找麻烦（与 TabBar 里的待办角标同一个处理）。
 * 这里不需要在别处维护这个数字：从名单子页面返回时本组件会重新挂载，
 * onMounted 再拉一次 —— 让服务端说话，别在前端算账。
 */
const missingCount = ref<number | null>(null)

const studentNo = ref('')
const searching = ref(false)
const student = ref<PhotoUploadTarget | null>(null)
const notFound = ref(false)

async function search(): Promise<void> {
  const keyword = studentNo.value.trim()

  if (!keyword) {
    ElMessage.warning('请输入学籍辅号。')

    return
  }

  searching.value = true
  notFound.value = false
  student.value = null

  try {
    const found = await studentApi.getList({ keyword, maxResultCount: 10 })

    // 必须精确匹配学籍辅号：keyword 也会命中姓名，模糊匹配会让老师拍错人
    const exact = found.items.find((s) => s.studentNo === keyword)

    if (exact) {
      student.value = toPhotoTarget(exact)
    } else {
      notFound.value = true
    }
  } finally {
    searching.value = false
  }
}

function reset(): void {
  studentNo.value = ''
  student.value = null
  notFound.value = false
}

function openMissingList(): void {
  void router.push({ name: 'photo-missing' })
}

onMounted(async () => {
  if (!canQuery.value) {
    return
  }

  try {
    // 只要总数，因此 maxResultCount 取 1（后端返回 totalCount）
    const result = await photoApi.getMissingList({ skipCount: 0, maxResultCount: 1 })

    missingCount.value = result.totalCount
  } catch {
    // 拉不到就不显示人数（入口本身仍然可用）—— 报错已由 http 拦截器统一提示
    missingCount.value = null
  }
})
</script>

<template>
  <div class="h5-page">
    <h2 class="h5-title">拍照上传学生照片</h2>

    <!--
      两种权限要分开说：只有查询权限的教师（如教务查看岗）**能看名单但不能补拍**，
      告诉他"本页不可用"是错的 —— 名单那部分对他是有用的。
    -->
    <el-alert
      v-if="!canUpload"
      type="warning"
      :closable="false"
      show-icon
      class="photo__card"
      :title="
        canQuery
          ? '您没有照片上传权限，只能查看缺照片名单。'
          : '您没有照片上传权限，本页不可用。'
      "
    />

    <!-- ---------------- 缺照片名单入口（先回答"还有谁没拍"） ---------------- -->
    <el-card
      v-if="canQuery"
      shadow="never"
      class="photo__card photo__entry"
      @click="openMissingList"
    >
      <div class="photo__entry-inner">
        <div class="photo__entry-main">
          <div class="photo__entry-title">缺照片名单</div>
          <div class="h5-muted">
            <template v-if="missingCount !== null">还有 {{ missingCount }} 名学生没有照片</template>
            <template v-else>按名单逐个补拍，不用输学籍辅号</template>
          </div>
        </div>
        <span class="photo__entry-go">›</span>
      </div>
    </el-card>

    <template v-if="canUpload">
      <!-- ---------------- 第一步：查学生 ---------------- -->
      <el-card shadow="never" class="photo__card">
        <!-- 两个入口并存时，必须让老师一眼看出这是"另一种开始方式" -->
        <div v-if="canQuery" class="h5-muted photo__step">或：直接输入学籍辅号</div>

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
      </el-card>

      <!-- ---------------- 第二、三步：确认学生 → 拍照上传 ---------------- -->
      <PhotoUploadPanel v-if="student" :target="student" @next="reset" />
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

.photo__step {
  margin-bottom: 8px;
}

.photo__tip {
  margin-top: 12px;
}

/* 入口卡片：整张可点，右侧一个 › 提示可进入（触摸端不靠 hover） */
.photo__entry {
  cursor: pointer;
}

.photo__entry-inner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.photo__entry-main {
  min-width: 0;
}

.photo__entry-title {
  font-size: 16px;
  font-weight: 600;
  margin-bottom: 2px;
}

.photo__entry-go {
  color: var(--el-text-color-secondary);
  font-size: 20px;
  line-height: 1;
}
</style>
