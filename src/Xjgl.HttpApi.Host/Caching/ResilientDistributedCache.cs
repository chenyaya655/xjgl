using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Xjgl.Caching;

/// <summary>
/// 容错的分布式缓存装饰器。
///
/// 对应方案文档「熔断降级矩阵」中的两条：
///   - Redis 不可用 → 降级为本地内存缓存，不阻塞业务；
///   - 主缓存恢复后自动回切（不做长时间熔断，避免雪崩后长时间不命中）。
///
/// 为什么必须自己实现：ABP 的 <c>AbpDistributedCacheOptions.HideErrors</c> 只对
/// ABP 自己包装的 <c>IDistributedCache&lt;T&gt;</c> 生效；而 ABP 的 SettingManagement、
/// PermissionManagement、FeatureManagement 的内部初始化器直接使用原生
/// <c>IDistributedCache</c>，Redis 抛异常会直接冒泡成 500。
///
/// 降级语义：
///   - 读：主缓存异常 → 回落内存缓存（数据可能不是最新，但比报错好）；
///   - 写：主缓存异常 → 只写内存（内存条目带较短过期时间，避免长期脏读），
///         并通过日志与计数器暴露，便于监控告警。
/// </summary>
public sealed class ResilientDistributedCache : IDistributedCache
{
    private static long _primaryFailureCount;
    private static long _fallbackHitCount;

    private readonly IDistributedCache _primary;
    private readonly IDistributedCache _fallback;
    private readonly ILogger<ResilientDistributedCache> _logger;

    /// <summary>降级写入内存缓存时的最长存活时间，避免主缓存长时间不可用导致长期脏读。</summary>
    private static readonly TimeSpan FallbackWriteTtl = TimeSpan.FromSeconds(30);

    public ResilientDistributedCache(
        IDistributedCache primary,
        IDistributedCache fallback,
        ILogger<ResilientDistributedCache> logger)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    /// <summary>主缓存累计失败次数（监控用）。</summary>
    public static long PrimaryFailureCount => Interlocked.Read(ref _primaryFailureCount);

    /// <summary>降级命中累计次数（监控用）。</summary>
    public static long FallbackHitCount => Interlocked.Read(ref _fallbackHitCount);

    public byte[]? Get(string key)
    {
        try
        {
            return _primary.Get(key);
        }
        catch (Exception ex)
        {
            OnPrimaryFailed(ex, key);
            Interlocked.Increment(ref _fallbackHitCount);
            return _fallback.Get(key);
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        try
        {
            return await _primary.GetAsync(key, token);
        }
        catch (Exception ex)
        {
            OnPrimaryFailed(ex, key);
            Interlocked.Increment(ref _fallbackHitCount);
            return await _fallback.GetAsync(key, token);
        }
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        SafePrimary(() => _primary.Set(key, value, options), key);
        _fallback.Set(key, value, Shrink(options));
    }

    public async Task SetAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        await SafePrimaryAsync(() => _primary.SetAsync(key, value, options, token), key);
        await _fallback.SetAsync(key, value, Shrink(options), token);
    }

    public void Refresh(string key)
    {
        SafePrimary(() => _primary.Refresh(key), key);
        _fallback.Refresh(key);
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        await SafePrimaryAsync(() => _primary.RefreshAsync(key, token), key);
        await _fallback.RefreshAsync(key, token);
    }

    public void Remove(string key)
    {
        SafePrimary(() => _primary.Remove(key), key);
        _fallback.Remove(key);
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        await SafePrimaryAsync(() => _primary.RemoveAsync(key, token), key);
        await _fallback.RemoveAsync(key, token);
    }

    private static DistributedCacheEntryOptions Shrink(DistributedCacheEntryOptions options)
    {
        // 只写内存时缩短有效期，防止主缓存长期不可用造成长期脏读
        return new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = FallbackWriteTtl,
            SlidingExpiration = null
        };
    }

    private void SafePrimary(Action action, string key)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            OnPrimaryFailed(ex, key);
        }
    }

    private async Task SafePrimaryAsync(Func<Task> action, string key)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            OnPrimaryFailed(ex, key);
        }
    }

    private void OnPrimaryFailed(Exception ex, string key)
    {
        var count = Interlocked.Increment(ref _primaryFailureCount);

        // 只在前若干次与每 100 次记录一次，避免 Redis 长时间不可用时刷爆日志
        if (count <= 5 || count % 100 == 0)
        {
            _logger.LogWarning(
                ex,
                "分布式缓存主实现不可用，已降级为内存缓存（累计失败 {Count} 次，key={Key}）。",
                count,
                key);
        }
    }
}
