import { expect, test, type Page, type Playwright } from '@playwright/test'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

/**
 * 角色管理：建角色 → 配权限 → 删角色，以及"读不到角色时必须说出来"。
 *
 * 【为什么这两件事写在同一个文件里】
 *   它们守的是**同一个缺陷的两半**。原来的问题是：
 *   演示账号缺 `AbpIdentity.Roles` 时，`/api/identity/roles` 返回的不是 403
 *   而是 **302 跳登录页**；浏览器跟随重定向后 axios 拿到登录页 HTML、解 JSON 抛错，
 *   而页面写的是 `catch { roles.value = [] }` —— 于是**空列表 + 零提示**，
 *   使用者的结论是"这个功能不存在"。
 *   所以只补权限（让接口通）是不够的：**必须证明失败时看得见**，
 *   否则下一次换个权限组合，同样的"静默空白"会原样复现。
 *
 * 用法：
 *   pnpm test:e2e --project=chromium --grep "角色管理"
 *   截图落在 `web/e2e/demo-shots/`
 *
 * ⚠ 前置：后端与 PC 前端已启动，且演示库跑过 DbMigrator（含 `Sys.Role` 模块）。
 */
const HERE = path.dirname(fileURLToPath(import.meta.url))
const SHOT_DIR = path.join(HERE, '..', 'demo-shots')

/** 省厅演示账号：权限来自 DemoDataSeedContributor，口令在 deploy/README 里。 */
const DEMO = { userName: 'demo-prov-990000', password: 'Demo@Xjgl2026' }
const ADMIN = { userName: 'admin', password: '1q2w3E*' }

/**
 * 本用例创建的角色名。
 *
 * 用固定名而不是随机名，是为了**可重复运行**：用例开头会先按名字清理，
 * 所以上一轮残留不会导致"角色名已存在"。而写死也让失败信息一眼能看懂
 * （随机名会让"哪个角色没删掉"变得难以追问）。
 */
const ROLE_NAME = 'E2E_REGISTRAR'

async function login(page: Page, who: { userName: string; password: string }): Promise<void> {
  await page.goto('/login')
  await page.getByPlaceholder('请输入用户名').fill(who.userName)
  await page.getByPlaceholder('请输入密码').fill(who.password)
  await page.getByRole('button', { name: /登\s*录/ }).click()
  await page.waitForURL('**/dashboard')
}

/** 等表格里的 loading 蒙层消失（本系统页面都是"先骨架后数据"）。 */
async function settle(page: Page): Promise<void> {
  await page.locator('.el-loading-mask').waitFor({ state: 'detached', timeout: 20_000 }).catch(() => {})
}

/**
 * 展开左侧某个子系统菜单，然后断言其中的子项可见。
 *
 * 【为什么不能直接断言子项可见】
 *   `el-menu` 的子菜单默认**折叠**：未展开时子项虽然在 DOM 里
 *   （所以 `getByText(...)` 能"resolved to"一个 `<li>`），但 `toBeVisible()` 会失败。
 *   实测报错是 `locator resolved to <li ...> ... unexpected value "hidden"` ——
 *   它看起来像"菜单没渲染"，实际只是没展开。这类断言假阴性必须先展开再断言。
 */
async function expectSubmenuItem(page: Page, group: string, item: string): Promise<void> {
  const submenu = page.locator('.el-sub-menu', { hasText: group }).first()

  await submenu.locator('.el-sub-menu__title').click()
  await expect(submenu.getByText(item, { exact: true }).first()).toBeVisible({ timeout: 15_000 })
}

