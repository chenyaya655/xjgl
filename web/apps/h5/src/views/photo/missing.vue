<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'

import { MetadataPermissions, photoApi } from '@xjgl/api'
import type { MissingPhotoStudent, PhotoUploadItemResult } from '@xjgl/api'
import PhotoUploadPanel from '@/components/PhotoUploadPanel.vue'
import { missingToPhotoTarget } from '@/composables/usePhotoUpload'
import type { PhotoUploadTarget } from '@/composables/usePhotoUpload'
import { useAuthStore } from '@/stores/auth'

/**
 * 缺照片名单 —— 教师"补照片"这条路径的入口（ROADMAP 10）。
 *
 * 【为什么是子页面，而不是第四个页签 / 页内模式切换】
 *   - 第四个页签：320px 宽下四个页签会把「拍照上传」挤成两行，而这份名单只是
 *     **一个任务的队列**（把照片补齐），不是应用的一级功能区；
 *   - 页内模式切换：名单与"确认学生 + 取景"两块都要整屏高度（取景区 260px），
 *     塞进同一个滚动容器里，"切回来时选中的学生还在不在"很难解释清楚；
 *   - 子页面：三个页签的结构不动（端到端用例钉着这三个），返回有明确落点，
 *     并且与既有页面"一屏只做一件事"的约定一致 —— 底部页签仍然可见、
 *     仍然高亮「拍照上传」（靠路由的 meta.tab，见 router/index.ts）。
 *
 * 【这个页面真正解决的问题】
 * 原来的拍照上传只支持"输入学籍辅号 → 拍照"，于是老师得先知道**谁缺照片**
 * （多半是拿着纸质名册一个个对）才能开始拍。这里直接把名单摊开：点一个学生
 * 就进入"确认姓名 → 拍照 → 上传"，全程不用输一个字。
 */
const auth = useAuthStore()
const router = useRouter()

const canQuery = computed(() => auth.can(MetadataPermissions.Pho.Photo.Query))
const canUpload = computed(() => auth.can(MetadataPermissions.Pho.Photo.Upload))

const loading = ref(false)
const rows = ref<MissingPhotoStudent[]>([])
const total = ref(0)
const keyword = ref('')

/** 一次 20 条：移动端一次拉太多既慢又没人会翻到底，翻不完就"加载更多" */
const pageSize = 20
const skipCount = ref(0)

async function load(reset = false): Promise<void> {
  if (!canQuery.value) {
    return
  }

  loading.value = true

  try {
    if (reset) {
      skipCount.value = 0
      rows.value = []
    }

    const result = await photoApi.getMissingList({
      skipCount: skipCount.value,
      maxResultCount: pageSize,
      keyword: keyword.value.trim() || undefined,
      // 按学籍辅号排序：与班主任手里的名册顺序一致，便于逐行核对
      sorting: 'StudentNo ASC'
    })

    // 追加而不是替换：移动端的"加载更多"是连续的
    rows.value = skipCount.value === 0 ? result.items : [...rows.value, ...result.items]
    total.value = result.totalCount
  } finally {
    loading.value = false
  }
}

function search(): void {
  skipCount.value = 0
  void load(true)
}

function loadMore(): void {
  skipCount.value += pageSize
  void load()
}

const hasMore = computed(() => rows.value.length < total.value)

// ---------------------------------------------------------------------------
// 选中 → 上传
// ---------------------------------------------------------------------------

const selected = ref<PhotoUploadTarget | null>(null)

function openStudent(row: MissingPhotoStudent): void {
  // 没有上传权限的人只应该"看到名单"，不该进入拍摄界面（否则点了上传才吃 403）
  if (!canUpload.value) {
    return
  }

  selected.value = missingToPhotoTarget(row)
}

function backToList(): void {
  selected.value = null
}

function goBack(): void {
  // 用 replace 而不是 back()：老师可能从微信里直接打开这个地址，
  // 此时 back() 会退出应用（hash 路由下表现为白屏），replace 有确定落点
  void router.replace({ name: 'photo' })
}

/**
 * 上传成功后把这个人从名单里去掉。
 *
 * 【为什么是"去掉"而不是"刷新整个列表"】
 * 后端在"成功"与"降级暂存"两条路径上都会写学生主表的 PhotoPath，而缺照片名单
 * 正是按 PhotoPath 为空筛的 —— 也就是说服务端已经认为他有照片了，把他留在
 * 名单里才是假信息。相反，秒传（内容未变）那条路径**不写** PhotoPath，
 * 所以这种人保留在名单里，让下次加载以服务端为准。
 */
