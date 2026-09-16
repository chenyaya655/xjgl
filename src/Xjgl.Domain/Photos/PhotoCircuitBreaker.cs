namespace Xjgl.Photos;

/// <summary>熔断器状态。</summary>
public enum PhotoCircuitState
{
    /// <summary>正常放行。</summary>
    Closed,

    /// <summary>熔断打开，直接走降级。</summary>
    Open,

    /// <summary>半开：只放行一次试探请求。</summary>
    HalfOpen
}

public sealed class PhotoCircuitBreakerOptions
{
    /// <summary>判定熔断所需的最小样本数（样本太少时失败率不可信）。</summary>
    public int MinimumSamples { get; set; } = 20;

    /// <summary>失败率阈值（0~1）。</summary>
    public double FailureRateThreshold { get; set; } = 0.5;

    /// <summary>熔断保持时长；到期后进入半开试探。</summary>
    public TimeSpan OpenDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>滑动窗口大小。</summary>
    public int WindowSize { get; set; } = 50;
}

/// <summary>熔断器快照（监控用）。</summary>
public sealed class PhotoCircuitSnapshot
{
    public PhotoCircuitState State { get; init; }
    public int SampleCount { get; init; }
    public int FailureCount { get; init; }
    public double FailureRate { get; init; }
    public int RejectedCount { get; init; }
}

/// <summary>
/// 照片存储的熔断器 —— **纯状态机，不碰任何 IO**。
///
/// 刻意自己实现而不直接用 Polly：熔断的语义（失败率阈值 + 最小样本 +
/// 半开试探 + 恢复判定）是照片上传韧性链路的核心正确性所在，
/// 自己实现可以让它成为可被单元测试穷尽覆盖的纯逻辑，
/// 而不是依赖第三方库的行为假设。
///
/// 状态迁移：
///   Closed --失败率达到阈值且样本足够--> Open
///   Open   --经过 OpenDuration--> HalfOpen（放行一次试探）
///   HalfOpen --试探成功--> Closed（清空窗口）
///   HalfOpen --试探失败--> Open（重新计时）
///
/// 约定：<see cref="TryEnter"/> 返回 true 之后**必须**调用
/// <see cref="RecordSuccess"/> 或 <see cref="RecordFailure"/>，
/// 否则半开状态会因为试探未归还而卡住。
/// </summary>
public sealed class PhotoCircuitBreaker
{
    private readonly PhotoCircuitBreakerOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _sync = new();
    private readonly Queue<bool> _window = new();

    private PhotoCircuitState _state = PhotoCircuitState.Closed;
    private DateTimeOffset _openedAt;
    private bool _trialInFlight;
    private int _rejectedCount;

    public PhotoCircuitBreaker(
        PhotoCircuitBreakerOptions? options = null,
        Func<DateTimeOffset>? now = null)
    {
        _options = options ?? new PhotoCircuitBreakerOptions();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public PhotoCircuitState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// 尝试进入。返回 false 表示熔断打开，调用方应直接走降级路径而不必浪费一次超时。
    /// </summary>
    public bool TryEnter()
    {
        lock (_sync)
        {
            switch (_state)
            {
                case PhotoCircuitState.Closed:
                    return true;

                case PhotoCircuitState.Open:
                    if (_now() - _openedAt < _options.OpenDuration)
                    {
                        _rejectedCount++;
                        return false;
                    }

                    // 熔断到期 → 半开，放行一次试探
                    _state = PhotoCircuitState.HalfOpen;
                    _trialInFlight = true;
                    return true;

                case PhotoCircuitState.HalfOpen:
                    if (_trialInFlight)
                    {
                        _rejectedCount++;
                        return false;
                    }

                    _trialInFlight = true;
                    return true;

                default:
                    return true;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_sync)
        {
            switch (_state)
            {
                case PhotoCircuitState.HalfOpen:
                    // 试探成功 → 恢复
                    _state = PhotoCircuitState.Closed;
                    _trialInFlight = false;
                    _window.Clear();
                    return;

                case PhotoCircuitState.Open:
                    return;

                default:
                    _window.Enqueue(true);
                    Trim();
                    return;
            }
        }
    }

    public void RecordFailure()
    {
        lock (_sync)
        {
            switch (_state)
            {
                case PhotoCircuitState.HalfOpen:
                    // 试探失败 → 重新熔断
                    OpenInternal();
                    return;

                case PhotoCircuitState.Open:
                    return;

                default:
                    _window.Enqueue(false);
                    Trim();
                    Evaluate();
                    return;
            }
        }
    }

    public PhotoCircuitSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var total = _window.Count;
            var failures = _window.Count(x => !x);

            return new PhotoCircuitSnapshot
            {
                State = _state,
                SampleCount = total,
                FailureCount = failures,
                FailureRate = total == 0 ? 0 : (double)failures / total,
                RejectedCount = _rejectedCount
            };
        }
    }

    /// <summary>手工复位（运维用）。</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _state = PhotoCircuitState.Closed;
            _trialInFlight = false;
            _window.Clear();
        }
    }

    private void OpenInternal()
    {
        _state = PhotoCircuitState.Open;
        _openedAt = _now();
        _trialInFlight = false;
    }

    private void Trim()
    {
        while (_window.Count > _options.WindowSize)
        {
            _window.Dequeue();
        }
    }

    private void Evaluate()
    {
        var total = _window.Count;

        if (total < _options.MinimumSamples)
        {
            return;
        }

        var failures = _window.Count(x => !x);
        var rate = (double)failures / total;

        if (rate >= _options.FailureRateThreshold)
        {
            OpenInternal();
        }
    }
}