/**
 * 通过接口删除指定角色（若存在）。
 *
 * 【为什么准备/收尾走接口，而不是"进页面点删除按钮"】
 *   原先是在界面上点删除，实测出现**偶发失效**：`settle()` 等的是
 *   "loading 蒙层消失"，而这个蒙层可能**还没出现**就已经被判为消失
 *   （Vite 首次编译页面时最明显）。于是读到的是**尚未加载的空表格**，
 *   逻辑判"没有残留"直接返回，而角色其实还在 —— 下一次建同名角色
 *   就报 403「已存在」，最终表现为"弹窗关不掉"，与真实原因相隔甚远。
 *   准备与收尾的正确性不该依赖渲染时序，走接口是确定性的。
 *   （界面上的删除按钮另有一条专门断言，覆盖并没有丢。）
 *
 * 【为什么不用 `page.request`】
 *   它是**不带页面的 Authorization 头**的：令牌存在 localStorage 里，
 *   而 `page.request` 只共享 Cookie，不共享 localStorage。
 *   直接用它打 `/api/identity/roles` 会拿到未授权（302 跳登录页），
 *   报错却是"准备阶段读取角色列表失败"，同样指不到真正的原因。
 *   这里显式建一个 APIRequestContext 并自己走一次令牌登录。
 *
 * ⚠ 删的是**当前登录账号有权删的**角色（演示账号有 AbpIdentity.Roles.Delete）。
 */
async function deleteRoleByName(
  playwright: Playwright,
  name: string
): Promise<void> {
  const apiUrl = process.env.E2E_API_URL ?? 'http://127.0.0.1:44300'
  const api = await playwright.request.newContext({ baseURL: apiUrl })

  try {
    const tokenResponse = await api.post('/connect/token', {
      form: {
        grant_type: 'password',
        username: DEMO.userName,
        password: DEMO.password,
        client_id: 'Xjgl_App',
        scope: 'Xjgl offline_access'
      }
    })

    if (!tokenResponse.ok()) {
      throw new Error(`准备阶段登录取令牌失败：HTTP ${tokenResponse.status()}`)
    }

    const { access_token: token } = (await tokenResponse.json()) as { access_token: string }
    const headers = { Authorization: `Bearer ${token}` }

    const listResponse = await api.get('/api/identity/roles', {
      headers,
      params: { filter: name, maxResultCount: 100 }
    })

    if (!listResponse.ok()) {
      throw new Error(`准备阶段读取角色列表失败：HTTP ${listResponse.status()}`)
    }

    const body = (await listResponse.json()) as { items?: { id: string; name: string }[] }
    const hit = body.items?.find((r) => r.name === name)

    if (!hit) {
      return
    }

    const deleted = await api.delete(`/api/identity/roles/${hit.id}`, { headers })

    if (!deleted.ok()) {
      throw new Error(`准备阶段删除角色「${name}」失败：HTTP ${deleted.status()}`)
    }
  } finally {
    await api.dispose()
  }
}

