using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Xjgl.Caching;

/// <summary>
/// 容错的分布式缓存装饰器。
///
/// 对应方案文档「熔断降级矩阵」中的两条：
///   - Redis 不可用 → 降级为本地内存缓存，不阻塞业务；
///   - 主缓存恢复后自动回切。
///
/// 为什么必须自己实现：ABP 的 <c>AbpDistributedCacheOptions.HideErrors</c> 只对
/// ABP 自己包装的 <c>IDistributedCache&lt;T&gt;</c> 生效；而 ABP 的 SettingManagement、
/// PermissionManagement、FeatureManagement 的内部初始化器直接使用原生
/// <c>IDistributedCache</c>，Redis 抛异常会直接冒泡成 500。
///
/// **关键设计：失败后必须短路，而不是每次都去撞超时。**
/// 实测教训：Redis 密码配错时，StackExchange.Redis 每次操作要等满 5000ms
/// （"message timed out in the backlog ... no connection became available"），
/// 而一次登录要经过几十次缓存读写（OpenIddict 应用/作用域缓存、权限、设置、特性），
/// 于是 `POST /connect/token` **90 秒都返回不了** —— 明明「降级成功」，
/// 用户侧却表现为登录卡死。所以第一次失败后就开一个跳过窗口，
/// 窗口内直接走内存缓存，不再为每次操作支付超时代价；
/// 窗口到期放行一次试探，成功即恢复。
/// </summary>
public sealed class ResilientDistributedCache : IDistributedCache
{
    private static long _primaryFailureCount;
    private static long _fallbackHitCount;

    private readonly IDistributedCache _primary;
    private readonly IDistributedCache _fallback;
    private readonly ILogger<ResilientDistributedCache> _logger;
    private readonly TimeSpan _skipDuration;

    /// <summary>降级写入内存缓存时的最长存活时间，避免主缓存长时间不可用导致长期脏读。</summary>
    private static readonly TimeSpan FallbackWriteTtl = TimeSpan.FromSeconds(30);

    /// <summary>跳过主缓存的截止时刻（UTC Ticks）；0 表示不跳过。</summary>
    private long _skipUntilTicks;

    /// <summary>是否已经记录过「进入跳过窗口」的日志，避免刷屏。</summary>
    private long _skipLogged;

    public ResilientDistributedCache(
        IDistributedCache primary,
        IDistributedCache fallback,
        ILogger<ResilientDistributedCache> logger,
        TimeSpan? skipDuration = null)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
        _skipDuration = skipDuration ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>主缓存累计失败次数（监控用）。</summary>
    public static long PrimaryFailureCount => Interlocked.Read(ref _primaryFailureCount);

    /// <summary>降级命中累计次数（监控用）。</summary>
    public static long FallbackHitCount => Interlocked.Read(ref _fallbackHitCount);

    /// <summary>当前是否处于「跳过主缓存」窗口内（监控用）。</summary>
    public bool IsPrimarySkipped => !ShouldTryPrimary();

    // ------------------------------------------------------------------
    // 读
    // ------------------------------------------------------------------

    public byte[]? Get(string key)
    {
        if (ShouldTryPrimary())
        {
            try
            {
                var value = _primary.Get(key);
                OnPrimarySucceeded();
                return value;
            }
            catch (Exception ex)
            {
                OnPrimaryFailed(ex, key);
            }
        }

        Interlocked.Increment(ref _fallbackHitCount);
        return _fallback.Get(key);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        if (ShouldTryPrimary())
        {
            try
            {
                var value = await _primary.GetAsync(key, token);
                OnPrimarySucceeded();
                return value;
            }
            catch (Exception ex)
            {
                OnPrimaryFailed(ex, key);
            }
        }

        Interlocked.Increment(ref _fallbackHitCount);
        return await _fallback.GetAsync(key, token);
    }

