namespace Xjgl.Sso;

/// <summary>
/// 单点登录（SSO）相关的固定约定。
///
/// 为什么这些值要集中放一处：
///   它们同时出现在「OpenIddict 服务端端点配置」（<c>SetAuthorizationEndpointUris</c> 等）、
///   「控制器路由」与「前端契约」三处。任何一处各写一遍字符串，改一处忘一处的结果是
///   <b>端点 404 或登录页 404</b>，而报错现场只有浏览器地址栏 —— 很难查。
///
/// 【本类的定位（2026-09 精简后）】
///   登录链路**刻意保持简单**：本系统不给 SSO 自造会话方案，而是复用 ABP 既有的
///   Identity 应用 Cookie（<c>IdentityConstants.ApplicationScheme</c>）。
///   原因见 docs/学籍管理系统-总体方案.md §17.8：自造 Cookie 与 ABP 自带的
///   <c>AuthorizeController</c> 不兼容，实测表现为授权端点直接返回裸 401。
///   因此本类里**不再有**"自建 Cookie 方案"相关的常量。
/// </summary>
public static class SsoConstants
{
    // ───────────────────────── 浏览器导航入口 ─────────────────────────

    /// <summary>
    /// 浏览器被重定向到这里的登录入口（由 <c>SsoSessionController.Login</c> 承接）。
    ///
    /// 它只做一件事：把浏览器再转交给前端登录页。**只有浏览器导航会走这个地址**；
    /// 前端调用的接口都在 <c>/api</c> 下，因为 <c>/api</c> 前缀决定了它走 Bearer 鉴权、
    /// 失败返回 401 而不是被重定向到 HTML。
    ///
    /// ⚠ 它目前**还不是** Identity 应用 Cookie 的 <c>LoginPath</c>。
    ///   把 <c>LoginPath</c> 指到这里会得到一段**走不通的路**：前端登录页走的是
    ///   OpenIddict 密码模式，只签出 Bearer 令牌、不建立 Identity 应用 Cookie，
    ///   而授权端点认的恰恰是 Cookie —— 表现为"登录成功却一直在登录页与授权端点之间往返"。
    ///   打通需要的三步写在 <c>XjglHttpApiHostModule.ConfigureAuthentication</c> 的注释里。
    /// </summary>
    public const string LoginPath = "/sso/login";

    /// <summary>登录页回跳参数名（前端登录页按它把用户送回授权端点）。</summary>
    public const string ReturnUrlParameter = "returnUrl";

    // ───────────────────────── OpenIddict 端点路径 ─────────────────────────
    //
    // ⚠ 这三个常量必须与 XjglHttpApiHostModule 里 SetXxxEndpointUris 的取值逐字一致。
    //   不一致的后果不是编译错，而是运行期 404 —— 而且只有真正走一次授权码流程才会暴露。

    /// <summary>
    /// 授权端点。RP 把浏览器导航到这里。
    ///
    /// ⚠ 它由 **ABP 自带的 <c>Volo.Abp.OpenIddict.Controllers.AuthorizeController</c>** 承接
    ///   （类名不带 "ation"，我最初按 `AuthorizationController` 搜符号导致零命中，
    ///   误判为"ABP 没提供"并重复实现了一次，运行期表现为 AmbiguousMatchException）。
    ///   **不要在本仓库再写第二个授权端点控制器。**
    /// </summary>
    public const string AuthorizePath = "/connect/authorize";

    /// <summary>
    /// 会话结束（登出）端点，由本仓库的 <c>SsoAuthorizationController.Logout</c> 承接。
    ///
    /// ⚠ ABP 自带的登出控制器路由是 <c>connect/endsession</c>（类名 <c>LogoutController</c>，
    ///   注意文件名是 EndSessionController.cs）。而本 Host 的 PreConfigure 把 OpenIddict 的
    ///   会话结束端点覆盖成了 <c>/connect/logout</c>，两者因此**不一致**：
    ///     · <b>OpenIddict 认的</b>端点是 /connect/logout（PreConfigure 在 ABP 的 builder
    ///       末端执行，最后生效）→ 由本仓库控制器承接；
    ///     · <c>connect/endsession</c> 仍是可达的 MVC 路由，但落在它上面的请求不再被
    ///       OpenIddict 当作会话结束端点处理，实测返回 500
    ///       （<c>A sign-out response cannot be returned from this endpoint</c>），
    ///       等于一个孤儿入口，收敛方案见 §17.8 第三节。
    /// </summary>
    public const string EndSessionPath = "/connect/logout";