test.describe('角色管理', () => {
  test.setTimeout(180_000)

  /**
   * 演示账号（只有 `Sys.Role.*`，没有 `Sys.Permission.*`）：
   * 能建、能删角色，但**不该**看到「分配权限」——因为权限页的路由
   * 由 `Sys.Permission` 决定，点进去只会得到 404。
   *
   * 【这条用例是从一个真实 404 里长出来的】
   *   最初「分配权限」是无条件显示的，演示账号点下去直接掉进 404 页面。
   *   根因是两个页面的**路由权限彼此独立**：能管角色 ≠ 能配权限。
   *   修法是按权限显隐（与用户管理、学生管理等页面同一约定），
   *   这里把"没权限就不显示"固化下来，防止回归。
   */
  test('演示账号能建角色与删角色，但没有权限时不显示「分配权限」', async ({ page, playwright }) => {
    await login(page, DEMO)

    // 菜单里必须有「角色管理」——它是元数据驱动的，没出现说明 Sys.Role 没授权
    await expectSubmenuItem(page, '系统管理', '角色管理')

    // 先清掉上一轮可能残留的同名角色（走接口，不依赖页面渲染时序）
    await deleteRoleByName(playwright, ROLE_NAME)

    // ── 1. 列表（这一步同时验证框架权限 AbpIdentity.Roles 已具备）──
    await page.goto('/system/role')
    await settle(page)

    // 读不到角色时页面会显示错误 Alert —— 这里断言它**不**存在，
    // 否则"列表是空的"和"读取失败了"这两种情况在用例里无法区分
    await expect(page.locator('.el-alert')).toHaveCount(0)
    await expect(page.locator('.el-table__row').first()).toBeVisible()
    await page.screenshot({ path: `${SHOT_DIR}/30-角色管理-列表.png` })

    // ── 2. 新建角色 ──
    await page.getByRole('button', { name: /新建角色/ }).click()
    await page.getByPlaceholder('如 CountyAdmin、SchoolRegistrar').fill(ROLE_NAME)
    await page.screenshot({ path: `${SHOT_DIR}/31-角色管理-新建.png` })
    await page.getByRole('button', { name: '保存' }).click()

    // ⚠ 必须等弹窗**真的关掉**再继续下一步。
    //   不等的话后续点击会被 `.el-overlay-dialog` 遮罩拦住，
    //   Playwright 报的是"subtree intercepts pointer events"并一直重试到超时，
    //   看起来像"按钮点不动"，实际是"上一步的弹窗还开着"。
    await expect(page.locator('.el-overlay-dialog')).toBeHidden({ timeout: 20_000 })

    await expect(page.locator('.el-table__row', { hasText: ROLE_NAME })).toHaveCount(1, { timeout: 15_000 })
    await page.screenshot({ path: `${SHOT_DIR}/32-角色管理-已创建.png` })

    // ── 3. 没有 Sys.Permission.* 时不该出现「分配权限」 ──
    await expect(
      page.locator('.el-table__row', { hasText: ROLE_NAME }).getByRole('button', { name: '分配权限' })
    ).toHaveCount(0)

    // ── 4. 界面上删除（覆盖删除按钮 + 确认框这条路径）──
    //
    // 【为什么要"等行出现"再操作】
    //   直接 `page.goto` 之后就点，会打在一个**尚未加载的空表格**上：
    //   `settle()` 等的是 loading 蒙层消失，而蒙层可能还没出现就被判为已消失
    //   （Vite 首次编译时最明显）。所以这里显式等目标行渲染出来。
    await page.goto('/system/role')
    await expect(page.locator('.el-table__row', { hasText: ROLE_NAME })).toHaveCount(1, { timeout: 20_000 })

    await page.locator('.el-table__row', { hasText: ROLE_NAME })
      .getByRole('button', { name: '删除' })
      .click()

    const confirm = page.locator('.el-message-box')

    await expect(confirm).toBeVisible({ timeout: 10_000 })
    await confirm.getByRole('button', { name: '确定' }).click()

    await expect(page.locator('.el-table__row', { hasText: ROLE_NAME })).toHaveCount(0, { timeout: 15_000 })
    await page.screenshot({ path: `${SHOT_DIR}/35-角色管理-已删除.png` })
  })

  /**
   * 超管（admin）走完整的"两步连起来"：建角色 → 分配权限，
   * 并用**另一个 context** 复核权限真的落库。
   *
   * 用 admin 而不是演示账号，理由见上一条用例：演示账号没有
   * `Sys.Permission.*`，进不了权限页（那是有权限边界的设计，不是缺陷）。
   */
  test('超管建角色后「分配权限」能带入该角色，且权限确实落库', async ({ page, playwright }) => {
    await login(page, ADMIN)
    await deleteRoleByName(playwright, ROLE_NAME)

    await page.goto('/system/role')
    await settle(page)

    await page.getByRole('button', { name: /新建角色/ }).click()
    await page.getByPlaceholder('如 CountyAdmin、SchoolRegistrar').fill(ROLE_NAME)
    await page.getByRole('button', { name: '保存' }).click()
    await expect(page.locator('.el-overlay-dialog')).toBeHidden({ timeout: 20_000 })
    await expect(page.locator('.el-table__row', { hasText: ROLE_NAME })).toHaveCount(1, { timeout: 15_000 })

    // ── 点「分配权限」跳转，角色应被自动带入 ──
    //
    // 这一步守的是"两步操作要连起来"：若跳转没带上角色，
    // 页面会停在默认角色（admin）上，用户就会**给错误的角色配权限** ——
    // 而界面不会有任何异样。
    await page.locator('.el-table__row', { hasText: ROLE_NAME })
      .getByRole('button', { name: '分配权限' })
      .click()

    await expect(page).toHaveURL(/\/system\/permission\?role=/)

    // 用页面上的**确定性文案**验证，而不是去猜 el-select 的内部结构：
    // 「权限配置 — <角色名>」里的角色名来自接口返回的 entityDisplayName。
    // 注意选择器要避开 scoped 样式生成的类名后缀，所以用属性包含匹配。
    await expect(page.locator('[class*="perm__header"]')).toContainText(ROLE_NAME, {
      timeout: 20_000
    })

    // ── 勾选一个业务权限并保存 ──
    const group = page.locator('.el-collapse-item', { hasText: '学生管理' }).first()

    await group.locator('.el-collapse-item__header').click()

    await group.locator('.el-checkbox', { hasText: '查看' }).first().click()
    await page.screenshot({ path: `${SHOT_DIR}/33-角色管理-分配权限.png` })

    await page.getByRole('button', { name: /保存（/ }).click()
    await expect(page.getByText(/已保存 \d+ 项权限改动/)).toBeVisible({ timeout: 15_000 })

    // ── 复核：重新打开这个角色的配置页，权限必须还在（= 真的落库了）──
    await page.goto(`/system/permission?role=${ROLE_NAME}`)
    await settle(page)

    const reloaded = page.locator('.el-collapse-item', { hasText: '学生管理' }).first()

    await reloaded.locator('.el-collapse-item__header').click()

    // "已授 N / M"必须 ≥ 1（N 含子级继承，所以不断言恰好等于 1）
    await expect(reloaded.locator('.el-tag').filter({ hasText: /已授 [1-9]/ })).toBeVisible({
      timeout: 15_000
    })

    await page.screenshot({ path: `${SHOT_DIR}/34-角色管理-权限已落库.png` })

    // 收尾：删掉角色，保证用例可重复运行
    await deleteRoleByName(playwright, ROLE_NAME)
  })

  /**
   * ★ 这条用例守的是**"失败必须看得见"**，与上一条同样重要。
   *
   * 用 `page.route` 把 `/api/identity/roles` 改写成**登录页 HTML** ——
   * 这正是"未授权 → 302 跳登录"在浏览器里的最终形态。
   * 断言页面必须弹出错误 Alert 并点明原因，而不是渲染一个空表格。
   *
   * 【为什么用拦截而不是"真的把权限去掉"】
   *   真的去掉权限要动角色授权数据，用例之间有顺序依赖、失败还会污染演示库；
   *   而这里要验证的是**前端对失败的处理**，拦截恰好只改这一点，
   *   既不依赖权限配置，也不会被别的用例影响。
   */
  test('读取角色列表失败时必须明确报错，而不是静默留空', async ({ page }) => {
    await login(page, DEMO)

    /** 记录被改写过的请求，用于在失败时报出"拦截到底有没有命中"。 */
    const intercepted: string[] = []

    /*
     * 用 RegExp 而不是 glob 字符串来匹配（原先写的是带 ? 的 glob）。
     * glob 里的 ? 在 Playwright 的匹配器里有特殊含义（匹配单个字符），
     * 写成字符串很容易**静默不命中** —— 那样这条用例会"因为没拦截到而失败"，
     * 报错却是"没找到 .el-alert"，排查方向完全跑偏。
     * 用正则后命中与否一目了然；这里还把命中的 URL 记下来，失败时能直接看到。
     */
    await page.route(/\/api\/identity\/roles(\?|$)/, (route) => {
      const url = route.request().url()

      intercepted.push(url)
      console.log(`INTERCEPT >>> ${url}`)

      return route.fulfill({
        status: 200,
        contentType: 'text/html',
        body: '<!DOCTYPE html><html><body><form id="login">登录页</form></body></html>'
      })
    })

    await page.goto('/system/role')

    const alert = page.locator('.el-alert')

    await expect(alert).toBeVisible({ timeout: 20_000 })
    // 提示必须既说清"返回的不是 JSON"，又给出**可行动**的下一步（缺哪个权限）。
    // 这条断言是"拦截器识别出 HTML"与"页面把原因翻译出来"两者的共同证据：
    // 少了拦截器，错误根本不会抛，页面会安静地渲染一个空表格。
    await expect(alert).toContainText('不是 JSON')
    await expect(alert).toContainText('AbpIdentity.Roles')
    await expect(page.locator('.el-table__row')).toHaveCount(0)

    console.log(`INTERCEPT 命中次数=${intercepted.length}`)

    await page.screenshot({ path: `${SHOT_DIR}/36-角色管理-失败提示.png` })
  })
})