    // ------------------------------------------------------------------
    // 写
    // ------------------------------------------------------------------

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        if (ShouldTryPrimary())
        {
            try
            {
                _primary.Set(key, value, options);
                OnPrimarySucceeded();
            }
            catch (Exception ex)
            {
                OnPrimaryFailed(ex, key);
            }
        }

        // 内存副本始终写：主缓存恢复后，窗口内的读取仍能命中
        _fallback.Set(key, value, Shrink(options));
    }

    public async Task SetAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        if (ShouldTryPrimary())
        {
            try
            {
                await _primary.SetAsync(key, value, options, token);
                OnPrimarySucceeded();
            }
            catch (Exception ex)
            {
                OnPrimaryFailed(ex, key);
            }
        }

        await _fallback.SetAsync(key, value, Shrink(options), token);
    }

    // ------------------------------------------------------------------
    // 刷新与删除
    // ------------------------------------------------------------------

    public void Refresh(string key)
    {
        if (ShouldTryPrimary())
        {
            try
            {
                _primary.Refresh(key);
                OnPrimarySucceeded();
            }
            catch (Exception ex)
            {
                OnPrimaryFailed(ex, key);
            }
        }

        _fallback.Refresh(key);
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        if (ShouldTryPrimary())
        {
            try
            {
                await _primary.RefreshAsync(key, token);
                OnPrimarySucceeded();
            }
            catch (Exception ex)
            {
                OnPrimaryFailed(ex, key);
            }
        }

        await _fallback.RefreshAsync(key, token);
    }

    public void Remove(string key)
    {
        if (ShouldTryPrimary())
        {
            try
            {
                _primary.Remove(key);
                OnPrimarySucceeded();
            }
            catch (Exception ex)
            {
                OnPrimaryFailed(ex, key);
            }
        }

        _fallback.Remove(key);
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        if (ShouldTryPrimary())
        {
            try
            {
                await _primary.RemoveAsync(key, token);
                OnPrimarySucceeded();
            }
            catch (Exception ex)
            {
                OnPrimaryFailed(ex, key);
            }
        }

        await _fallback.RemoveAsync(key, token);
    }

    // ------------------------------------------------------------------
    // 短路窗口
    // ------------------------------------------------------------------

    private static DistributedCacheEntryOptions Shrink(DistributedCacheEntryOptions options)
    {
        // 只写内存时缩短有效期，防止主缓存长期不可用造成长期脏读
        return new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = FallbackWriteTtl,
            SlidingExpiration = null
        };
    }

    /// <summary>是否应该去碰主缓存：跳过窗口内一律不碰（这就是「不撞超时」的关键）。</summary>
    private bool ShouldTryPrimary()
    {
        var until = Interlocked.Read(ref _skipUntilTicks);

        return until == 0 || DateTimeOffset.UtcNow.Ticks >= until;
    }

    private void OnPrimarySucceeded()
    {
        // 从跳过窗口里恢复：清空截止时刻并允许下次再记日志
        if (Interlocked.Exchange(ref _skipUntilTicks, 0) != 0)
        {
            Interlocked.Exchange(ref _skipLogged, 0);

            _logger.LogInformation("分布式缓存主实现已恢复，重新启用。");
        }
    }

    private void OnPrimaryFailed(Exception ex, string key)
    {
        var count = Interlocked.Increment(ref _primaryFailureCount);

        Interlocked.Exchange(
            ref _skipUntilTicks,
            DateTimeOffset.UtcNow.Add(_skipDuration).Ticks);

        // 一个跳过窗口只记一条：Redis 长时间不可用时日志不能被刷爆
        if (Interlocked.Exchange(ref _skipLogged, 1) == 0)
        {
            _logger.LogWarning(
                ex,
                "分布式缓存主实现不可用，接下来 {SkipSeconds} 秒内直接使用内存缓存（累计失败 {Count} 次，key={Key}）。",
                _skipDuration.TotalSeconds,
                count,
                key);
        }
    }
}
