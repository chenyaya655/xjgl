import { createRouter, createWebHashHistory, type RouteRecordRaw } from 'vue-router'

import { setUnauthorizedHandler } from '@xjgl/api'
import { useAuthStore } from '@/stores/auth'

/**
 * H5 路由。
 *
 * **为什么用 hash 模式（`createWebHashHistory`）而不是 history 模式**：
 *   政务系统的 H5 通常挂在子路径下（如 `https://xxx.gov.cn/xjgl/h5/`），
 *   history 模式要求 nginx 把 `/xjgl/h5/*` 全部 rewrite 到 index.html ——
 *   而现场部署往往不归我们管，漏配一条 rewrite 的表现就是"能打开首页，
 *   一刷新就 404"。hash 模式（`.../h5/#/todo`）完全不依赖服务端配置。
 *   代价是 URL 里多个 `#`，对内部办公系统来说可以接受。
 *
 * 路由表是**固定三个页签**，不做元数据驱动的动态路由（原因见 stores/auth.ts）。
 */
const routes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'login',
    component: () => import('@/views/login/index.vue'),
    meta: { anonymous: true }
  },
  {
    path: '/',
    component: () => import('@/layout/TabBar.vue'),
    redirect: '/todo',
    children: [
      {
        path: 'todo',
        name: 'todo',
        component: () => import('@/views/todo/index.vue'),
        meta: { title: '待办' }
      },
      {
        path: 'photo',
        name: 'photo',
        component: () => import('@/views/photo/index.vue'),
        meta: { title: '拍照上传' }
      },
      {
        path: 'mine',
        name: 'mine',
        component: () => import('@/views/mine/index.vue'),
        meta: { title: '我的' }
      }
    ]
  },
  {
    // H5 只有三个页签，其它路径一律回到待办 —— 比给一个 404 页更符合移动端习惯
    path: '/:pathMatch(.*)*',
    redirect: '/todo'
  }
]

export const router = createRouter({
  history: createWebHashHistory(),
  routes
})

let unauthorizedHandlerRegistered = false

router.beforeEach(async (to) => {
  const auth = useAuthStore()

  if (!unauthorizedHandlerRegistered) {
    setUnauthorizedHandler(() => {
      auth.logout()
      void router.replace('/login')
    })
    unauthorizedHandlerRegistered = true
  }

  if (to.meta.anonymous) {
    return true
  }

  if (!auth.isAuthenticated) {
    // 有令牌但内存里没有上下文（刷新页面的正常情况）：静默恢复会话
    if (localStorage.getItem('xjgl.access_token')) {
      try {
        await auth.loadProfile()

        if (auth.isAuthenticated) {
          return true
        }
      } catch {
        auth.logout()
      }
    }

    return { path: '/login', query: { redirect: to.fullPath } }
  }

  return true
})
