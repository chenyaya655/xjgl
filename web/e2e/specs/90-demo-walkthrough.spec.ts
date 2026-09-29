import { expect, test, type Page } from '@playwright/test'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

/**
 * 演示走查：把学籍系统的典型流程在**真浏览器**里走一遍，并逐步截图。
 *
 * 【为什么把它写成一个 spec 而不是手动点】
 *   1. 截图是**可复现**的 —— 下次改完代码重跑一遍就能看出哪里变了；
 *   2. 每一步都带断言：截图好看但流程断了（例如菜单点进去 404）也能当场发现；
 *   3. 演示完不用清理 —— 它只读不写（不改数据、不建学生）。
 *
 * 用法：
 *   pnpm test:e2e --project=chromium --grep "演示走查"
 *   截图输出在 `web/e2e/demo-shots/`（该目录已在 .gitignore 里）
 *
 * ⚠ 前置：后端与前端都已启动，且库里有演示种子数据（见 deploy/README 或
 *   DbMigrator 的 `Xjgl__DemoData__Enabled=true`）。
 */
/**
 * 截图输出目录用**绝对路径**，不能写 `e2e/demo-shots` 这样的相对路径。
 *
 * 原因是 Playwright 解析 `screenshot({ path })` 与 `--output` 时，相对路径的基准
 * 是**配置文件所在目录**（`web/e2e/`），而不是仓库根目录。写成相对路径后
 * 实际落点是 `web/e2e/e2e/demo-shots/` —— 凭空多一层 `e2e`，
 * 而 `.gitignore` 忽略的是 `web/e2e/demo-shots/`，结果是截图全都被 git 跟踪，
 * 同时"按文档去 demo-shots 找图"什么也找不到。
 */
const HERE = path.dirname(fileURLToPath(import.meta.url))
const SHOT_DIR = path.join(HERE, '..', 'demo-shots')

/**
 * PC 端地址。与 playwright.config.ts 用同一个环境变量与同一个默认值 ——
 * 这里必须显式传给 `browser.newContext()`，否则新开的 context **不会**继承
 * 项目里的 `use.baseURL`，`page.goto('/login')` 会以 "Cannot navigate to invalid URL" 失败。
 */
const BASE_URL = process.env.E2E_WEB_URL ?? 'http://127.0.0.1:5173'

