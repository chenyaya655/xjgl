using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Volo.Abp.Timing;
using Xjgl.Application.RateLimiting;
using Xjgl.RateLimiting;
using Xunit;

namespace Xjgl.RateLimiting;

/// <summary>
/// 令牌桶限流的单元测试（不碰数据库）。
///
/// 【为什么要单独测它】
/// 限流器最容易出的问题是**"看起来在工作"**：
///   1. 时间算错 → 桶永远补不满，用户被永久限流（或反之，永远不生效）；
///   2. 桶超过容量 → 长时间空闲后攒出一大堆令牌，一次突发把 NAS 打垮；
///   3. 缓存故障时抛异常 → 限流本身变成新的故障源（Redis 抖一下全校就传不了照片）。
/// 这三点都不会在界面上表现出来，只会以"某天突然传不上去/某天突然被打垮"的形式出现。
/// </summary>
public class TokenBucketRateLimiterTests
{
    private static readonly RateLimitPolicy Policy = new("test", Capacity: 5, RefillPerSecond: 5);

    private static DistributedTokenBucketRateLimiter Create(
        IDistributedCache? cache = null,
        TestClock? clock = null)
    {
        return new DistributedTokenBucketRateLimiter(
            cache ?? new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            clock ?? new TestClock(),
            NullLogger<DistributedTokenBucketRateLimiter>.Instance);
    }

    [Fact]
    public async Task 容量内放行_超出被拒()
    {
        var limiter = Create();

        for (var i = 0; i < 5; i++)
        {
            var decision = await limiter.AcquireAsync(Policy, "user-1");

            decision.IsAllowed.ShouldBeTrue($"第 {i + 1} 次应当放行（容量 5）");
        }

        var denied = await limiter.AcquireAsync(Policy, "user-1");

        denied.IsAllowed.ShouldBeFalse("第 6 次应当被拒");
        denied.RemainingTokens.ShouldBe(0);
        denied.RetryAfter.TotalSeconds.ShouldBeGreaterThan(0, "被拒时要告诉调用方等多久（前端要写进 Retry-After）");
    }

    [Fact]
    public async Task 等待后按速率恢复()
    {
        var clock = new TestClock();
        var limiter = Create(clock: clock);

        for (var i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync(Policy, "user-2");
        }

        (await limiter.AcquireAsync(Policy, "user-2")).IsAllowed.ShouldBeFalse();

        // 速率 5/s：等 1 秒就应当补满 5 个令牌
        clock.Advance(TimeSpan.FromSeconds(1));

        for (var i = 0; i < 5; i++)
        {
            (await limiter.AcquireAsync(Policy, "user-2")).IsAllowed
                .ShouldBeTrue($"等待 1 秒后第 {i + 1} 个令牌应当可用");
        }

        (await limiter.AcquireAsync(Policy, "user-2")).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task 长时间空闲也不会攒超过容量()
    {
        var clock = new TestClock();
        var limiter = Create(clock: clock);

        // 空闲一小时（如果实现里没有封顶，会攒出 18000 个令牌）
        clock.Advance(TimeSpan.FromHours(1));

        for (var i = 0; i < 5; i++)
        {
            (await limiter.AcquireAsync(Policy, "user-3")).IsAllowed.ShouldBeTrue();
        }

        (await limiter.AcquireAsync(Policy, "user-3")).IsAllowed
            .ShouldBeFalse("令牌必须封顶在容量：否则闲置后的一次突发会把 NAS 打垮");
    }

    [Fact]
    public async Task 不同用户互不影响()
    {
        var limiter = Create();

        for (var i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync(Policy, "user-a");
        }

        (await limiter.AcquireAsync(Policy, "user-a")).IsAllowed.ShouldBeFalse();

        // 同一个策略、另一个用户：不该受别人影响
        (await limiter.AcquireAsync(Policy, "user-b")).IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task 一次取多个令牌()
    {
        var limiter = Create();

        (await limiter.AcquireAsync(Policy, "user-c", permits: 3)).IsAllowed.ShouldBeTrue();

        var second = await limiter.AcquireAsync(Policy, "user-c", permits: 3);

        second.IsAllowed.ShouldBeFalse("只剩 2 个令牌，要 3 个应当被拒");
    }

    [Fact]
    public async Task 缓存故障时放行_限流不能变成新的故障源()
    {
        var limiter = Create(cache: new ThrowingCache());

        var decision = await limiter.AcquireAsync(Policy, "user-d");

        decision.IsAllowed.ShouldBeTrue(
            "Redis 抖动时宁可放开限流，也不能让全校上传直接失败 —— 上传链路上还有舱壁与熔断兜底");
    }

    [Fact]
    public async Task 非法策略直接放行()
    {
        var limiter = Create();

        var decision = await limiter.AcquireAsync(new RateLimitPolicy("broken", Capacity: 0, RefillPerSecond: 0), "user-e");

        decision.IsAllowed.ShouldBeTrue("策略配错时不应把人挡在门外");
    }

    // ------------------------------------------------------------------
    // 测试替身
    // ------------------------------------------------------------------

    /// <summary>可控时钟（限流的核心是时间，必须能"推进时间"来验证补充逻辑）。</summary>
    private sealed class TestClock : IClock
    {
        private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public DateTime Now => _now;

        public DateTimeKind Kind => DateTimeKind.Utc;

        public bool SupportsMultipleTimezone => false;

        public DateTime Normalize(DateTime dateTime) => dateTime;

        // 测试里不做时区换算（系统统一用 UTC 存储时间）
        public DateTime ConvertToUserTime(DateTime dateTime) => dateTime;

        public DateTimeOffset ConvertToUserTime(DateTimeOffset dateTime) => dateTime;

        public DateTime ConvertToUtc(DateTime dateTime) => dateTime;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    /// <summary>总是抛异常的缓存，用来验证"失败放行"。</summary>
    private sealed class ThrowingCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("Redis 不可用");

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
            => throw new InvalidOperationException("Redis 不可用");

        public void Refresh(string key) => throw new InvalidOperationException("Redis 不可用");

        public Task RefreshAsync(string key, CancellationToken token = default)
            => throw new InvalidOperationException("Redis 不可用");

        public void Remove(string key) => throw new InvalidOperationException("Redis 不可用");

        public Task RemoveAsync(string key, CancellationToken token = default)
            => throw new InvalidOperationException("Redis 不可用");

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => throw new InvalidOperationException("Redis 不可用");

        public Task SetAsync(
            string key,
            byte[] value,
            DistributedCacheEntryOptions options,
            CancellationToken token = default)
            => throw new InvalidOperationException("Redis 不可用");
    }
}
