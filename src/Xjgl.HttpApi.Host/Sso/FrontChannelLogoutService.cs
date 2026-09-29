using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.OpenIddict.Applications;

namespace Xjgl.Sso;

/// <summary>一个需要被前通道登出通知的 RP。</summary>
/// <param name="ClientId">客户端标识，只用于日志与页面展示。</param>
/// <param name="Uri">RP 注册的 frontchannel_logout_uri（已校验为绝对 URI）。</param>
public sealed record FrontChannelLogoutTarget(string ClientId, string Uri);

/// <summary>一次全局登出对已存储凭据的清理结果，用于日志与排查。</summary>
/// <param name="RevokedTokens">被置为失效的令牌数（含刷新令牌）。</param>
/// <param name="RevokedAuthorizations">被撤销的授权数。</param>
public sealed record FrontChannelLogoutRevocationResult(long RevokedTokens, long RevokedAuthorizations);

/// <summary>
/// IdP 侧的前通道登出：找出「这个用户登录过哪些 RP」，撤销其凭据，并渲染扇出页面。
///
/// 【它解决的是什么问题】
/// 教师先登录学籍系统，再免登进入学分系统；之后在学籍系统点「退出」。
/// 如果只清掉学籍系统自己的 Cookie，学分系统那边的会话还活着 ——
/// 下一个用这台电脑的人打开学分系统就是别人的身份。这就是「一处登出、全部下线」。
///
/// 【为什么是「前通道」而不是「后通道」】
/// 前通道由浏览器逐个 iframe 通知 RP，不需要 IdP 能反向访问 RP 的内网地址；
/// 代价是可靠性依赖浏览器（见 SsoConstants.FrontChannelGracePeriod 的注释）。
/// OpenIddict 7.5.0 本身不实现该规范，ABP 只提供了存储与读取
/// （<c>FrontChannelLogoutUri</c> 列 + <c>IAbpApplicationManager.GetFrontChannelLogoutUriAsync</c>），
/// 扇出逻辑必须由应用自己写。
/// </summary>
public interface IFrontChannelLogoutService
{
    /// <summary>找出该用户当前仍然有效、且登记了前通道登出地址的 RP。</summary>
    Task<IReadOnlyList<FrontChannelLogoutTarget>> FindTargetsAsync(
        string subject, CancellationToken cancellationToken = default);

    /// <summary>撤销该用户的全部令牌与授权（让 RP 手里的刷新令牌同时作废）。</summary>
    Task<FrontChannelLogoutRevocationResult> RevokeSubjectAsync(
        string subject, CancellationToken cancellationToken = default);

    /// <summary>渲染带隐藏 iframe 的登出扇出页。</summary>
    Task<string> RenderAsync(
        IReadOnlyList<FrontChannelLogoutTarget> targets,
        string issuer,
        string? sessionId,
        string? postLogoutRedirectUri,
        string? state,
        CancellationToken cancellationToken = default);
}

public class FrontChannelLogoutService : IFrontChannelLogoutService, ITransientDependency
{
    private readonly IAbpApplicationManager _applicationManager;
    private readonly IOpenIddictAuthorizationManager _authorizationManager;
    private readonly IOpenIddictTokenManager _tokenManager;
    private readonly ILogger<FrontChannelLogoutService> _logger;

