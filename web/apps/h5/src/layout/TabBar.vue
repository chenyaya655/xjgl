<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import { changeFlowApi, MetadataPermissions } from '@xjgl/api'
import { useAuthStore } from '@/stores/auth'

/**
 * 底部页签 + 待办数量角标。
 *
 * 为什么把待办数量放在页签上、并在进入页面时刷新：
 *   教师打开手机端最想知道的一件事就是"有没有轮到我审"。
 *   把数字显示在页签上，他不用点进去就知道要不要处理 ——
 *   这是移动端最重要的一个设计点（省掉一次点击 = 省掉一次等待）。
 *
 * 注意角标只在**有权限查询待办**时才去请求：普通教师（非审核角色）
 * 没有 Chg.Audit.Query 权限，硬请求会得到一个 403 弹窗，属于自找麻烦。
 */
const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

/**
 * 当前高亮的页签。
 *
 * 优先读 `meta.tab`：`photo/missing` 这类**子页面**的路由名不等于页签名，
 * 若直接拿 route.name 比对，进入子页面时三个页签会全部不高亮 ——
 * 老师会以为自己点丢了、然后反复点页签。子页面属于哪个页签，由路由自己声明。
 */
const active = computed(
  () => (route.meta.tab as string | undefined) ?? (route.name as string) ?? 'todo'
)
const todoCount = ref(0)

const canQueryAudit = computed(() => auth.can(MetadataPermissions.Chg.Audit.Query))

async function refreshTodoCount(): Promise<void> {
  if (!canQueryAudit.value) {
    return
  }

  try {
    // 只要总数，因此 maxResultCount 取 1（后端返回 totalCount）
    const result = await changeFlowApi.getTodoList({ skipCount: 0, maxResultCount: 1 })
    todoCount.value = result.totalCount
  } catch {
    todoCount.value = 0
  }
}

function go(name: string): void {
  void router.replace({ name })
}

onMounted(() => {
  void refreshTodoCount()
})

// 每次切换页签都刷新一次角标：审完一条回到待办列表，数字应该立刻变小
watch(
  () => route.name,
  () => {
    void refreshTodoCount()
  }
)
</script>

<template>
  <div class="tabbar">
    <div class="tabbar__content">
      <router-view />
    </div>

    <nav class="tabbar__bar">
      <button class="tabbar__item" :class="{ 'is-active': active === 'todo' }" @click="go('todo')">
        <el-badge :value="todoCount" :hidden="todoCount === 0" :max="99">
          <el-icon :size="22"><Document /></el-icon>
        </el-badge>
        <span>待办</span>
      </button>

      <button class="tabbar__item" :class="{ 'is-active': active === 'photo' }" @click="go('photo')">
        <el-icon :size="22"><Camera /></el-icon>
        <span>拍照上传</span>
      </button>

      <button class="tabbar__item" :class="{ 'is-active': active === 'mine' }" @click="go('mine')">
        <el-icon :size="22"><User /></el-icon>
        <span>我的</span>
      </button>
    </nav>
  </div>
</template>

<style scoped>
.tabbar {
  height: 100%;
  display: flex;
  flex-direction: column;
}

.tabbar__content {
  flex: 1;
  overflow-y: auto;
  -webkit-overflow-scrolling: touch;
}

.tabbar__bar {
  display: flex;
  border-top: 1px solid var(--el-border-color-light);
  background: #fff;
  /* 适配 iPhone 底部安全区，否则最后一行会被 Home Indicator 挡住 */
  padding-bottom: env(safe-area-inset-bottom);
}

.tabbar__item {
  flex: 1;
  border: none;
  background: transparent;
  padding: 8px 0 6px;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
  color: var(--el-text-color-secondary);
  font-size: 11px;
  cursor: pointer;
}

.tabbar__item.is-active {
  color: var(--el-color-primary);
}
</style>
