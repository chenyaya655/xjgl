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
    /// 按策略名做多维限流；被限流时返回 <c>429</c>（带 <c>Retry-After</c>），否则返回 <c>null</c>。
    ///
    /// 用法：
    /// <code>
    /// var limited = await EnforceRateLimitAsync(_rateLimiter, _rateLimitPolicies, RateLimitPolicies.PhotoUpload.Name);
    /// if (limited != null) { return limited; }
    /// </code>
    ///
    /// 参数传**策略名**而不是策略对象：策略的容量与速率由配置决定（<c>Xjgl:RateLimit</c>），
    /// 传对象就要求每个调用点自己持有那份数值，现场调参得改代码 —— 那正是债 27 要消掉的东西。
    ///
    /// 限流器与策略提供者都由调用方（构造函数注入）传进来，而不是属性注入：
    /// 上传与导出都在关键链路上，属性注入漏配的后果是运行期 500，
    /// 而构造函数注入在编译期就管住了。
    /// </summary>
    protected async Task<IActionResult?> EnforceRateLimitAsync(
        ITokenBucketRateLimiter rateLimiter,
        IRateLimitPolicyProvider policies,
        string policyName,
        int permits = 1,
        CancellationToken cancellationToken = default)
    {
        var policy = policies.Get(policyName);
        var dimensions = policies.GetDimensions(policy.Name);

        // 逐维度判定，任一维度拒绝即拒绝。
        // 刻意**不**做"先全部试一遍再决定要不要扣"——那需要两阶段提交，
        // 而在限流这种场景里，宁可让被拒的请求多消耗掉前面几个维度的令牌
        // （代价是严格了一点），也不要为了精确而引入一个可能与 Redis 状态不一致的协议。
        var retryAfter = TimeSpan.Zero;

        foreach (var dimension in dimensions)
        {
            var key = BuildDimensionKey(policy.Name, dimension);

            if (key == null)
            {
                // 该维度在本请求上取不到值（例如匿名请求没有用户）——跳过而不是当成
                // "一个叫 null 的桶"，后者会把所有匿名流量并进同一个桶互相影响
                continue;
            }

            var decision = await rateLimiter.AcquireAsync(policy, key, permits, cancellationToken);

            if (!decision.IsAllowed && decision.RetryAfter > retryAfter)
            {
                retryAfter = decision.RetryAfter;
            }
        }

        if (retryAfter <= TimeSpan.Zero)
        {
            return null;
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));

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

    /// <summary>
    /// 构造某个维度的限流键；该维度不适用时返回 <c>null</c>。
    ///
    /// 键里带策略名：不同策略各自一个桶，否则"上传攒下的令牌"会被拿去抵"导出"的额度。
    /// </summary>
    private string? BuildDimensionKey(string policyName, string dimension)
    {
        switch (dimension)
        {
            case RateLimitDimensions.User:
                var userId = CurrentUser.Id?.ToString();
                return userId == null ? null : $"{policyName}:user:{userId}";

            case RateLimitDimensions.Ip:
                var ip = ResolveClientIp();
                return ip == null ? null : $"{policyName}:ip:{ip}";

            case RateLimitDimensions.Global:
                return $"{policyName}:global";

            default:
                return null;
        }
    }

    /// <summary>
    /// 取客户端 IP，优先 <c>X-Forwarded-For</c> 的第一段（生产走 nginx 反代，
    /// 此时 <c>RemoteIpAddress</c> 是网关地址，全校会被算成同一个来源）。
    ///
    /// ⚠ <c>X-Forwarded-For</c> 是**客户端可伪造**的。所以 IP 维度是**容量护栏、不是安全边界**：
    /// 它能防住"一栋楼共用一个出口 IP 的正常洪峰"，但挡不住刻意伪造头部的人 ——
    /// 而后者本来也应该由 nginx 覆写该头、以及账号维度来兜。
    /// 把它当安全边界用（例如"按 IP 封禁"）之前，必须先让 nginx 强制覆写这个头。
    /// </summary>
    private string? ResolveClientIp()
    {
        var forwarded = Request.Headers["X-Forwarded-For"].ToString();

        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();

            if (first.Length > 0)
            {
                return first;
            }
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }
}
