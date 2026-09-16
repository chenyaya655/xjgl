using Microsoft.Extensions.Logging;

namespace Xjgl.Photos;

/// <summary>
/// 带熔断降级的照片存储装饰器 —— 照片上传链路的韧性核心。
///
/// 组合了四道保护（顺序即执行顺序）：
///   1) **熔断**：主存储（NAS）连续失败时直接拒绝，不再浪费一次超时；
///   2) **并发舱壁**：限制同时在途的写入数，避免大量慢请求把线程池拖垮；
///   3) **超时**：单次写入超过阈值即视为失败（NAS 卡死时不会无限等待）；
///   4) **降级**：任何失败都转写本地待补偿存储，接口仍然返回成功（标记为降级），
///      由后台补偿任务在 NAS 恢复后回放 —— 故障不暴露给教师。
///
/// 说明：本类刻意只做组合与状态判断，不做任何真实 IO，
/// 因此可以用假的存储实现在单元测试里穷尽验证
/// 「熔断打开 → 直接降级 → 半开试探 → 恢复」的全过程。
/// </summary>
public sealed class ResilientPhotoStorage : IPhotoStorage
{
    private readonly IPhotoStorage _primary;
    private readonly IPhotoStorage _fallback;
    private readonly PhotoCircuitBreaker _breaker;
    private readonly SemaphoreSlim _bulkhead;
    private readonly TimeSpan _timeout;
    private readonly ILogger? _logger;

    public ResilientPhotoStorage(
        IPhotoStorage primary,
        IPhotoStorage fallback,
        PhotoCircuitBreaker breaker,
        int bulkheadSize = 8,
        TimeSpan? timeout = null,
        ILogger? logger = null)
    {
        _primary = primary;
        _fallback = fallback;
        _breaker = breaker;
        _bulkhead = new SemaphoreSlim(Math.Max(1, bulkheadSize), Math.Max(1, bulkheadSize));
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        _logger = logger;
    }

    public string Name => $"韧性({_primary.Name} → {_fallback.Name})";

    /// <summary>熔断器当前快照，供监控与「照片存储健康度」接口使用。</summary>
    public PhotoCircuitSnapshot GetCircuitSnapshot() => _breaker.GetSnapshot();

    public async Task<PhotoWriteResult> WriteAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        // 1) 熔断打开 → 连试都不试，直接降级
        if (!_breaker.TryEnter())
        {
            _logger?.LogWarning("照片存储已熔断，直接写入本地降级区：{Path}", relativePath);

            return await WriteFallbackAsync(relativePath, content, "主存储熔断中", cancellationToken);
        }

        // 2) 并发舱壁
        await _bulkhead.WaitAsync(cancellationToken);

        try
        {
            // 3) 单次写入超时
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            var result = await _primary.WriteAsync(relativePath, Rewind(content), timeoutCts.Token);

            if (result.Success)
            {
                _breaker.RecordSuccess();
                return result;
            }

            _breaker.RecordFailure();
            _logger?.LogWarning("主存储写入失败（{Error}），转入本地降级：{Path}", result.Error, relativePath);

            return await WriteFallbackAsync(relativePath, content, result.Error ?? "主存储写入失败", cancellationToken);
        }
        catch (Exception ex)
        {
            _breaker.RecordFailure();
            _logger?.LogWarning(ex, "主存储写入异常，转入本地降级：{Path}", relativePath);

            return await WriteFallbackAsync(relativePath, content, ex.Message, cancellationToken);
        }
        finally
        {
            _bulkhead.Release();
        }
    }

    public async Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (await _primary.ExistsAsync(relativePath, cancellationToken))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "检查主存储失败，回落检查降级存储：{Path}", relativePath);
        }

        try
        {
            return await _fallback.ExistsAsync(relativePath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "检查降级存储失败：{Path}", relativePath);
            return false;
        }
    }

    public async Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        // 删除要两边都尝试：降级期的文件可能还在本地
        try
        {
            await _primary.DeleteAsync(relativePath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "删除主存储文件失败：{Path}", relativePath);
        }

        try
        {
            await _fallback.DeleteAsync(relativePath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "删除降级存储文件失败：{Path}", relativePath);
        }
    }

    private async Task<PhotoWriteResult> WriteFallbackAsync(
        string relativePath,
        Stream content,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _fallback.WriteAsync(relativePath, Rewind(content), cancellationToken);

            if (result.Success)
            {
                return PhotoWriteResult.DegradedTo(result.StoredPath ?? relativePath, reason);
            }

            return PhotoWriteResult.Failed($"主存储与降级存储均写入失败：{result.Error}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "降级存储写入失败：{Path}", relativePath);
            return PhotoWriteResult.Failed($"降级存储写入失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 主存储失败后流的读取位置可能已经前进，降级写入前需要复位。
    /// 非可寻道流无法复位，此时降级写入会拿到空内容 —— 因此上层必须保证传入可寻道流。
    /// </summary>
    private static Stream Rewind(Stream content)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        return content;
    }
}
