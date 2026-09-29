using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Volo.Abp;

namespace Xjgl.Sso;

/// <summary>
/// 浏览器导航用的 SSO 入口。
///
/// 【它为什么存在】
/// 授权端点发现"这个浏览器还没登录"时，只能把浏览器 <b>302</b> 到某个地址。
/// 那个地址必须由服务器提供（前端是独立部署的 SPA，Host 不该硬编码它的路由），
/// 所以由这里做一次"转交"：把 returnUrl 交给前端登录页，登录完再回来。
///
/// 本仓库这一轮只保证服务端骨架成立（不改造前端页面），因此这里拼接的是前端
/// <b>已有的</b>登录路由约定（见 web/apps/pc/src/router/index.ts 的 /login 与 redirect 参数），
/// 而不是新发明一个参数名 —— 新发明的结果是前端根本不认。
/// </summary>
[Route("sso")]
[AllowAnonymous]
public class SsoSessionController : Controller
{
    private readonly IConfiguration _configuration;

    public SsoSessionController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet("login")]
    public IActionResult Login([FromQuery(Name = SsoConstants.ReturnUrlParameter)] string? returnUrl)
    {
        var clientUrl = _configuration[SsoConstants.ClientUrlConfigKey]?.TrimEnd('/');

        if (clientUrl.IsNullOrWhiteSpace())
        {
            // 没有前端地址就没法转交。返回明确的服务不可用，而不是 302 到一个空地址 ——
            // 后者在浏览器里只表现为"跳到了一个不存在的站点"，排查时看不出根因。
            return Problem(
                title: "SSO 登录入口未配置",
                detail: $"缺少配置 {SsoConstants.ClientUrlConfigKey}，无法把浏览器转交给前端登录页。",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // 前端登录路由可配置，默认 /login（与 web/apps/pc/src/router/index.ts 一致）。
        // 做成配置项而不是常量：H5 端将来接 SSO 时路由前缀不同，改配置即可。
        var loginPath = _configuration[SsoConstants.LoginPathConfigKey];
        if (loginPath.IsNullOrWhiteSpace())
        {
            loginPath = "/login";
        }

        var redirect = SafeReturnUrl(returnUrl) ?? "/";

        // ⚠ 这里**不能**自己 Uri.EscapeDataString(redirect)。
        //
        // `returnUrl` 是 ASP.NET Core 的 Cookie 挑战生成的，**它自己已经编码过一次**
        // （实测：`scope=openid profile` 到达这里时是 `scope=openid%20profile`）。
        // 再编码一次就会变成 `%2520`，服务端拿到的参数是字面量 "%20" —— 校验直接不过。
        // 实测后果：授权端点返回 **400**，而地址栏里参数"看起来都在"，极难定位。
        //
        // 正确做法是交给查询串构造器：它对**已解码**的值编码，得到唯一的正确形态。
        // `QueryHelpers.AddQueryString` 会先解码再编码（幂等），因此不会双重编码。
        var loginUrl = QueryHelpers.AddQueryString(
            $"{clientUrl}{loginPath}",
            "redirect",
            redirect);

        return Redirect(loginUrl);
    }

    /// <summary>
    /// 只接受本站的绝对路径，用于防开放重定向。
    ///
    /// 必须同时挡掉 <c>//evil.com</c> 与 <c>/\evil.com</c>：
    /// 浏览器把这两种都当作"协议相对地址"，也就是会把用户送到外站。
    /// 只判断 StartsWith('/') 是不够的 —— 这是一个真实的钓鱼跳板。
    /// </summary>
    private static string? SafeReturnUrl(string? returnUrl)
    {
        if (returnUrl.IsNullOrWhiteSpace())
        {
            return null;
        }

        if (!returnUrl!.StartsWith('/') || returnUrl.StartsWith("//") || returnUrl.StartsWith("/\\"))
        {
            return null;
        }

        return returnUrl;
    }
}
