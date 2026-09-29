using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Xjgl.RateLimiting;

namespace Xjgl.Application.RateLimiting;

/// <summary>
/// 从配置读限流策略；配置缺项时回落到代码内置默认值。
///
/// 配置形如：
/// <code>
/// "Xjgl": { "RateLimit": {
///   "export-task": { "Capacity": 5, "RefillPerSecond": 0.2, "Dimensions": ["user","ip","global"] }
/// }}
/// </code>
///
/// 【为什么"缺项回落"而不是"缺项报错"】
/// 策略名是代码里的常量（调用方传 <c>RateLimitPolicies.ExportTask.Name</c>），
/// 所以"配置里没写这个策略"是**正常状态**（用默认值即可），不是错误。
/// 真正该报错的是"调用方传了一个不存在的名字" —— 那由 <see cref="Get"/> 兜住。
///
/// 【为什么注册成单例】
/// 每个请求都要取策略，而它只是读几个配置值、解析一次就固定了；
/// 做成瞬态会在每次限流判定时重复解析字符串。
/// </summary>
public class RateLimitPolicyProvider : IRateLimitPolicyProvider, ISingletonDependency
{
    private const string ConfigSection = "Xjgl:RateLimit";

    private readonly IConfiguration _configuration;
    private readonly ILogger<RateLimitPolicyProvider> _logger;

    public RateLimitPolicyProvider(IConfiguration configuration, ILogger<RateLimitPolicyProvider> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public RateLimitPolicy Get(string name)
    {
        var fallback = RateLimitPolicies.Find(name);

        if (fallback == null)
        {
            // 名字拼错**必须炸**：否则限流会静默失效，
            // 而"看起来有防护、实际没有"比"启动就失败"危险得多
            throw new InvalidOperationException(
                $"未知的限流策略「{name}」。可用策略：{string.Join("、", RateLimitPolicies.BuiltIn.Select(p => p.Name))}。");
        }

        var section = _configuration.GetSection($"{ConfigSection}:{fallback.Name}");

        // 配置里没写这一节 → 用内置默认，这是正常情况，不记日志也不报错
        if (!section.Exists())
        {
            return fallback;
        }

        var capacity = section.GetValue<int?>("Capacity") ?? fallback.Capacity;
        var refill = section.GetValue<double?>("RefillPerSecond") ?? fallback.RefillPerSecond;

        if (capacity <= 0 || refill <= 0)
        {
            // 配成 0 或负数会让桶永远取不到令牌 —— 表现为"所有请求 429"，
            // 也就是把限流配成了全站不可用。宁可退回默认并告警。
            _logger.LogWarning(
                "[限流] 策略 {Name} 的配置非法（Capacity={Capacity}、RefillPerSecond={Refill}），已退回内置默认值。",
                fallback.Name, capacity, refill);

            return fallback;
        }

        if (capacity != fallback.Capacity || Math.Abs(refill - fallback.RefillPerSecond) > double.Epsilon)
        {
            _logger.LogInformation(
                "[限流] 策略 {Name} 使用配置值：Capacity={Capacity}、RefillPerSecond={Refill}（内置默认为 {DC}/{DR}）。",
                fallback.Name, capacity, refill, fallback.Capacity, fallback.RefillPerSecond);
        }

        return new RateLimitPolicy(fallback.Name, capacity, refill);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetDimensions(string policyName)
    {
        var section = _configuration.GetSection($"{ConfigSection}:{policyName}:Dimensions");

        var configured = section.Get<string[]>();

        if (configured == null || configured.Length == 0)
        {
            return RateLimitDimensions.Default;
        }

        var unknown = configured
            .Where(d => !RateLimitDimensions.All.Contains(d, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unknown.Count > 0)
        {
            // 维度名写错时**只忽略这一项并告警**，不抛异常也不静默用默认全集：
            // 抛异常会让限流把整个接口打成 500，而静默用默认全集会让现场以为"我关掉了 IP 维度"却没关掉。
            _logger.LogWarning(
                "[限流] 策略 {Name} 的维度配置含未知项 {Unknown}，它们已被忽略；可用维度：{All}。",
                policyName, string.Join("、", unknown), string.Join("、", RateLimitDimensions.All));

            configured = configured
                .Where(d => RateLimitDimensions.All.Contains(d, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        return configured.Length == 0 ? RateLimitDimensions.Default : configured;
    }
}
