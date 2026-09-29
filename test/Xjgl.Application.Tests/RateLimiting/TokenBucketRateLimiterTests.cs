using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
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
    // 配置化策略（债 27）
    //
    // 【为什么这几条必须有】
    // 限流数值挪到配置之后，"配错"变成了一类新的、**只在现场出现**的故障：
    // 策略名拼错、容量配成 0、维度名写错 —— 三种错法的表现完全不同，
    // 而且都不在编译期。这里把它们逐条钉死：
    //   - 名字拼错 → 抛异常（静默不限流比启动失败危险得多）
    //   - 容量非法 → 退回默认（配成 0 会把全站打成 429）
    //   - 维度名错 → 忽略该项并保留其余（不能静默用默认全集，那会让现场以为关掉了）
    // ------------------------------------------------------------------

    private static IRateLimitPolicyProvider PolicyProvider(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

        return new RateLimitPolicyProvider(
            config, NullLogger<RateLimitPolicyProvider>.Instance);
    }

    [Fact]
    public void 策略_配置里没写时回落到内置默认()
    {
        var provider = PolicyProvider();

        var policy = provider.Get(RateLimitPolicies.PhotoUpload.Name);

        policy.Capacity.ShouldBe(RateLimitPolicies.PhotoUpload.Capacity);
        policy.RefillPerSecond.ShouldBe(RateLimitPolicies.PhotoUpload.RefillPerSecond);
    }

    [Fact]
    public void 策略_配置里写了就用配置值()
    {
        var provider = PolicyProvider(
            ("Xjgl:RateLimit:photo-upload:Capacity", "1"),
            ("Xjgl:RateLimit:photo-upload:RefillPerSecond", "0.1"));

        var policy = provider.Get(RateLimitPolicies.PhotoUpload.Name);

        policy.Capacity.ShouldBe(1, "现场调参必须立刻生效，否则这个配置项等于没有");
        policy.RefillPerSecond.ShouldBe(0.1);
        policy.Name.ShouldBe(RateLimitPolicies.PhotoUpload.Name, "名字仍以内置常量为准");
    }

    [Fact]
    public void 策略_名字拼错时抛异常_绝不静默放行()
    {
        var provider = PolicyProvider();

        var ex = Should.Throw<InvalidOperationException>(() => provider.Get("photo-uplaod"));

        ex.Message.ShouldContain("photo-upload", Case.Sensitive,
            "报错要把可用策略列出来，否则现场只能去翻代码");
    }

    [Fact]
    public void 策略_容量配成0时退回默认而不是把全站打成429()
    {
        // Capacity=0 的桶永远取不到令牌 —— 那等于把上传接口整个关掉，
        // 而现场的本意可能只是"想调小一点"。这种情况下退回默认并告警才是对的。
        var provider = PolicyProvider(
            ("Xjgl:RateLimit:photo-upload:Capacity", "0"),
            ("Xjgl:RateLimit:photo-upload:RefillPerSecond", "5"));

        var policy = provider.Get(RateLimitPolicies.PhotoUpload.Name);

        policy.Capacity.ShouldBe(RateLimitPolicies.PhotoUpload.Capacity);
    }

    [Fact]
    public void 策略_补充速率配成负数时退回默认()
    {
        var provider = PolicyProvider(
            ("Xjgl:RateLimit:photo-upload:Capacity", "5"),
            ("Xjgl:RateLimit:photo-upload:RefillPerSecond", "-1"));

        var policy = provider.Get(RateLimitPolicies.PhotoUpload.Name);

        policy.RefillPerSecond.ShouldBe(RateLimitPolicies.PhotoUpload.RefillPerSecond);
    }

    [Fact]
    public void 策略_只配了一半时另一半用默认()
    {
        var provider = PolicyProvider(("Xjgl:RateLimit:photo-upload:Capacity", "9"));

        var policy = provider.Get(RateLimitPolicies.PhotoUpload.Name);

        policy.Capacity.ShouldBe(9);
        policy.RefillPerSecond.ShouldBe(RateLimitPolicies.PhotoUpload.RefillPerSecond);
    }

    [Fact]
    public void 维度_没配置时用默认全集()
    {
        var provider = PolicyProvider();

        provider.GetDimensions(RateLimitPolicies.PhotoUpload.Name)
            .ShouldBe(RateLimitDimensions.Default);
    }

    [Fact]
    public void 维度_按配置裁剪()
    {
        var provider = PolicyProvider(
            ("Xjgl:RateLimit:export-task:Dimensions:0", "user"),
            ("Xjgl:RateLimit:export-task:Dimensions:1", "ip"));

        provider.GetDimensions(RateLimitPolicies.ExportTask.Name)
            .ShouldBe(new[] { "user", "ip" });
    }

    [Fact]
    public void 维度_含未知项时只忽略该项并保留其余()
    {
        // 静默用默认全集会让现场以为"我关掉了 IP 维度"却没关掉；
        // 抛异常则会把限流接口打成 500。所以正确行为是"忽略那一项 + 保留其余 + 告警"。
        var provider = PolicyProvider(
            ("Xjgl:RateLimit:export-task:Dimensions:0", "user"),
            ("Xjgl:RateLimit:export-task:Dimensions:1", "school"),
            ("Xjgl:RateLimit:export-task:Dimensions:2", "global"));

        provider.GetDimensions(RateLimitPolicies.ExportTask.Name)
            .ShouldBe(new[] { "user", "global" }, "未知维度 school 被忽略，其余保留");
    }

    [Fact]
    public void 维度_全部写错时回落到默认全集而不是空集()
    {
        // 空集意味着"一个维度都不限" —— 那等于限流整个失效，
        // 而调用方传的明明是一个合法策略名。
        var provider = PolicyProvider(
            ("Xjgl:RateLimit:export-task:Dimensions:0", "school"),
            ("Xjgl:RateLimit:export-task:Dimensions:1", "ipv6"));

        provider.GetDimensions(RateLimitPolicies.ExportTask.Name)
            .ShouldBe(RateLimitDimensions.Default);
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
