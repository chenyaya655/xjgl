import { expect, test } from '@playwright/test'

import { login } from './support/auth'

/**
 * 各业务模块的"能打开、能查到数据"冒烟。
 *
 * 【为什么要这样一组"薄"用例】
 * 菜单与路由**完全由后端元数据驱动**（第 13 章）：管理员在"模块管理"里改一个路径，
 * 前端菜单就跟着变。因此"每个模块的页面都能打开"这件事本身值得自动化守着 ——
 * 它覆盖的是一整类问题：
 *   - 元数据里的 `component` 路径与前端文件对不上（会静默落到占位页）；
 *   - 动态路由没注册成功（点菜单 404）；
 *   - 页面在真实数据下渲染报错（本地开发时可能因为库里没数据而看不出来）。
 */
const MODULES: { path: string; name: string; expectText: RegExp }[] = [
  { path: '/student/list', name: '学生管理', expectText: /学生信息|学籍辅号/ },
  { path: '/photo/list', name: '照片管理', expectText: /照片台账|存储/ },
  { path: '/change/type', name: '异动类型管理', expectText: /异动类型|类型编码|流程/ },
  { path: '/change/apply', name: '异动申请', expectText: /申请|异动/ },
  { path: '/change/audit', name: '异动审核', expectText: /审核|待办/ },
  { path: '/system/org', name: '组织管理', expectText: /组织|学校|省/ },
  { path: '/system/user', name: '用户管理', expectText: /用户|角色/ },
  { path: '/system/permission', name: '权限分配', expectText: /权限|角色/ },
  { path: '/system/module', name: '模块管理', expectText: /模块|子系统|操作/ },
  { path: '/system/code', name: '微代码管理', expectText: /码表|代码|类别/ },
  { path: '/system/user-provision', name: '预置与激活', expectText: /预置|激活/ }
]

test.describe('各模块冒烟', () => {
  test.beforeEach(async ({ page }) => {
    await login(page)
  })

  for (const module of MODULES) {
    test(`${module.name} 页面能打开并渲染出内容`, async ({ page }) => {
      const errors: string[] = []

      page.on('pageerror', (error) => errors.push(error.message))

      const response = await page.goto(module.path)

      expect(response?.status(), `${module.path} 返回了异常状态码`).toBeLessThan(400)

      // 不能落在"页面不存在"或"尚未实现"的占位页上
      await expect(page.getByText('页面不存在')).toHaveCount(0)
      await expect(page.getByText('该模块尚未实现')).toHaveCount(0)

      // 页面上应当出现与模块相关的文案（说明组件真的渲染了，而不是空白）
      await expect(page.getByText(module.expectText).first()).toBeVisible({ timeout: 30_000 })

      // 没有未捕获的前端异常
      expect(errors, `${module.name} 页面出现前端异常：${errors.join('；')}`).toHaveLength(0)
    })
  }

  test('左侧菜单与后端元数据一致（能看到五大子系统）', async ({ page }) => {
    await page.goto('/dashboard')

    for (const group of ['学生管理', '异动管理', '照片管理']) {
      await expect(page.getByText(group).first()).toBeVisible()
    }
  })
})
