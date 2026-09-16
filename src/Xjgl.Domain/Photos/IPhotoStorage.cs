namespace Xjgl.Photos;

/// <summary>照片存储的写入结果。</summary>
public sealed class PhotoWriteResult
{
    /// <summary>是否写入成功（含降级成功）。</summary>
    public bool Success { get; init; }

    /// <summary>是否走了降级路径（主存储不可用，写到了本地待补偿）。</summary>
    public bool Degraded { get; init; }

    /// <summary>实际存储的相对路径。</summary>
    public string? StoredPath { get; init; }

    /// <summary>失败或降级的原因。</summary>
    public string? Error { get; init; }

    public static PhotoWriteResult Ok(string path) => new()
    {
        Success = true, StoredPath = path
    };

    public static PhotoWriteResult DegradedTo(string path, string reason) => new()
    {
        Success = true, Degraded = true, StoredPath = path, Error = reason
    };

    public static PhotoWriteResult Failed(string error) => new()
    {
        Success = false, Error = error
    };
}

/// <summary>
/// 照片存储抽象。
///
/// 实现：
///   - NAS 主存储（SMB/NFS 挂载成目录）
///   - 本地降级存储（NAS 熔断时暂存，后台补偿任务回放）
///   - 由 <see cref="ResilientPhotoStorage"/> 把两者组合成带熔断的韧性链路
/// </summary>
public interface IPhotoStorage
{
    /// <summary>存储名称（用于日志与状态展示）。</summary>
    string Name { get; }

    Task<PhotoWriteResult> WriteAsync(string relativePath, Stream content, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default);

    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
}
