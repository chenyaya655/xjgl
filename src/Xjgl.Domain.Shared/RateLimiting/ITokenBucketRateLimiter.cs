namespace Xjgl.RateLimiting;

/// <summary>限流策略（令牌桶）。</summary>
public sealed record RateLimitPolicy(
    string Name,
    /// <summary>桶容量：允许的突发量。</summary>
    int Capacity,
    /// <summary>每秒补充的令牌数：长期平均速率。</summary>
    double RefillPerSecond);

/// <summary>一次限流判定的结果。</summary>
public sealed record RateLimitResult(bool IsAllowed, double RemainingTokens, TimeSpan RetryAfter)
{
    public static RateLimitResult Allow(double remaining) => new(true, remaining, TimeSpan.Zero);

    public static RateLimitResult Deny(TimeSpan retryAfter) => new(false, 0, retryAfter);
}

/// <summary>
/// 令牌桶限流。
///
/// 【为什么是令牌桶而不是"每秒钟最多 N 次"】
/// 固定窗口计数在窗口边界会放过两倍流量（第 1 秒末尾 N 次 + 第 2 秒开头 N 次），
/// 而令牌桶天然支持"攒一会儿再突发"：教师一次选 50 张照片上传，
/// 前端会并发发几个请求 —— 这些都是正常用法，不该被误伤。
///
/// 【为什么桶状态放在分布式缓存里】
/// 本系统的 <c>IDistributedCache</c> 在生产环境是 **Redis 主 + 内存兜底**的容错装饰器
/// （见 Host 的 ResilientDistributedCache）：Redis 可用时所有实例共享同一份桶状态；
/// Redis 不可用时自动降级为进程内缓存，限流精度下降但**不会因为缓存故障而挡住上传**。
/// </summary>
public interface ITokenBucketRateLimiter
{
    /// <summary>
    /// 尝试取走 <paramref name="permits"/> 个令牌。
    ///
    /// <paramref name="key"/> 是限流维度（例如 <c>upload:{userId}</c>）：
    /// 按用户限流而不是按 IP —— 一所学校可能整栋楼共用一个出口 IP，
    /// 按 IP 限流会把"同事在上传"算到某个人头上。
    /// </summary>
    Task<RateLimitResult> AcquireAsync(
        RateLimitPolicy policy,
        string key,
        int permits = 1,
        CancellationToken cancellationToken = default);
}

/// <summary>系统内置的限流策略。</summary>
public static class RateLimitPolicies
{
    /// <summary>照片上传：单用户每秒 5 次请求（一次请求可以带多张照片）。</summary>
    public static readonly RateLimitPolicy PhotoUpload = new("photo-upload", Capacity: 5, RefillPerSecond: 5);

    /// <summary>照片打包导出：单用户每 5 秒 1 次（导出要读大量文件并压缩，比上传重得多）。</summary>
    public static readonly RateLimitPolicy PhotoExport = new("photo-export", Capacity: 2, RefillPerSecond: 0.2);

    /// <summary>打印：单用户每 2 秒 1 次（PDF 渲染是 CPU 活）。</summary>
    public static readonly RateLimitPolicy Print = new("print", Capacity: 3, RefillPerSecond: 0.5);
}