    public FrontChannelLogoutService(
        IAbpApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictTokenManager tokenManager,
        ILogger<FrontChannelLogoutService> logger)
    {
        _applicationManager = applicationManager;
        _authorizationManager = authorizationManager;
        _tokenManager = tokenManager;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FrontChannelLogoutTarget>> FindTargetsAsync(
        string subject, CancellationToken cancellationToken = default)
    {
        Check.NotNullOrWhiteSpace(subject, nameof(subject));

        var targets = new List<FrontChannelLogoutTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var authorization in _authorizationManager
            .FindBySubjectAsync(subject, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            // 只通知「仍然有效」的授权。已撤销/已失效的会话不该再被敲一次，
            // 否则登出会被放大成对 RP 的无效请求风暴。
            var status = await _authorizationManager.GetStatusAsync(authorization, cancellationToken);
            if (!string.Equals(status, OpenIddictConstants.Statuses.Valid, StringComparison.Ordinal))
            {
                continue;
            }

            var applicationId = await _authorizationManager.GetApplicationIdAsync(authorization, cancellationToken);
            if (applicationId.IsNullOrWhiteSpace())
            {
                continue;
            }

            var application = await _applicationManager.FindByIdAsync(applicationId!, cancellationToken);
            if (application == null)
            {
                // 客户端被删掉了但授权还在（种子配置改过 ClientId）——记一条日志而不是抛异常：
                // 登出流程绝不能因为一个孤儿授权就整条断掉，那会导致用户根本退不出去。
                _logger.LogWarning(
                    "SSO 登出：授权 {AuthorizationId} 指向的应用 {ApplicationId} 不存在，已跳过。",
                    await _authorizationManager.GetIdAsync(authorization, cancellationToken),
                    applicationId);
                continue;
            }

            var frontChannelLogoutUri = await _applicationManager
                .GetFrontChannelLogoutUriAsync(application, cancellationToken);

            if (frontChannelLogoutUri.IsNullOrWhiteSpace())
            {
                // 没登记前通道登出地址的 RP 只能靠访问令牌自然过期。
                // 这是刻意不报错的分支：绝大多数接入方一开始都不会提供该地址。
                continue;
            }

            var clientId = await _applicationManager.GetClientIdAsync(application, cancellationToken)
                           ?? applicationId!;

            if (!seen.Add(clientId))
            {
                continue;
            }

            targets.Add(new FrontChannelLogoutTarget(clientId, frontChannelLogoutUri!));
        }

        return targets;
    }

    public async Task<FrontChannelLogoutRevocationResult> RevokeSubjectAsync(
        string subject, CancellationToken cancellationToken = default)
    {
        Check.NotNullOrWhiteSpace(subject, nameof(subject));

        // 令牌先撤：RP 手里通常是 refresh_token 在先、access_token 在后，
        // 只撤授权不撤令牌的话，刷新令牌仍可能换出新的访问令牌。
        var revokedTokens = await _tokenManager.RevokeBySubjectAsync(subject, cancellationToken);
        var revokedAuthorizations = await _authorizationManager.RevokeBySubjectAsync(subject, cancellationToken);

        if (revokedTokens > 0 || revokedAuthorizations > 0)
        {
            _logger.LogInformation(
                "SSO 全局登出：主体 {Subject} 撤销令牌 {Tokens} 个、授权 {Authorizations} 个。",
                subject, revokedTokens, revokedAuthorizations);
        }

        return new FrontChannelLogoutRevocationResult(revokedTokens, revokedAuthorizations);
    }

    public Task<string> RenderAsync(
        IReadOnlyList<FrontChannelLogoutTarget> targets,
        string issuer,
        string? sessionId,
        string? postLogoutRedirectUri,
        string? state,
        CancellationToken cancellationToken = default)
    {
        Check.NotNullOrEmpty(issuer, nameof(issuer));

        var redirectUrl = BuildPostLogoutRedirectUrl(postLogoutRedirectUri, state);

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>正在退出所有已登录系统</title></head><body>");
        html.Append("<p id=\"xjgl-sso-message\">正在退出所有已登录系统…</p>");

        if (targets.Count == 0)
        {
            html.Append("<p>没有需要通知的其它系统。</p>");
        }
        else
        {
            // 每个 RP 一个隐藏 iframe。用 display:none 而不是不渲染：
            // 浏览器仍然会加载 display:none 的 iframe，而这正是通知送达的方式。
            foreach (var target in targets)
            {
                var notificationUrl = AppendQuery(
                    target.Uri,
                    (SsoConstants.FrontChannelIssuerParameter, issuer),
                    (SsoConstants.FrontChannelSessionIdParameter, sessionId));

                html.Append("<iframe style=\"display:none\" title=\"")
                    .Append(WebUtility.HtmlEncode(target.ClientId))
                    .Append("\" src=\"")
                    .Append(WebUtility.HtmlEncode(notificationUrl))
                    .Append("\"></iframe>");
            }
        }

        if (redirectUrl != null)
        {
            // 用 JsonSerializer 而不是手工拼引号：登出后回跳地址来自查询串，
            // 直接插进 <script> 会造成脚本注入。
            var scriptUrl = JsonSerializer.Serialize(redirectUrl, JsonOptions);

            html.Append("<p><a id=\"xjgl-sso-continue\" href=\"")
                .Append(WebUtility.HtmlEncode(redirectUrl))
                .Append("\">继续</a></p>");

            html.Append("<script>setTimeout(function(){location.replace(")
                .Append(scriptUrl)
                .Append(");},")
                .Append((int)SsoConstants.FrontChannelGracePeriod.TotalMilliseconds)
                .Append(");</script>");
        }
        else
        {
            html.Append("<p>已退出。可以关闭此页面。</p>");
        }

        html.Append("</body></html>");

        return Task.FromResult(html.ToString());
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 默认的编码器会把中文与 < > & 转成 \uXXXX，虽然安全但可读性差；
        // 这里只用于 JSON 字符串字面量，配合 HtmlEncode 之外仍需防止 </script> 逃逸。
        Encoder = JavaScriptEncoder.Default
    };

    /// <summary>
    /// 拼出登出后回跳地址。state 必须原样带回（OIDC 要求），否则 RP 侧会判定为串号并拒绝。
    /// </summary>
    private static string? BuildPostLogoutRedirectUrl(string? postLogoutRedirectUri, string? state)
    {
        if (postLogoutRedirectUri.IsNullOrWhiteSpace())
        {
            return null;
        }

        return state.IsNullOrWhiteSpace()
            ? postLogoutRedirectUri
            : AppendQuery(postLogoutRedirectUri!, ("state", state));
    }

    /// <summary>
    /// 往 URI 上追加查询参数。必须区分「已有查询串」的情况 ——
    /// 一律用 '?' 拼会得到 <c>...?a=1?iss=...</c>，RP 侧只会解析出第一个参数。
    /// </summary>
    private static string AppendQuery(string uri, params (string Key, string? Value)[] parameters)
    {
        var builder = new StringBuilder(uri);
        var separator = uri.Contains('?') ? '&' : '?';

        foreach (var (key, value) in parameters)
        {
            if (value.IsNullOrWhiteSpace())
            {
                continue;
            }

            builder.Append(separator)
                .Append(WebUtility.UrlEncode(key))
                .Append('=')
                .Append(WebUtility.UrlEncode(value));

            separator = '&';
        }

        return builder.ToString();
    }
}
