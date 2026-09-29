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
 * 页签下可以有子页面（如 `photo/missing`），但子页面**不新增页签** ——
 * 移动端的功能入口一旦变多，教师就得靠翻菜单找活儿干。
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
        meta: { title: '拍照上传', tab: 'photo' }
      },
      {
        // 缺照片名单：从「拍照上传」页签进入的子页面。
        //
        // 【为什么它不是第四个页签】见 views/photo/index.vue 顶部注释。简单说：
        // 它是"把照片补齐"这一个任务的队列，不是一级功能区；四个页签在 320px
        // 宽下会把标签挤成两行。做成子页面还额外买到两件事：名单与拍摄各占一整屏，
        // 以及"返回"有明确落点。
        //
        // meta.tab 是给底部页签用的：子页面的路由名不叫 photo，若页签直接读
        // route.name，进入子页面时三个页签会**全部不高亮**，老师会以为点丢了。
        path: 'photo/missing',
        name: 'photo-missing',
        component: () => import('@/views/photo/missing.vue'),
        meta: { title: '缺照片名单', tab: 'photo' }
      },
      {
        path: 'mine',
        name: 'mine',
        component: () => import('@/views/mine/index.vue'),
        meta: { title: '我的' }
      },
      {
        // 发起异动申请：从「我的」页的「业务入口」进入的子页面。
        //
        // 【为什么入口在「我的」而不是「待办」】「待办」的语义是"轮到我审的任务"，
        // 把"发起申请"混进去，教师看一条记录就得先判断"这是让我审的还是我提的" ——
        // 语义混用最典型的代价就是判断错方向。而「我的」是每个人都有的功能入口页，
        // 放在那里既不依赖审核角色，也不会串语义（详见 views/change/apply.vue 顶部注释）。
        //
        // meta.tab 照 photo/missing 的做法：子页面的路由名不等于页签名，页签若直接读
        // route.name，进来时三个页签会**全部不高亮**，老师会以为点丢了。页签**仍然是 3 个**。
        path: 'change/apply',
        name: 'change-apply',
        component: () => import('@/views/change/apply.vue'),
        meta: { title: '发起异动申请', tab: 'mine' }
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
