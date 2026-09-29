<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'

import { photoApi } from '@/api'
import type { PhotoStorageHealth } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'

/**
 * 主框架：左侧菜单 + 顶栏 + 内容区。
 *
 * 菜单来自登录时拉取的 `menus`，**不含任何前端写死的模块**。
 * 管理员在「模块管理」里停用一个模块，对应的菜单和路由就不存在了。
 */
const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

const collapsed = ref(false)

const menus = computed(() => auth.profile?.menus ?? [])

/**
 * 真正渲染到左侧菜单里的分组：**滤掉 `isHidden` 的模块**。
 *
 * 【为什么过滤放在渲染层，而不是让后端不下发】
 * 动态路由是用同一份 `menus` 注册的（见 `router/index.ts` 的 `registerDynamicRoutes`）。
 * 后端若直接把隐藏模块删掉，路由也一起没了 —— "隐藏菜单"就变成了"页面打不开"，
 * 而「模块管理」里这个复选框的本意是"不进菜单，但按 URL 仍可访问"
 * （典型场景：从别处跳进来的详情页、灰度中的模块）。
 * 因此后端照样下发 + 带 `isHidden` 标记，由这里决定画不画。
 *
 * 顺带把"整组都隐藏"的分组也去掉，避免留下一个点开是空的子系统。
 */
const visibleMenus = computed(() =>
  menus.value
    .map((group) => ({
      ...group,
      children: group.children.filter((item) => !item.isHidden)
    }))
    .filter((group) => group.children.length > 0)
)

const activePath = computed(() => route.path)

const currentTitle = computed(() => (route.meta.title as string | undefined) ?? '')

const showStorageHealth = computed(() => auth.can(MetadataPermissions.Pho.Photo.Query))

const health = ref<PhotoStorageHealth | null>(null)
let timer: number | undefined

async function refreshHealth(): Promise<void> {
  if (!showStorageHealth.value) {
    return
  }

  try {
    health.value = await photoApi.getStorageHealth()
  } catch {
    // 探针失败不打扰用户：头部指示器会显示为「未知」
    health.value = null
  }
}

onMounted(() => {
  void refreshHealth()

  // 存储健康度是运维信息，1 分钟一次足够，不需要长轮询
  timer = window.setInterval(() => void refreshHealth(), 60_000)
})

onBeforeUnmount(() => {
  if (timer) {
    window.clearInterval(timer)
  }
})

/** 顶栏上的存储状态：正常时只是一个绿点，异常时才吸引注意力。 */
const healthTag = computed(() => {
  if (!health.value) {
    return { type: 'info' as const, text: '存储状态未知' }
  }

  if (!health.value.primaryAvailable) {
    return { type: 'danger' as const, text: '照片存储异常（已降级）' }
  }

  if (health.value.fallbackPendingCount > 0) {
    return { type: 'warning' as const, text: `待补传 ${health.value.fallbackPendingCount} 张` }
  }

  return { type: 'success' as const, text: '照片存储正常' }
})

async function handleLogout(): Promise<void> {
  try {
    await ElMessageBox.confirm('确定要退出登录吗？', '提示', {
      confirmButtonText: '退出',
      cancelButtonText: '取消',
      type: 'warning'
    })
  } catch {
    return
  }

  auth.logout()
  ElMessage.success('已退出登录')
  await router.replace('/login')
}
</script>

<template>
  <el-container class="layout">
    <el-aside :width="collapsed ? '64px' : '220px'" class="layout__aside">
      <div class="layout__brand">
        <AppIcon name="School" />
        <span v-show="!collapsed" class="layout__brand-text">学籍管理系统</span>
      </div>

      <el-scrollbar class="layout__menu">
        <el-menu
          :default-active="activePath"
          :collapse="collapsed"
          :collapse-transition="false"
          router
          background-color="#1f2d3d"
          text-color="#c0c4cc"
          active-text-color="#ffffff"
        >
          <el-menu-item index="/dashboard">
            <AppIcon name="HomeFilled" />
            <template #title>工作台</template>
          </el-menu-item>

          <el-sub-menu v-for="group in visibleMenus" :key="group.code" :index="group.code">
            <template #title>
              <AppIcon :name="group.icon" />
              <span>{{ group.name }}</span>
            </template>

            <template v-for="item in group.children" :key="item.code">
              <!-- 外链菜单（如跳转到其它教育系统）不走路由 -->
              <el-menu-item v-if="item.isExternal" :index="item.path ?? item.code">
                <a :href="item.path ?? '#'" target="_blank" rel="noopener" class="layout__external">
                  <AppIcon :name="item.icon" />
                  <span>{{ item.name }}</span>
                </a>
              </el-menu-item>

              <el-menu-item v-else :index="item.path!">
                <AppIcon :name="item.icon" />
                <template #title>{{ item.name }}</template>
              </el-menu-item>
            </template>
          </el-sub-menu>
        </el-menu>
      </el-scrollbar>
    </el-aside>

    <el-container>
      <el-header class="layout__header">
        <div class="layout__header-left">
          <el-button text @click="collapsed = !collapsed">
            <AppIcon :name="collapsed ? 'Expand' : 'Fold'" />
          </el-button>
          <span class="layout__title">{{ currentTitle }}</span>
        </div>

        <div class="layout__header-right">
          <el-tag v-if="showStorageHealth" :type="healthTag.type" effect="plain" size="small">
            {{ healthTag.text }}
          </el-tag>

          <el-dropdown>
            <span class="layout__user">
              <AppIcon name="UserFilled" />
              <span>{{ auth.profile?.realName || auth.profile?.userName }}</span>
              <el-tag v-if="auth.profile?.orgType" size="small" type="info" effect="plain">
                {{ auth.profile.orgName }}
              </el-tag>
            </span>

            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item disabled>
                  账号：{{ auth.profile?.userName }}
                </el-dropdown-item>
                <el-dropdown-item disabled>
                  角色：{{ auth.profile?.roles.join('、') || '（未分配）' }}
                </el-dropdown-item>
                <el-dropdown-item divided @click="handleLogout">退出登录</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </el-header>

      <el-main class="layout__main">
        <router-view v-slot="{ Component }">
          <keep-alive :max="8">
            <component :is="Component" />
          </keep-alive>
        </router-view>
      </el-main>
    </el-container>
  </el-container>
</template>

<style scoped>
.layout {
  height: 100vh;
}

.layout__aside {
  background-color: #1f2d3d;
  transition: width 0.2s;
  overflow: hidden;
}

.layout__brand {
  display: flex;
  align-items: center;
  gap: 8px;
  height: 60px;
  padding: 0 18px;
  color: #fff;
  font-size: 16px;
  font-weight: 600;
  white-space: nowrap;
  background-color: #16222e;
}

.layout__brand-text {
  overflow: hidden;
}

.layout__menu {
  height: calc(100vh - 60px);
}

.layout__menu :deep(.el-menu) {
  border-right: none;
}

.layout__external {
  display: flex;
  align-items: center;
  gap: 6px;
  color: inherit;
  text-decoration: none;
}

.layout__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  border-bottom: 1px solid var(--el-border-color-light);
  background: #fff;
}

.layout__header-left,
.layout__header-right {
  display: flex;
  align-items: center;
  gap: 12px;
}

.layout__title {
  font-size: 15px;
  font-weight: 600;
}

.layout__user {
  display: flex;
  align-items: center;
  gap: 6px;
  cursor: pointer;
  outline: none;
}

.layout__main {
  padding: 16px;
  background-color: #f5f7fa;
  overflow: auto;
}
</style>
