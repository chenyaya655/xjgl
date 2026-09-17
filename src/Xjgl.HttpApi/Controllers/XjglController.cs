using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc;
using Xjgl.RateLimiting;

namespace Xjgl.Controllers;

/// <summary>
/// 本系统 Controller 的公共基类。
///
/// 除了统一本地化资源，这里还收了一个**限流入口**：昂贵接口（上传、导出、渲染打印）
/// 都走它，避免每个 action 各写一遍 429 与 Retry-After 的报文，
/// 也避免有人新加接口时忘了限流（漏一个就等于给系统开了一个口子）。
/// </summary>
public abstract class XjglController : AbpControllerBase
{
    protected XjglController()
    {
        LocalizationResource = typeof(Localization.XjglResource);
    }

    /// <summary>
    /// 尝试取一个令牌；被限流时返回 <c>429</c>（带 <c>Retry-After</c>），否则返回 <c>null</c>。
    ///
    /// 用法：
    /// <code>
    /// var limited = await EnforceRateLimitAsync(_rateLimiter, RateLimitPolicies.PhotoUpload, "photo-upload");
    /// if (limited != null) { return limited; }
    /// </code>
    ///
    /// 限流器由调用方（构造函数注入）传进来，而不是属性注入：
    /// 上传与导出都在关键链路上，属性注入漏配的后果是运行期 500，
    /// 而构造函数注入在编译期就管住了。
    ///
    /// 为什么限流维度是"用户"而不是 IP：一所学校往往整栋楼共用一个出口 IP，
    /// 按 IP 限流会把同事的正常上传算到某个人头上。
    /// </summary>
    protected async Task<IActionResult?> EnforceRateLimitAsync(
        ITokenBucketRateLimiter rateLimiter,
        RateLimitPolicy policy,
        string subject,
        int permits = 1,
        CancellationToken cancellationToken = default)
    {
        var user = CurrentUser.Id?.ToString() ?? "anonymous";
        var decision = await rateLimiter.AcquireAsync(policy, $"{subject}:{user}", permits, cancellationToken);

        if (decision.IsAllowed)
        {
            return null;
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));

        Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        return StatusCode(
            StatusCodes.Status429TooManyRequests,
            new
            {
                message = $"操作过于频繁，请 {seconds} 秒后再试。",
                policy = policy.Name,
                retryAfterSeconds = seconds
            });
    }
}