function onUploaded(item: PhotoUploadItemResult): void {
  if (!item.success || item.skipped) {
    return
  }

  rows.value = rows.value.filter((r) => r.studentId !== item.studentId)
  total.value = Math.max(0, total.value - 1)
}

onMounted(() => {
  void load(true)
})
</script>

<template>
  <div class="h5-page">
    <div class="miss__head">
      <h2 class="h5-title">缺照片名单</h2>
      <el-button size="small" @click="goBack">返回</el-button>
    </div>

    <el-alert
      v-if="!canQuery"
      type="warning"
      :closable="false"
      show-icon
      title="您没有照片查询权限，本页不可用。"
    />

    <!-- ---------------- 拍摄：确认学生 → 拍照 → 上传 ---------------- -->
    <template v-else-if="selected">
      <div class="miss__crumb">
        <el-button text type="primary" @click="backToList">‹ 返回名单</el-button>
      </div>

      <PhotoUploadPanel :target="selected" @uploaded="onUploaded" @next="backToList" />
    </template>

    <!-- ---------------- 名单 ---------------- -->
    <template v-else>
      <el-alert
        v-if="!canUpload"
        type="info"
        :closable="false"
        show-icon
        class="miss__notice"
        title="您只有查看权限：如需补拍，请联系管理员开通照片上传权限。"
      />

      <el-card shadow="never" class="miss__filter">
        <div class="miss__row">
          <el-input
            v-model="keyword"
            size="large"
            placeholder="学籍辅号或姓名"
            clearable
            @keyup.enter="search"
          />
          <el-button size="large" type="primary" :loading="loading" @click="search">查询</el-button>
        </div>

        <div class="miss__count">共 {{ total }} 名学生缺照片</div>
        <div v-if="canUpload" class="h5-muted miss__count">
          点击学生即可为他拍照上传（不用输学籍辅号）
        </div>
      </el-card>

      <el-empty
        v-if="!loading && rows.length === 0"
        :description="keyword ? '没有匹配的缺照片学生' : '没有学生缺照片'"
        :image-size="80"
      />

      <el-card
        v-for="row in rows"
        :key="row.studentId"
        shadow="never"
        class="miss__card"
        :class="{ 'is-tappable': canUpload }"
        @click="openStudent(row)"
      >
        <div class="miss__line1">
          <span class="miss__name">{{ row.name || '—' }}</span>
          <span class="h5-muted">{{ row.studentNo || '—' }}</span>
          <span v-if="canUpload" class="miss__go">拍照 ›</span>
        </div>

        <div class="h5-muted">
          {{ row.gradeName || '—' }} {{ row.className || '' }}
        </div>

        <div class="miss__line3">
          <span class="h5-muted miss__school">{{ row.schoolName || '—' }}</span>
          <el-tag size="small" type="info" effect="plain">
            {{ row.statusName || row.status || '—' }}
          </el-tag>
        </div>
      </el-card>

      <div v-if="hasMore" class="miss__more">
        <el-button text type="primary" :loading="loading" @click="loadMore">
          加载更多（还有 {{ total - rows.length }} 人）
        </el-button>
      </div>
    </template>
  </div>
</template>

<style scoped>
.miss__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.miss__notice,
.miss__crumb {
  margin-bottom: 12px;
}

.miss__filter {
  margin-bottom: 10px;
  border-radius: 10px;
}

.miss__row {
  display: flex;
  gap: 8px;
}

.miss__count {
  margin-top: 10px;
}

.miss__card {
  margin-bottom: 10px;
  border-radius: 10px;
}

/* 可点：整张卡片都是触摸目标（≥44px 高），不依赖 hover 提示 */
.miss__card.is-tappable {
  cursor: pointer;
}

.miss__line1 {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  margin-bottom: 4px;
}

.miss__name {
  font-size: 16px;
  font-weight: 600;
}

.miss__go {
  margin-left: auto;
  color: var(--el-color-primary);
  font-size: 13px;
}

.miss__line3 {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin-top: 6px;
}

/* 学校名可能很长：必须允许收缩与断行，否则 320px 宽下会横向溢出 */
.miss__school {
  min-width: 0;
  overflow-wrap: anywhere;
}

.miss__more {
  text-align: center;
  padding: 12px 0;
}
</style>
