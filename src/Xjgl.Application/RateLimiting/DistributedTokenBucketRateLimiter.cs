using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Timing;
using Xjgl.RateLimiting;

namespace Xjgl.Application.RateLimiting;

/// <summary>
/// 基于分布式缓存的令牌桶限流实现。
///
/// 【为什么不用"每秒计数"】
/// 固定窗口在边界会放过双倍流量；令牌桶允许"攒一会儿再突发"，
/// 而教师一次选几十张照片并发的场景本来就是正常用法。
///
/// 【为什么用 IDistributedCache 而不是直连 Redis】
/// 本系统的 IDistributedCache 在生产是**容错装饰器**（Redis 主 + 内存兜底），
/// 复用它就自动继承了那套降级策略：Redis 挂了限流精度下降，但**不会挡住上传**。
/// 直连 Redis 会引入独立的连接与故障面，还要在业务层依赖 StackExchange.Redis。
///
/// 【已知局限（写在技术债里）】
/// 多实例部署时，每个实例只在自己进程内加锁，实际速率上限会被放大到 N 倍。
/// 要做到严格全局限流需要把"读-算-写"合并成 Redis 的 Lua 脚本（原子执行）。
/// 对本系统来说，限流的目的是保护 NAS 不被突发流量打垮，而这一层还有舱壁与熔断兜底，
/// 因此先用这个实现，把复杂度留在能验证的地方。
/// </summary>
public class DistributedTokenBucketRateLimiter : ITokenBucketRateLimiter, ITransientDependency
{
    /// <summary>
    /// 进程内的按键互斥。
    ///
    /// 必须**静态**：限流器本身是瞬态的，锁如果跟着实例走就等于没加。
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LocalGates = new(StringComparer.Ordinal);

    private readonly IDistributedCache _cache;
    private readonly IClock _clock;
    private readonly ILogger<DistributedTokenBucketRateLimiter> _logger;

    public DistributedTokenBucketRateLimiter(
        IDistributedCache cache,
        IClock clock,
        ILogger<DistributedTokenBucketRateLimiter> logger)
    {
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RateLimitResult> AcquireAsync(
        RateLimitPolicy policy,
        string key,
        int permits = 1,
        CancellationToken cancellationToken = default)
    {
        if (policy.Capacity <= 0 || policy.RefillPerSecond <= 0)
        {
            return RateLimitResult.Allow(policy.Capacity);
        }

        // 键里只放"业务维度 + 用户"：策略名已经体现在调用方给的 key 里，
        // 再拼一次会得到 ratelimit:print:print:{userId} 这种重复可读性差的名字
        var cacheKey = $"ratelimit:{key}";
        var gate = LocalGates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);

        try
        {
            var now = _clock.Now;
            var bucket = await ReadAsync(cacheKey, policy, now, cancellationToken);

            var available = bucket.Refill(policy, now);

            if (available < permits)
            {
                // 差几个令牌就要等几个令牌补充出来
                var waitSeconds = (permits - available) / policy.RefillPerSecond;

                return RateLimitResult.Deny(TimeSpan.FromSeconds(Math.Max(waitSeconds, 0.2)));
            }

            var remaining = available - permits;

            await WriteAsync(cacheKey, new Bucket(remaining, now), policy, cancellationToken);

            return RateLimitResult.Allow(remaining);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Bucket> ReadAsync(
        string cacheKey,
        RateLimitPolicy policy,
        DateTime now,
        CancellationToken cancellationToken)
    {
        try
        {
            var raw = await _cache.GetStringAsync(cacheKey, cancellationToken);

            if (!raw.IsNullOrWhiteSpace() && Bucket.TryParse(raw!, out var parsed))
            {
                return parsed;
            }
        }
        catch (Exception ex)
        {
            // **失败放行**：限流是保护措施，不能反过来变成"缓存一抖动全校就传不了照片"
            _logger.LogWarning(ex, "[限流] 读取桶状态失败，本次直接放行：{Key}", cacheKey);
        }

        return new Bucket(policy.Capacity, now);
    }

    private async Task WriteAsync(
        string cacheKey,
        Bucket bucket,
        RateLimitPolicy policy,
        CancellationToken cancellationToken)
    {
        try
        {
            // 空桶放满所需的时间 + 一点余量：桶闲置后自然过期，不占缓存
            var lifetime = TimeSpan.FromSeconds(policy.Capacity / policy.RefillPerSecond + 60);

            await _cache.SetStringAsync(
                cacheKey,
                bucket.Serialize(),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime },
                cancellationToken);
        }
        catch (Exception ex)
        {
            // 写失败同样不回滚判定：本次放行已经发生，硬要报错只会让用户重试更多次
            _logger.LogWarning(ex, "[限流] 写入桶状态失败：{Key}", cacheKey);
        }
    }

    /// <summary>桶状态：剩余令牌 + 上次补充时间。用最简文本格式，避免为两个数字引入序列化开销。</summary>
    private readonly record struct Bucket(double Tokens, DateTime LastRefill)
    {
        public double Refill(RateLimitPolicy policy, DateTime now)
        {
            var elapsed = (now - LastRefill).TotalSeconds;

            return elapsed <= 0
                ? Math.Min(policy.Capacity, Tokens)
                : Math.Min(policy.Capacity, Tokens + elapsed * policy.RefillPerSecond);
        }

        public string Serialize()
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Tokens:F4}|{LastRefill.Ticks}");
        }

        public static bool TryParse(string raw, out Bucket bucket)
        {
            bucket = default;

            var parts = raw.Split('|');

            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var tokens)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            {
                return false;
            }

            bucket = new Bucket(tokens, new DateTime(ticks, DateTimeKind.Utc));

            return true;
        }
    }
}
