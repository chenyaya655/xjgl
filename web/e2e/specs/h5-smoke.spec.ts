import { expect, test, type Page } from '@playwright/test'

/**
 * H5（手机端）端到端冒烟 —— **债 28**。
 *
 * 【为什么 H5 必须单独一组用例，而不是"PC 的用例顺便跑一下"】
 * 它是另一个 Vite 应用：端口 5174、**hash 路由**、移动端布局与触屏交互。
 * 拿 PC 的 spec 去跑必然全红（登录页字段与页签结构都不同），
 * 而那种红只会制造噪音。所以 `playwright.config.ts` 里两个项目互相 testIgnore，
 * 这一组用 `devices['Pixel 5']`（移动视口 + 触屏 + 移动 UA）跑。
 *
 * 【这一组守的是什么】
 *   1. **H5 真的能独立起来并登录** —— 它有自己的 dev server 与代理，
 *      "PC 好好的、H5 连不上后端"是双应用部署里最常见的形态；
 *   2. **三个页签在窄屏下都能进** —— 溢出/卡死这类问题桌面宽度下看不出来；
 *   3. **hash 路由刷新后仍在原页** —— history 模式在子路径部署下"一刷新就 404"，
 *      H5 刻意选了 hash 正是为了这个；这条用例把它钉住。
 */
test.describe('H5 手机端', () => {
  /** H5 用的是 hash 路由，路径要写成 `/#/xxx`。 */
  const HASH_TODO = '/#/todo'

  async function login(page: Page): Promise<void> {
    await page.goto('/#/login')

    // H5 的用户名占位符与 PC 不同（"用户名 / 手机号"）—— 这正是"两边不能共用 spec"的原因
    await page.getByPlaceholder('用户名 / 手机号').fill('admin')
    await page.getByPlaceholder('请输入密码').fill('1q2w3E*')
    await page.getByRole('button', { name: /登\s*录/ }).click()

    // 登录后落到待办页签
    await expect(page).toHaveURL(/#\/todo/)
  }

  test('H5 能独立启动并完成登录', async ({ page }) => {
    await login(page)

    // 底部三个页签都在
    await expect(page.getByText('待办', { exact: true })).toBeVisible()
    await expect(page.getByText('拍照上传', { exact: true })).toBeVisible()
    await expect(page.getByText('我的', { exact: true })).toBeVisible()
  })

  test('三个页签都能切换，且窄屏下没有横向溢出', async ({ page }) => {
    await login(page)

    for (const [label, hash] of [
      ['拍照上传', /#\/photo/],
      ['我的', /#\/mine/],
      ['待办', /#\/todo/]
    ] as const) {
      await page.getByText(label, { exact: true }).click()
      await expect(page).toHaveURL(hash)

      // ★ 横向溢出探针：移动端最典型的布局缺陷是"某个元素比屏幕宽"，
      //   表现是页面能左右拖动、右侧内容被裁掉。桌面宽度下**永远不会出现**，
      //   所以只能在移动视口里断言。
      const overflow = await page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth
      )

      expect(overflow, `「${label}」页在 ${page.viewportSize()?.width}px 宽下有横向溢出`).toBeLessThanOrEqual(1)
    }
  })

  test('hash 路由下刷新页面不会 404（这正是选 hash 模式的原因）', async ({ page }) => {
    await login(page)

    await page.getByText('我的', { exact: true }).click()
    await expect(page).toHaveURL(/#\/mine/)

    // 直接刷新（模拟用户在手机上误触刷新 / 从微信里重新打开）
    await page.reload()

    // 必须还在「我的」页；history 模式在子路径部署下这里会变成 404
    await expect(page).toHaveURL(/#\/mine/)
    await expect(page.getByText('待办', { exact: true })).toBeVisible()
  })

  test('待办页能打开并给出内容或明确的空态', async ({ page }) => {
    await login(page)

    await page.goto(HASH_TODO)

    // 三种可接受形态：有待办行 / 有空态说明 / 有加载态。
    // 演示库里异动申请为 0（用户明确要求不写演示种子），所以空态是**预期**形态。
    await expect
      .poll(
        async () => {
          const body = await page.locator('body').innerText()

          return (
            body.includes('待办') &&
            (body.includes('暂无') || body.includes('没有') || body.length > 60)
          )
        },
        { timeout: 30_000 }
      )
      .toBe(true)
  })
})
