using Volo.Abp;

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

    /// <summary>
    /// 导出/打印**后台任务**的提交：单用户每 10 秒 1 次。
    ///
    /// 任务式之后单次请求本身很便宜（只写一行任务记录就返回），
    /// 所以限的不是"这次请求有多重"，而是**别让一个人排队刷出几百个渲染任务**——
    /// 那些任务是串行被 worker 消费的，刷满队列等于把全校的导出都堵在他后面。
    /// </summary>
    public static readonly RateLimitPolicy ExportTask = new("export-task", Capacity: 3, RefillPerSecond: 0.1);

    /// <summary>全部内置策略，供"配置里没写就用内置默认"的查找使用。</summary>
    public static readonly IReadOnlyList<RateLimitPolicy> BuiltIn =
        new[] { PhotoUpload, PhotoExport, Print, ExportTask };

    /// <summary>按名字找内置策略；名字拼错时返回 <c>null</c>（由调用方决定是报错还是放行）。</summary>
    public static RateLimitPolicy? Find(string? name)
    {
        if (name.IsNullOrWhiteSpace())
        {
            return null;
        }

        foreach (var policy in BuiltIn)
        {
            if (string.Equals(policy.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return policy;
            }
        }

        return null;
    }
}

/// <summary>
/// 限流维度。
///
/// 【为什么不能只有一个"用户"维度】
/// 只按用户限流时，"某校整栋楼同时上传"是挡不住的：几十个教师各用各的账号，
/// 每个人的速率都合规，但 NAS 面对的是叠加后的总流量 —— 而这正是要防的场景。
///
/// 【维度怎么选（实测代价）】
///   - <see cref="User"/>：来自令牌，零成本，必开。
///   - <see cref="Ip"/>：来自请求头，零成本。**它才是"整栋楼"那条的对应维度** ——
///     学校往往是整个出口共用一个 IP，按 IP 限流天然覆盖这个场景，
///     而且不需要为每个请求去查"这个用户属于哪个学校"（那是每请求一次库的开销）。
///   - <see cref="Global"/>：单个键，零成本。它防的是"没人越界但总量把后端压垮"。
///
/// 刻意**不做**"按学校组织"维度：那需要在每请求解析用户组织机构（一次库查询或把机构写进令牌），
/// 而 IP 维度已经覆盖了它要解决的真实场景。少一个每请求的库查询，
/// 比多一个语义更漂亮但更贵的维度划算。
/// </summary>
public static class RateLimitDimensions
{
    public const string User = "user";
    public const string Ip = "ip";
    public const string Global = "global";

    /// <summary>默认开启的维度。按学校/全局的场景都要覆盖，所以三者都开。</summary>
    public static readonly IReadOnlyList<string> Default = new[] { User, Ip, Global };

    public static readonly IReadOnlyList<string> All = new[] { User, Ip, Global };
}

/// <summary>
/// 取限流策略。
///
/// 【为什么要有它，而不是直接用 <see cref="RateLimitPolicies"/> 的静态字段】
/// 策略值（容量、补充速率）原先写死在代码里，现场调参必须改代码重新发布 ——
/// 而限流参数恰恰是最需要"上线后按真实流量调"的那类值。
/// 有了这个抽象，调用方只认策略**名字**，数值由配置决定（见 Host 的 <c>Xjgl:RateLimit</c> 一节）。
/// </summary>
public interface IRateLimitPolicyProvider
{
    /// <summary>
    /// 按名字取策略。配置里有就覆盖内置默认；配置里没有就用内置默认；
    /// 两边都没有（名字拼错）时**抛异常**而不是放行 —— 名字拼错会让限流静默失效，
    /// 那种"看起来有防护其实没有"的状态比启动即失败危险得多。
    /// </summary>
    RateLimitPolicy Get(string name);

    /// <summary>该策略启用的限流维度。</summary>
    IReadOnlyList<string> GetDimensions(string policyName);
}
