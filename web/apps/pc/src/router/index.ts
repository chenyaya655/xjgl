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
    /**
     * 路由名必须**全局唯一**：vue-router 的 `addRoute` 遇到重名时
     * 会覆盖掉先前那一条，而 `getRoutes()` 里不会告诉你少了谁。
     *
     * 【为什么不能直接用 item.code】
     * `code` 只在**同一个子系统内**唯一（后端是 (subsystem_id, code) 唯一索引），
     * 跨子系统重名是完全正常的 —— 真实的例子：
     *   - `Stu` 子系统有模块 code=Student → /student/list
     *   - `Rpt` 子系统也有模块 code=Student → /report/student
     * 直接拿 code 当路由名，两者就是同一个名字；下面的去重集合会把第二个
     * **静默跳过**，表现为"菜单里有『学生统计』，点进去却是 404"。
     * 这个缺陷真实发生过，而且只影响"跨子系统同 code"的那一个模块 ——
     * 同一子系统内看不出来，所以很隐蔽。
     *
     * 因此在前面缀上子系统 code（`{groupCode}:{item.code}`，如 `Rpt:Student`）：
     * 子系统 code 本身是主键级唯一的，拼出来的名字必然全局唯一。
     * 原始的 code 仍放在 meta.code 里，需要时可用。
     */
    name: `${groupCode}:${item.code}`,
    component: resolveView(item.component),
    meta: {
      title: item.name,
      code: item.code,
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
    // 账号激活是**公开页面**：教师此时还没有账号，登录不了。
    // 它只调用一个匿名接口（POST /api/app/user-provision/activate）。
    path: '/activate',
    name: 'activate',
    component: () => import('@/views/activate/index.vue'),
    meta: { title: '账号激活', anonymous: true }
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

/**
 * 公开页面（不需要登录）。
 *
 * ⚠ **不能用 `to.meta.anonymous` 判断"是不是公开页"**：
 * 业务路由是登录后才注册的，所以在"还没恢复会话"的那一刻，
 * `/student/list` 这类路径匹配到的是 **catch-all（404 页）**，而它也带 `anonymous: true` ——
 * 于是守卫会直接放行，用户看到的是「页面不存在或您没有访问权限」，
 * 而不是被送到登录页（未登录）或自动恢复会话（已登录后刷新）。
 *
 * 这个缺陷是端到端测试（Playwright）抓出来的：集成测试与类型检查都看不到它。
 */
const PUBLIC_PATHS = ['/login', '/activate']

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

  // 先尝试恢复会话：动态路由注册之后，业务路径才解析得到
  if (!auth.isAuthenticated && hasToken()) {
    const restored = await restoreSession(auth)

    if (restored) {
      // 路由表变了，重新解析当前目标。
      //
      // ⚠ 必须用 fullPath 字符串，**不能写 `{ ...to, replace: true }`**：
      // `to` 是已解析的路由对象，里面带着 `name: 'not-found'`（此刻它匹配的是 catch-all），
      // 而 vue-router 里 **name 优先于 path** —— 于是会被导航回 404 页，
      // 表现就是"刷新业务页面永远停在页面不存在"。这个坑是端到端测试抓出来的。
      return { path: to.fullPath, replace: true }
    }
  }

  if (PUBLIC_PATHS.includes(to.path)) {
    return true
  }

  if (!auth.isAuthenticated) {
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
