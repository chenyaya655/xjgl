using Shouldly;
using Xunit;

namespace Xjgl.Photos;

/// <summary>
/// 熔断器的单元测试。
///
/// 熔断是照片上传韧性链路的核心正确性，所以它被写成了纯状态机，
/// 这里用可注入的时钟把「打开 → 半开 → 恢复」的全过程穷尽验证一遍，
/// 不依赖任何真实 IO 与真实时间流逝。
/// </summary>
public class PhotoCircuitBreakerTests
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private PhotoCircuitBreaker Create(
        int minimumSamples = 4,
        double failureRate = 0.5,
        int openSeconds = 30)
    {
        return new PhotoCircuitBreaker(
            new PhotoCircuitBreakerOptions
            {
                MinimumSamples = minimumSamples,
                FailureRateThreshold = failureRate,
                OpenDuration = TimeSpan.FromSeconds(openSeconds),
                WindowSize = 50
            },
            () => _now);
    }

    private void Advance(int seconds) => _now = _now.AddSeconds(seconds);

    // ------------------------------------------------------------------
    // 关闭状态
    // ------------------------------------------------------------------

    [Fact]
    public void 初始为关闭状态_全部放行()
    {
        var breaker = Create();

        breaker.State.ShouldBe(PhotoCircuitState.Closed);

        for (var i = 0; i < 100; i++)
        {
            breaker.TryEnter().ShouldBeTrue();
            breaker.RecordSuccess();
        }

        breaker.State.ShouldBe(PhotoCircuitState.Closed);
    }

    [Fact]
    public void 样本不足时不熔断_哪怕全部失败()
    {
        // 最小样本 4：只失败 3 次不应熔断
        var breaker = Create(minimumSamples: 4);

        for (var i = 0; i < 3; i++)
        {
            breaker.TryEnter().ShouldBeTrue();
            breaker.RecordFailure();
        }

        breaker.State.ShouldBe(PhotoCircuitState.Closed);
    }

    [Fact]
    public void 失败率达到阈值且样本足够时熔断()
    {
        var breaker = Create(minimumSamples: 4, failureRate: 0.5);

        for (var i = 0; i < 4; i++)
        {
            breaker.TryEnter();
            breaker.RecordFailure();
        }

        breaker.State.ShouldBe(PhotoCircuitState.Open);
    }

    [Fact]
    public void 失败率未达阈值时不熔断()
    {
        var breaker = Create(minimumSamples: 4, failureRate: 0.5);

        // 2 成功 2 失败 → 失败率 50%，刚好达到阈值会熔断；
        // 这里改为 3 成功 1 失败 → 25%
        breaker.TryEnter(); breaker.RecordSuccess();
        breaker.TryEnter(); breaker.RecordSuccess();
        breaker.TryEnter(); breaker.RecordSuccess();
        breaker.TryEnter(); breaker.RecordFailure();

        breaker.State.ShouldBe(PhotoCircuitState.Closed);
    }

    // ------------------------------------------------------------------
    // 打开状态
    // ------------------------------------------------------------------

    [Fact]
    public void 熔断打开后_期间的所有请求都被拒绝()
    {
        var breaker = OpenBreaker(openSeconds: 30);

        for (var i = 0; i < 10; i++)
        {
            breaker.TryEnter().ShouldBeFalse();
        }

        breaker.GetSnapshot().RejectedCount.ShouldBe(10);
    }

    [Fact]
    public void 熔断到期后_进入半开且只放行一次试探()
    {
        var breaker = OpenBreaker(openSeconds: 30);

        Advance(29);
        breaker.TryEnter().ShouldBeFalse();

        Advance(2); // 累计 31 秒，已过期

        breaker.TryEnter().ShouldBeTrue();   // 第一次试探放行
        breaker.State.ShouldBe(PhotoCircuitState.HalfOpen);

        breaker.TryEnter().ShouldBeFalse();  // 试探未归还不能再放行
    }

    // ------------------------------------------------------------------
    // 半开与恢复
    // ------------------------------------------------------------------

    [Fact]
    public void 半开试探成功_恢复关闭并清空窗口()
    {
        var breaker = OpenBreaker(openSeconds: 30);

        Advance(31);
        breaker.TryEnter().ShouldBeTrue();
        breaker.RecordSuccess();

        breaker.State.ShouldBe(PhotoCircuitState.Closed);
        breaker.GetSnapshot().SampleCount.ShouldBe(0);

        // 恢复后全部放行
        breaker.TryEnter().ShouldBeTrue();
    }

    [Fact]
    public void 半开试探失败_重新熔断并重新计时()
    {
        var breaker = OpenBreaker(openSeconds: 30);

        Advance(31);
        breaker.TryEnter().ShouldBeTrue();
        breaker.RecordFailure();

        breaker.State.ShouldBe(PhotoCircuitState.Open);

        // 重新计时：再过 29 秒仍不放行
        Advance(29);
        breaker.TryEnter().ShouldBeFalse();

        Advance(2);
        breaker.TryEnter().ShouldBeTrue();
    }

    [Fact]
    public void 半开试探期间再次熔断_不会把状态卡死()
    {
        var breaker = OpenBreaker(openSeconds: 10);

        // 反复「到期 → 试探失败 → 再熔断」
        for (var round = 0; round < 5; round++)
        {
            Advance(11);
            breaker.TryEnter().ShouldBeTrue();
            breaker.RecordFailure();
            breaker.State.ShouldBe(PhotoCircuitState.Open);
        }

        Advance(11);
        breaker.TryEnter().ShouldBeTrue();
        breaker.RecordSuccess();
        breaker.State.ShouldBe(PhotoCircuitState.Closed);
    }

    // ------------------------------------------------------------------
    // 快照与复位
    // ------------------------------------------------------------------

    [Fact]
    public void 快照反映样本数失败率与拒绝次数()
    {
        var breaker = Create(minimumSamples: 10);

        breaker.TryEnter(); breaker.RecordSuccess();
        breaker.TryEnter(); breaker.RecordFailure();
        breaker.TryEnter(); breaker.RecordFailure();

        var snapshot = breaker.GetSnapshot();

        snapshot.State.ShouldBe(PhotoCircuitState.Closed);
        snapshot.SampleCount.ShouldBe(3);
        snapshot.FailureCount.ShouldBe(2);
        snapshot.FailureRate.ShouldBe(2.0 / 3, 0.001);
    }

    [Fact]
    public void 窗口超出上限时丢弃最旧样本()
    {
        var breaker = new PhotoCircuitBreaker(
            new PhotoCircuitBreakerOptions
            {
                MinimumSamples = 2, FailureRateThreshold = 0.9, WindowSize = 3
            },
            () => _now);

        breaker.TryEnter(); breaker.RecordSuccess();
        breaker.TryEnter(); breaker.RecordSuccess();
        breaker.TryEnter(); breaker.RecordSuccess();
        breaker.TryEnter(); breaker.RecordSuccess();

        breaker.GetSnapshot().SampleCount.ShouldBe(3);
    }

    [Fact]
    public void 手工复位后恢复放行()
    {
        var breaker = OpenBreaker(openSeconds: 300);

        breaker.TryEnter().ShouldBeFalse();

        breaker.Reset();

        breaker.State.ShouldBe(PhotoCircuitState.Closed);
        breaker.TryEnter().ShouldBeTrue();
    }

    /// <summary>构造一个已熔断的熔断器。</summary>
    private PhotoCircuitBreaker OpenBreaker(int openSeconds)
    {
        var breaker = Create(minimumSamples: 4, failureRate: 0.5, openSeconds: openSeconds);

        for (var i = 0; i < 4; i++)
        {
            breaker.TryEnter();
            breaker.RecordFailure();
        }

        breaker.State.ShouldBe(PhotoCircuitState.Open);

        return breaker;
    }
}