    /// <summary>令牌端点。由 ABP 的 TokenController 处理（密码模式即由此工作）。</summary>
    public const string TokenPath = "/connect/token";

    // ───────────────────────── IdP 侧会话标识（sid） ─────────────────────────

    /// <summary>
    /// 保存 IdP 会话标识的声明类型。
    ///
    /// 【为什么需要自己生成 sid】
    /// OIDC Front-Channel Logout 1.0 要求 IdP 在通知 RP 时把 <c>sid</c> 传回去，
    /// 而 **OpenIddict 7.5.0 既不签发 sid、也没有 SessionId 声明常量**
    /// （已核对程序集元数据：<c>OpenIddictConstants.Claims</c> 里没有该成员）。
    /// 好消息是**传递这一环 ABP 已经做了**：它的 <c>AuthorizeController</c> 会把动态主体里的
    /// <c>JwtRegisteredClaimNames.Sid</c>（值就是 <c>"sid"</c>）复制进令牌。
    /// 所以本系统只需要在建立会话时把这个声明放进去，不必改令牌环节。
    /// </summary>
    public const string SessionIdClaimType = "sid";

    // ───────────────────────── Front-Channel Logout ─────────────────────────

    /// <summary>Front-Channel Logout 1.0 §2 定义的通知参数：签发者。</summary>
    public const string FrontChannelIssuerParameter = "iss";

    /// <summary>Front-Channel Logout 1.0 §2 定义的通知参数：会话标识。</summary>
    public const string FrontChannelSessionIdParameter = "sid";

    /// <summary>
    /// 渲染「正在退出所有系统」页之后，留给 iframe 的加载时间。
    ///
    /// 为什么不能立刻跳走：iframe 是异步加载的，页面一卸载浏览器就可能取消这些请求，
    /// 结果是<b>部分 RP 没收到登出通知却没人知道</b>。留一个宽限期是前通道登出固有的
    /// 取舍（这也是它不如后通道登出可靠的原因，见总体方案 §17.5）。
    /// </summary>
    public static readonly TimeSpan FrontChannelGracePeriod = TimeSpan.FromSeconds(3);

    // ───────────────────────── 配置键 ─────────────────────────

    /// <summary>OpenIddict 的签发者。令牌里的 iss 就是它，FC 登出通知也必须回传同一个值。</summary>
    public const string IssuerConfigKey = "AuthServer:Authority";

    /// <summary>前端地址。SSO 登录入口要把浏览器转交到这里。</summary>
    public const string ClientUrlConfigKey = "App:ClientUrl";

    /// <summary>前端登录路由（默认 /login）。做成配置项是因为 H5 端路由前缀不同。</summary>
    public const string LoginPathConfigKey = "App:SsoLoginPath";

    // ───────────────────────── 预留：小程序登录 ─────────────────────────

    /// <summary>
    /// 微信小程序的令牌端点授权类型。
    ///
    /// 【为什么现在只留常量不实现】
    /// 小程序拿不到浏览器会话，它走的是「wx.login 拿 code → 后端换 openid → 换令牌」，
    /// 与本系统现在的密码模式/授权码模式都不同。这一轮明确不做小程序，
    /// 但**种子代码已经能支持它**：<c>OpenIddictDataSeedContributor</c> 里
    /// 授权类型的 <c>default</c> 分支会为自定义授权类型补上
    /// <c>Permissions.Prefixes.GrantType + "{值}"</c>，因此将来只需在
    /// DbMigrator 的 appsettings 里加一个客户端、把 GrantTypes 写成这个值，
    /// 并实现一个对应的令牌端点处理器即可，不需要改种子框架。
    /// </summary>
    public const string WeChatMiniProgramGrantType = "urn:wechat:miniprogram";
}
