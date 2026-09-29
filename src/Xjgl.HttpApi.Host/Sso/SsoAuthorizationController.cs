using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using Volo.Abp;

namespace Xjgl.Sso;

/// <summary>
/// 会话结束端点：在 ABP 既有能力之上补「Front-Channel 全局登出」的扇出。
///
/// 【本类为什么只剩这一个动作 —— 一次真实的重复开发教训】
/// 我最初还写了 <c>/connect/authorize</c> 的处理器，理由是"用程序集元数据核对过，
/// ABP 的 AspNetCore 模块里没有授权控制器"。**那个核对是错的**：我按
/// <c>AuthorizationController</c> / <c>OpenIddictController</c> 去搜符号，
/// 而 ABP 的真实类名是 <c>AuthorizeController</c>。零命中被我误读成"不存在"，
/// 于是重复实现了授权端点。
///
/// 后果不是编译错，而是运行期：
/// <code>
/// AmbiguousMatchException: The request matched multiple endpoints.
/// Matches: Xjgl.Sso.SsoAuthorizationController.AuthorizeAsync
///          Volo.Abp.OpenIddict.Controllers.AuthorizeController.HandleAsync
/// </code>
/// 即 <c>/connect/authorize</c> 直接 500 —— 整个 SSO 从此不可用。
/// 教训：**"没搜到"不等于"不存在"**，符号名猜错时零命中是必然的；
/// 要否定一个能力，必须用<b>能枚举全部成员</b>的手段，而不是拿几个猜出来的名字去撞。
///
/// 【为什么登出这一段不是重复开发】
/// ABP 的 <c>LogoutController</c>（文件叫 EndSessionController.cs，路由
/// <c>connect/endsession</c>）只做两件事：<c>SignInManager.SignOutAsync()</c> 与
/// <c>SignOut(OpenIddict...)</c>。它<b>不通知任何 RP</b> ——
/// 也就是「一处登出、所有已登录 RP 全部下线」这件事确实缺人做。
///
/// 【会话来源：复用 ABP 的 Identity 应用 Cookie，不自造方案】
/// 这里读的是 <c>IdentityConstants.ApplicationScheme</c> —— 与 ABP 的
/// <c>AuthorizeController</c> 完全同一个来源。早先版本自造了一个 <c>Xjgl.Sso</c> Cookie，
/// 结果与 ABP 的授权端点不兼容（实测：授权端点返回裸 401），已删除。
/// </summary>
[Route("connect")]
[AllowAnonymous]
public class SsoAuthorizationController : Controller
{
    private readonly IFrontChannelLogoutService _frontChannelLogout;
    private readonly IConfiguration _configuration;

    public SsoAuthorizationController(
        IFrontChannelLogoutService frontChannelLogout,
        IConfiguration configuration)
    {
        _frontChannelLogout = frontChannelLogout;
        _configuration = configuration;
    }

    /// <summary>
    /// 会话结束端点 —— 全局登出的入口（一处登出，所有已登录 RP 全部下线）。
    ///
    /// 顺序在这里是正确性的一部分：**先收集靶点，再撤销授权**。
    /// 原因是收集靶点时会按「授权仍然有效」过滤，而撤销正好会把状态改成 revoked。
    /// 反过来写的话，最该收到通知的那些 RP 反而一个都收不到 ——
    /// 而现象是"登出看起来成功了"，没有任何报错。
    /// </summary>
    [HttpGet("logout")]
    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync()
    {
        var request = HttpContext.GetOpenIddictServerRequest();
        var cancellationToken = HttpContext.RequestAborted;

        // 会话来源与 ABP 的授权端点一致：Identity 应用 Cookie。
        var session = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var subject = session.Succeeded && session.Principal != null
            ? FindSubject(session.Principal)
            : null;
        var sessionId = session.Succeeded
            ? session.Principal?.FindFirst(SsoConstants.SessionIdClaimType)?.Value
            : null;

        // 1) 收集需要通知的 RP（必须在撤销之前）
        var targets = subject.IsNullOrWhiteSpace()
            ? Array.Empty<FrontChannelLogoutTarget>()
            : await _frontChannelLogout.FindTargetsAsync(subject!, cancellationToken);

        // 2) 撤销该主体的令牌与授权，让 RP 手里的刷新令牌同时作废
        if (!subject.IsNullOrWhiteSpace())
        {
            await _frontChannelLogout.RevokeSubjectAsync(subject!, cancellationToken);
        }

        // 3) 清掉会话 Cookie。只清 Identity 应用 Cookie 一个：
        //    登出必须清掉"授权端点认的那个 Cookie"，清别的都没用。
        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);

        // 4) 渲染扇出页面：逐 iframe 通知各 RP，宽限期后回跳 post_logout_redirect_uri
        var html = await _frontChannelLogout.RenderAsync(
            targets,
            ResolveIssuer(),
            sessionId,
            request?.PostLogoutRedirectUri,
            request?.State,
            cancellationToken);

        // 刻意返回 Content 而不是 SignOut(OpenIddictServerAspNetCoreDefaults...):
        // 后者会立刻发出 302，页面一卸载浏览器就可能取消那些 iframe，
        // 于是"有些 RP 没收到通知"这件事会静默发生。宽限期是前通道登出固有的取舍。
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>
    /// 取主体标识。两个来源是有原因的：ABP 在 <c>UpdateAbpClaimTypes</c> 打开时
    /// 会把 <c>AbpClaimTypes.UserId</c> 映射成 <c>sub</c>，但历史会话 Cookie 里可能
    /// 仍是 <c>NameIdentifier</c>。两条都认，避免旧会话在部署后立刻失效。
    /// </summary>
    private static string? FindSubject(ClaimsPrincipal principal)
    {
        return principal.FindFirst(OpenIddictConstants.Claims.Subject)?.Value
               ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }

    /// <summary>
    /// 解析签发者标识。<b>优先用配置</b>而不是当前请求的 Host：
    /// 令牌里的 iss 来自配置，而 FC 登出通知里的 iss 必须与之一致，
    /// RP 会比对这两个值；用请求 Host 会在反向代理/多域名下不一致。
    /// </summary>
    private string ResolveIssuer()
    {
        var configured = _configuration[SsoConstants.IssuerConfigKey];

        return configured.IsNullOrWhiteSpace()
            ? $"{Request.Scheme}://{Request.Host}"
            : configured!.TrimEnd('/');
    }
}