test.describe('演示走查', () => {
  test.setTimeout(240_000)

  /** 截图并返回文件名（顺序前缀保证按文件名排序就是流程顺序）。 */
  let seq = 0

  async function shot(page: Page, name: string): Promise<void> {
    seq += 1

    const file = `${SHOT_DIR}/${String(seq).padStart(2, '0')}-${name}.png`

    await page.screenshot({ path: file, fullPage: false })
    console.log(`SHOT >>> ${file}`)
  }

  /** 等页面稳定：本系统的页面都是"先渲染骨架、再拉数据"，所以要等 loading 消失。 */
  async function settle(page: Page): Promise<void> {
    await page.waitForLoadState('networkidle')
    await expect(page.locator('.el-loading-mask')).toHaveCount(0, { timeout: 20_000 })
  }

  test('从登录到统计的完整走查', async ({ page, browser }) => {
    // ── 1. 登录页 ──
    await page.goto('/login')
    await shot(page, '登录页')

    await page.getByPlaceholder('请输入用户名').fill('demo-prov-990000')
    await page.getByPlaceholder('请输入密码').fill('Demo@Xjgl2026')

    await shot(page, '登录页-已填省厅账号')

    await page.getByRole('button', { name: /登\s*录/ }).click()
    await page.waitForURL('**/dashboard')

    // ── 2. 工作台 ──
    await settle(page)
    await shot(page, '工作台')

    // 左侧菜单必须能看出五大子系统（元数据驱动）。
    //
    // ⚠ 这里用**省厅演示账号**而不是 admin，是刻意的：演示账号按最小权限授权，
    //   只拿到「学生管理 / 照片管理 / 统计报表 / 系统管理」四个模块，
    //   而**异动管理一个权限都没有**，所以它**不应**出现在菜单里（见下面第 7 步）。
    //   拿 admin 走查会把"菜单会不会漏"这件事完全盖住 ——
    //   admin 被种子授予了全部权限，包括全部模块级权限，永远看不出菜单为空。
    for (const group of ['系统管理', '学生管理', '照片管理', '统计报表']) {
      await expect(page.getByText(group, { exact: true }).first()).toBeVisible()
    }

    await expect(page.getByText('异动管理', { exact: true })).toHaveCount(0)

    // ── 3. 学生列表（省厅账号 → 数据范围是全省）──
    await page.goto('/student/list')
    await settle(page)
    await shot(page, '学生列表-全省')

    const total = await page.locator('.el-pagination__total').innerText().catch(() => '')

    console.log(`SHOT-NOTE >>> 学生列表分页信息：${total}`)

    // ── 4. 按学校筛选 ──
    const schoolBox = page
      .locator('.el-form-item', { hasText: '学校' })
      .locator('input')
      .first()

    await schoolBox.click()
    await schoolBox.fill('示例')
    // 虚拟滚动下拉：等第一个选项出现再点
    const firstOption = page.locator('.el-select-dropdown__item').first()

    await expect(firstOption).toBeVisible({ timeout: 15_000 })
    await shot(page, '学生列表-学校下拉(虚拟滚动)')

    await firstOption.click()
    await page.getByRole('button', { name: '查询' }).click()
    await settle(page)
    await shot(page, '学生列表-按学校筛选后')

    // ── 5. 学生详情抽屉：六个子表 + 变更留痕 ──
    const firstRow = page.locator('.el-table__row').first()

    await expect(firstRow).toBeVisible()
    await firstRow.getByRole('button', { name: '详情' }).click()

    const drawer = page.locator('.el-drawer')

    await expect(drawer.getByRole('tab', { name: '监护人' })).toBeVisible()
    await shot(page, '学生详情-监护人')

    await drawer.getByRole('tab', { name: '体质健康' }).click()
    await settle(page)
    await shot(page, '学生详情-体质健康')

    await drawer.getByRole('tab', { name: '变更记录' }).click()
    await settle(page)
    await shot(page, '学生详情-变更留痕(只读)')

    // 关闭抽屉
    await page.locator('.el-drawer__close-btn').click()
    await expect(drawer).toBeHidden()

    // ── 6. 照片管理 ──
    await page.goto('/photo/list')
    await settle(page)
    await shot(page, '照片管理-台账与存储健康度')

    await page.getByRole('tab', { name: /缺照片名单/ }).click()
    await settle(page)
    await shot(page, '照片管理-缺照片名单')

    // ── 7. 异动管理（类型 / 申请 / 审核）──
    //
    // ⚠ 这一段**必须换 admin**，不能用上面的省厅演示账号。
    //   理由不是"省事"，而是演示角色的授权里**根本没有 Chg 前缀的任何权限**
    //   （见 DemoDataSeedContributor 的 DemoRolePermissions）：异动管理菜单不会出现，
    //   直接敲 /change/type 也会因为路由未注册而落到 404 —— 那是**正确行为**，
    //   不是缺陷。用第二个 context 登录 admin 走这三个页面，
    //   顺带把"没权限的账号看不到菜单、也没法直达"这件事一起验证掉。
    const adminContext = await browser.newContext({
      baseURL: BASE_URL,
      locale: 'zh-CN',
      timezoneId: 'Asia/Shanghai'
    })
    const adminPage = await adminContext.newPage()

    try {
      await adminPage.goto('/login')
      await adminPage.getByPlaceholder('请输入用户名').fill('admin')
      await adminPage.getByPlaceholder('请输入密码').fill('1q2w3E*')
      await adminPage.getByRole('button', { name: /登\s*录/ }).click()
      await adminPage.waitForURL('**/dashboard')

      // admin 是超管：五大子系统必须一个不少
      await settle(adminPage)
      for (const group of ['系统管理', '学生管理', '异动管理', '照片管理', '统计报表']) {
        await expect(adminPage.getByText(group, { exact: true }).first()).toBeVisible()
      }

      await adminPage.goto('/change/type')
      await settle(adminPage)
      await shot(adminPage, '异动类型管理')

      await adminPage.goto('/change/apply')
      await settle(adminPage)
      await shot(adminPage, '异动申请')

      await adminPage.goto('/change/audit')
      await settle(adminPage)
      await shot(adminPage, '异动审核-待办')
    } finally {
      await adminContext.close()
    }

    // 回到省厅演示账号继续后面几步（统计 / 系统管理都在它的授权范围内）
    // ── 8. 统计报表（本轮的交付之一）──
    await page.goto('/report/student')
    await settle(page)
    await page.getByRole('button', { name: /统\s*计/ }).click()

    await expect(page.locator('.el-table__row').first()).toBeVisible({ timeout: 20_000 })
    await shot(page, '学生统计-按学校')

    // 切一个码值维度，展示"码值被翻译成中文"
    await page.locator('.el-radio-button__inner', { hasText: '按学籍状态' }).click()
    await expect(page.locator('.el-table').first()).toContainText(/在读|转出|休学/)
    await shot(page, '学生统计-按学籍状态(码值已翻译)')

    await page.goto('/report/change')
    await settle(page)
    await page.getByRole('button', { name: /统\s*计/ }).click()
    await page.waitForTimeout(1500)
    await shot(page, '异动统计')

    // ── 9. 系统管理（元数据驱动的落点）──
    await page.goto('/system/module')
    await settle(page)
    await shot(page, '系统管理-模块与权限元数据')

    await page.goto('/system/org')
    await settle(page)
    await shot(page, '系统管理-组织树')

    // 演示账号列表（15 个）
    await page.goto('/system/user')
    await settle(page)
    await shot(page, '系统管理-用户管理')
  })
})
