import { expect, test } from '@playwright/test'

/**
 * SSO 授权码流程的**入口**这一小段（本轮唯一修好的一处）。
 *
 * 【它守的是什么】
 * 外部教育系统免登的第一步：RP 把浏览器导航到 `/connect/authorize`，
 * 未登录时**必须有重定向**（302/303），而不是**裸 401**。
 *
 * 这一条以前是不成立的：授权端点在未登录分支调的是不带方案名的 `Challenge()`，
 * 而 `DefaultChallengeScheme` 原本是 OpenIddict 令牌校验 —— 它没有重定向能力，
 * 于是浏览器拿到 401。修法是只把 `DefaultChallengeScheme` 指向 Identity 应用 Cookie
 * （见 `XjglHttpApiHostModule.ConfigureAuthentication`，那里也写了为什么**不能**
 * 顺手把 LoginPath 指到前端登录页）。
 *
 * 【为什么断言停在"有重定向"为止】
 * 完整打通还缺一次"在浏览器里建立 Identity 会话"的登录：前端密码模式只签出
 * Bearer 令牌、不建 Cookie，而授权端点认的是 Cookie。因此现在到了登录页也回不来。
 * 这是**已知未完成项**，用例不假装它已通过（见下面第三条）。
 */
test.describe('SSO 授权码流程入口', () => {
  /** 用 Xjgl_Credit 已注册的 redirect_uri 构造一个合法授权请求。 */
  function authorizePath(): string {
    const params = new URLSearchParams({
      client_id: 'Xjgl_Credit',
      response_type: 'code',
      // 带空格的 scope 是"编码正确性"的探针：多编码一次会变成 %2520，服务端随即 400
      scope: 'openid profile',
      redirect_uri: 'https://credit.example.edu.cn/signin-oidc',
      state: 'e2e-state-123',
      nonce: 'e2e-nonce'
    })

    return `/connect/authorize?${params.toString()}`
  }

  test('未登录访问授权端点会被重定向，而不是拿到裸 401', async ({ request }) => {
    // 用 APIRequestContext 而不是浏览器：要断言的是**服务端的响应码**，
    // 浏览器会自动跟随重定向，把 302 吃掉。
    const response = await request.get(authorizePath(), { maxRedirects: 0 })

    expect(
      response.status(),
      '未登录的授权请求不该是 401 —— 401 意味着 Challenge 落到了没有重定向能力的方案上'
    ).not.toBe(401)

    expect([302, 303], '应当是重定向到登录页').toContain(response.status())

    const location = response.headers()['location'] ?? ''

    expect(location, '重定向目标不该为空').not.toBe('')
    // 目标应当是一个登录入口（本仓库的 /sso/login，或 Identity 应用 Cookie 的默认 LoginPath）
    expect(location).toMatch(/login/i)

    // ★ 回跳参数必须被带上，并且**恰好两层解码后**等于原始授权请求。
    //
    // 【为什么是两层，以及为什么这不是"多编码了"】
    // 这个 Location 的 ReturnUrl 是**再一层**的查询串参数：
    //   Location:  /Account/Login?ReturnUrl=%2Fconnect%2Fauthorize%3F...scope%3Dopenid%2520profile
    // 浏览器跟随这次重定向时会解码一层 → 服务端拿到 `…scope=openid%20profile`，
    // 再解码一次才是 `openid profile`。也就是说**两层解码才回到原值**是这里的正确形态。
    //
    // 我第一次的断言是"解码一次就该含 scope=openid profile"、并禁止出现 %2520 ——
    // 两条都把这层嵌套的必然结果当成了缺陷（把正确响应判成失败）。
    // 真正该断言的不变量只有一句：**解码两次能还原出原始参数**。
    // （"多编码一次"会让它需要三层解码，那时下面的断言才会失败。）
    const decodedTwice = decodeURIComponent(
      decodeURIComponent(location.replace(/^[^?]*\?/, ''))
    )

    expect(decodedTwice, '回跳地址里应当带上原始授权请求').toContain('connect/authorize')
    expect(decodedTwice, 'scope 里的空格被多编码了').toContain('scope=openid profile')
    expect(decodedTwice, 'redirect_uri 被多编码了').toContain(
      'redirect_uri=https://credit.example.edu.cn/signin-oidc'
    )
    expect(decodedTwice, 'client_id 丢了').toContain('client_id=Xjgl_Credit')
  })

  test('本仓库的 SSO 入口会把浏览器转交给前端登录页', async ({ playwright }) => {
    // SsoSessionController 的职责只有"转交"：拼出前端登录页地址并 302 过去。
    // 它不做认证，所以不需要登录态。
    //
    // ⚠ 这里必须**自己建一个"不跟随重定向"的请求上下文**。
    //   Playwright 的 `maxRedirects` 只在 `APIRequestContext` 的构造参数里生效，
    //   放进 `request.get(...)` 的 options 会被**静默忽略** —— 于是跟随重定向后
    //   拿到的是最终页面的 200，而不是中转的 302（实测踩过：断言一直报"期望 302、实际 200"）。
    const ctx = await playwright.request.newContext({
      baseURL: process.env.E2E_API_URL ?? 'http://127.0.0.1:44300',
      maxRedirects: 0
    })

    try {
      const response = await ctx.get(
        '/sso/login?returnUrl=%2Fconnect%2Fauthorize%3Fclient_id%3DXjgl_Credit'
      )

      expect(response.status(), '应当是 302 转交，而不是渲染一个页面').toBe(302)

      const location = response.headers()['location'] ?? ''

      expect(location, '应当转交到前端登录页').toMatch(/\/login\?/)
      expect(location, '回跳参数必须原样带过去').toContain('connect')

      // 目标必须是 **App:ClientUrl** 那个前端地址，而不是后端自己 ——
      // 指向后端的话浏览器会停在 API 的域名上，永远打不开 SPA 登录页。
      expect(location).toMatch(/^https?:\/\/[^/]+\/login/)
      expect(location, '不能把浏览器转交到后端自己的端口').not.toMatch(/:44300/)
    } finally {
      await ctx.dispose()
    }
  })

  test('（已知未完成）前端密码模式登录不会建立授权端点认的会话', async ({ page }) => {
    // 这条用例**刻意断言"当前是坏的"**，把"SSO 尚未打通"变成可执行的记录：
    //   1) 它说明"能登录（拿到令牌）"与"授权端点认你（有 Cookie 会话）"是两件事；
    //   2) 真打通之后它会**变红**，提醒把它改成"登录后拿到授权码"。
    // 不要因为"它断言失败态"就删掉它 —— 删掉等于把这件事藏起来。
    await page.goto('/login')

    await page.getByPlaceholder('请输入用户名').fill('admin')
    await page.getByPlaceholder('请输入密码').fill('1q2w3E*')
    await page.getByRole('button', { name: /登\s*录/ }).click()

    // 密码模式登录本身是成功的
    await page.waitForURL('**/dashboard')

    // 但带上去访问授权端点，仍会被送回来（因为它只认 Identity 应用 Cookie）
    const response = await page
      .goto(authorizePath(), { waitUntil: 'domcontentloaded' })
      .catch(() => null)

    expect(
      page.url(),
      '若这里不再是登录相关页面，说明 SSO 已打通 —— 请把本用例改成"拿到授权码"'
    ).toMatch(/login/i)

    expect(response?.status(), '授权端点不应把已登录的会话当成 401').not.toBe(401)
  })
})
