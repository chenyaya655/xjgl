import { expect, test, type Page } from '@playwright/test'

import { login } from './support/auth'

/**
 * 进入某个页面并**等到动态路由真的注册好**。
 *
 * 【为什么不能只 `page.goto`】
 * 本系统的路由是登录后按后端返回的菜单树 `addRoute()` 动态挂载的。
 * 冷启动时 Vite 还在编译页面 chunk，此时 `goto('/report/student')` 会匹配到
 * catch-all 的 404 页 —— 而**它同样是 200**，所以 goto 不会报错，
 * 后续断言却会在一个"页面不存在"的页面上找按钮，超时消息完全指不到真正的原因
 * （实测踩过：5 条用例全红，报的是"找不到按钮"）。
 * 因此统一走这个辅助函数：直接 goto，然后**断言 404 页没有出现**并重试。
 */
async function gotoDynamicPage(page: Page, path: string, anchor: RegExp): Promise<void> {
  await expect(async () => {
    await page.goto(path)

    // 404 页的特征文案；它在冷启动时会短暂出现
    await expect(page.getByText('页面不存在或您没有访问权限')).toHaveCount(0)

    await expect(page.getByRole('button', { name: anchor })).toBeVisible({ timeout: 10_000 })
  }).toPass({ timeout: 60_000, intervals: [1_000, 2_000, 3_000] })
}

/**
 * 统计报表页面的端到端验证。
 *
 * 【为什么统计页特别需要"在浏览器里真的点一遍"】
 * 这两页的逻辑几乎全在**渲染**上，而不是在请求上：
 *   - 分组键到展示名的映射（`hasPhoto` 是 `yes`/`no`、性别是码值 `1`/`2`）；
 *   - 数据范围说明（县区用户看上级时后端会**收敛**，说明文字必须出现）；
 *   - 截断告警（分组数达上限时"只显示前 200 组"不能被当成"一共 200 组"）；
 *   - 空结果的说明（演示库没有异动数据时必须说清"这不是坏了"）。
 * 上面每一条都能在"接口 200、表格能画出来"的前提下出错 ——
 * 类型检查与集成测试都发现不了，只有真渲染一次才看得见。
 */
test.describe('统计报表', () => {
  test.beforeEach(async ({ page }) => {
    await login(page)
  })

  test('学生统计页能出分组表格，并显示数据范围与编码后的名称', async ({ page }) => {
    await gotoDynamicPage(page, '/report/student', /统\s*计/)

    await page.getByRole('button', { name: /统\s*计/ }).click()

    // 表格至少有一行（演示库里学生数不为 0）
    await expect(page.locator('.el-table__row').first()).toBeVisible()

    // ★ 数据范围说明必须显示出来：不写的话"县区用户看全市"会被误读成"这个市只有这么点人"
    //
    // ⚠ 必须 `exact: true`：学校下拉的占位符里也有"数据范围"四个字
    // （"全部学校（按你的数据范围）"），非精确匹配会同时命中两个元素，
    // Playwright 的 strict mode 会直接报"resolved to 2 elements"。
    await expect(page.getByText('数据范围', { exact: true })).toBeVisible()

    // ★ 分组键必须被翻译成中文：直接渲染键的话这里会出现 "yes" / "no"
    const text = (await page.locator('.el-table').first().innerText()) ?? ''

    expect(text, '表格里不该出现未翻译的分组键').not.toMatch(/\b(yes|no)\b/)
  })

  test('学生统计页的"按有无照片"维度显示中文而不是 yes/no', async ({ page }) => {
    await gotoDynamicPage(page, '/report/student', /统\s*计/)

    // ⚠ 要点 `.el-radio-button__inner`（那层可见的 label），
    // 不能点 `input`：真正的 radio 被 label 盖住，Playwright 会判定
    // "intercepts pointer events" 并一直重试到超时。
    await page.locator('.el-radio-button__inner', { hasText: '按有无照片' }).click()

    await expect(page.locator('.el-table').first()).toContainText(/有照片|缺照片/)
  })

  test('学生统计页的年级/班级下拉在未选学校时是禁用的', async ({ page }) => {
    await gotoDynamicPage(page, '/report/student', /统\s*计/)

    // 年级/班级挂在「学校 + 学年」下：没选学校时后端无法回答"有哪些年级"，
    // 所以下拉必须禁用并给出可理解的占位文案，而不是给一个空的可点下拉。
    const gradeSelect = page
      .locator('.el-form-item', { hasText: '年级 / 班级' })
      .locator('.el-select__wrapper')
      .first()

    await expect(gradeSelect).toHaveClass(/is-disabled/)
    await expect(page.getByText('请先选学校')).toBeVisible()
  })

  test('异动统计页在无数据时明确说明"这不是坏了"', async ({ page }) => {
    await gotoDynamicPage(page, '/report/change', /统\s*计/)

    await page.getByRole('button', { name: /统\s*计/ }).click()

    // 演示库不含异动数据（用户明确要求"异动数据通过测试加入，不写演示种子"），
    // 所以这一页的**正确**表现是给一句解释，而不是一张空表让人以为功能坏了。
    //
    // ⚠ 但"有没有数据"取决于这个库跑过什么：集成测试会造真实申请，
    // 于是同一个页面可能是有数据的形态。两种都必须成立 —— 断言要覆盖**分叉**，
    // 而不是假定其中一种（第一版只在"空"这一支上断言，全量跑一次就红了）。
    const empty = page.locator('.el-empty')
    const rows = page.locator('.el-table__row')

    await expect
      .poll(async () => (await empty.isVisible().catch(() => false)) || (await rows.count()) > 0, {
        timeout: 30_000
      })
      .toBe(true)

    if (await empty.isVisible().catch(() => false)) {
      // 空态必须解释原因，否则使用者会把"没数据"当成"功能坏了"
      await expect(page.getByText(/演示库不含异动数据/)).toBeVisible()
    } else {
      // 有数据时合计必须是个数字（说明分组真的算出来了）
      await expect(page.getByText('合计（当前范围与筛选）')).toBeVisible()
    }
  })

  test('异动统计页的"按转出学校"维度给出双口径提示', async ({ page }) => {
    await gotoDynamicPage(page, '/report/change', /统\s*计/)

    await page.locator('.el-radio-button__inner', { hasText: '按转出学校' }).click()

    // "我校转出 30 人"与"我校转入 30 人"是完全不同的两件事，而总数上它们长得一样 ——
    // 界面必须在这一点上说清楚，否则使用者会把两个口径的数字当同一个。
    await expect(page.getByText(/两个口径/)).toBeVisible()
  })
})
