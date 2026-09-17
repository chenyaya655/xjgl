import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'

import { setUnauthorizedHandler } from '@/api/http'
import type { MenuItem } from '@/api/types'
import { useAuthStore } from '@/stores/auth'

/**
 * 路由与菜单 —— **完全由后端元数据驱动**。
 *
 * 为什么不在前端写死路由表：
 *   本系统的「模块管理」是一个业务功能（管理员可以新增模块、改路径、改图标、
 *   启停模块）。如果路由写死在前端，管理员在界面上改的东西就不会生效，
 *   「模块管理」这个功能就退化成一个摆设。
 *
 * 因此：登录后拉一次 `/api/app/my-profile`，拿到**当前用户有权访问的菜单树**，
 * 再据此动态注册路由。这样三件事天然一致：
 *   路由、菜单、权限（后端只返回你有权限的菜单和操作码）。
 */

/** 视图组件集合。Vite 在构建期静态分析 glob，因此路径必须是字面量。 */
const viewModules = import.meta.glob('../views/**/*.vue')

/**
 * 把元数据里的 `component` 字符串解析成真实组件。
 *
 * 约定与后端种子数据一致（`sys_module.component`）：
 *   `student/list/index` → `src/views/student/list/index.vue`
 *   `photo/list/index`   → `src/views/photo/list/index.vue`
 *
 * 解析不到时**不抛错**，而是回落到占位页：
 *   模块是运行期数据，管理员完全可能先建好模块再等开发补页面；
 *   此时让用户看到一个「该模块尚未实现」的页面，远好过一个白屏或报错。
 */
function resolveView(component?: string | null) {
  if (!component) {
    return () => import('@/views/Placeholder.vue')
  }

  const normalized = component.replace(/^\/+|\/+$/g, '')

  const candidates = [`../views/${normalized}.vue`, `../views/${normalized}/index.vue`]

  for (const candidate of candidates) {
    if (viewModules[candidate]) {
      return viewModules[candidate]
    }
  }

  return () => import('@/views/Placeholder.vue')
}

function toRoute(item: MenuItem, groupName: string, groupCode: string): RouteRecordRaw | null {
  // 外链菜单不走前端路由，由布局里的 <a> 直接打开
  if (!item.path || item.isExternal) {
    return null
  }

  return {
    path: item.path,
    name: item.code,
    component: resolveView(item.component),
    meta: {
      title: item.name,
      icon: item.icon,
      groupName,
      groupCode,
      // 该页面需要哪些操作权限（用于按钮级控制）
      actions: item.actions
    }
  }
}

const staticRoutes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'login',
    component: () => import('@/views/login/index.vue'),
    meta: { title: '登录', anonymous: true }
  },
  {
    path: '/',
    name: 'layout',
    component: () => import('@/layout/MainLayout.vue'),
    redirect: '/dashboard',
    children: [
      {
        path: 'dashboard',
        name: 'dashboard',
        component: () => import('@/views/dashboard/index.vue'),
        meta: { title: '工作台', icon: 'HomeFilled' }
      }
    ]
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    component: () => import('@/views/error/NotFound.vue'),
    meta: { title: '页面不存在', anonymous: true }
  }
]

export const router = createRouter({
  history: createWebHistory(),
  routes: staticRoutes,
  scrollBehavior: () => ({ top: 0 })
})

/** 已经动态注册过的路由名，避免重复 addRoute 造成路由表膨胀。 */
const registered = new Set<string>()

/** 按当前用户的菜单树注册路由。 */
export function registerDynamicRoutes(menus: { code: string; name: string; children: MenuItem[] }[]) {
  for (const group of menus) {
    for (const item of group.children) {
      const route = toRoute(item, group.name, group.code)

      if (!route || registered.has(String(route.name))) {
        continue
      }

      // 挂到 layout 之下：这样动态页面也带侧边栏与顶栏
      router.addRoute('layout', route)
      registered.add(String(route.name))
    }
  }
}

let unauthorizedHandlerRegistered = false

router.beforeEach(async (to) => {
  const auth = useAuthStore()

  if (!unauthorizedHandlerRegistered) {
    // 令牌彻底失效（刷新也失败）时由 http 层回调到这里
    setUnauthorizedHandler(() => {
      auth.logout()
      registered.clear()
      void router.replace({ path: '/login', query: { redirect: router.currentRoute.value.fullPath } })
    })
    unauthorizedHandlerRegistered = true
  }

  if (to.meta.anonymous) {
    return true
  }

  if (!auth.isAuthenticated) {
    if (!auth.profile && hasToken()) {
      // 有令牌但内存里没有上下文（刷新页面后的正常情况）：静默恢复会话
      const restored = await restoreSession(auth)

      if (restored) {
        return { ...to, replace: true }
      }
    }

    return { path: '/login', query: { redirect: to.fullPath } }
  }

  return true
})

function hasToken(): boolean {
  return !!localStorage.getItem('xjgl.access_token')
}

async function restoreSession(auth: ReturnType<typeof useAuthStore>): Promise<boolean> {
  try {
    await auth.loadProfile()

    if (auth.profile) {
      registerDynamicRoutes(auth.profile.menus)

      return true
    }
  } catch {
    auth.logout()
  }

  return false
}

export { hasToken, restoreSession }
