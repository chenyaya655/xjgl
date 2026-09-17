import { expect, test } from '@playwright/test'

import { ADMIN, login } from './support/auth'

/**
 * 登录与会话（"模拟注册登录"里的登录部分）。
 *
 * 激活（注册）页是**公开页面**（教师拿到激活码后自己设密码），
 * 它不需要登录，因此单独在下面用一个用例验证页面可达性。
 */
test.describe('登录与会话', () => {
  test('未登录访问业务页面会被重定向到登录页', async ({ page }) => {
    await page.goto('/student/list')

    await expect(page).toHaveURL(/\/login/)
    await expect(page.getByPlaceholder('请输入用户名')).toBeVisible()
  })

  test('密码错误时提示失败且不跳转', async ({ page }) => {
    await page.goto('/login')

    await page.getByPlaceholder('请输入用户名').fill(ADMIN.userName)
    await page.getByPlaceholder('请输入密码').fill('definitely-wrong-password')
    await page.getByRole('button', { name: /登\s*录/ }).click()

    // 仍然停在登录页，并且给出可见的错误提示（而不是静默无反应）
    await expect(page).toHaveURL(/\/login/)
    await expect(page.locator('.login').getByText(/失败|错误|不正确|无效/)).toBeVisible({ timeout: 20_000 })
  })

  test('正确账号密码可以进入工作台并看到菜单', async ({ page }) => {
    await login(page)

    await expect(page).toHaveURL(/\/dashboard/)

    // 菜单来自后端元数据（不是前端写死的路由表）——
    // 能看到"学生管理"就说明 my-profile 与动态路由注册都成功了
    await expect(page.getByText('学生管理').first()).toBeVisible()
  })

  test('刷新页面后会话仍然有效（令牌持久化 + 静默恢复）', async ({ page }) => {
    await login(page)

    await page.goto('/photo/list')
    await expect(page).toHaveURL(/\/photo\/list/)

    await page.reload()

    // 刷新后不应被踢回登录页
    await expect(page).toHaveURL(/\/photo\/list/)
  })

  test('账号激活页是公开页面（教师没有账号时也能打开）', async ({ page }) => {
    await page.goto('/activate')

    await expect(page).toHaveURL(/\/activate/)
    await expect(page.getByText(/激活/).first()).toBeVisible()
  })
})
