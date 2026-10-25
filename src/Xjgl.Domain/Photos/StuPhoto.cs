using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;
using Xjgl.Storage;

namespace Xjgl.Photos;

/// <summary>照片处理状态。</summary>
public static class PhotoStatuses
{
    /// <summary>已接收，待处理（已入队）。</summary>
    public const string Pending = "Pending";

    /// <summary>已正常写入 NAS。</summary>
    public const string Normal = "Normal";

    /// <summary>NAS 不可用，已降级写到本地，等待补偿回放。</summary>
    public const string FallbackPending = "FallbackPending";

    /// <summary>处理失败（主存储与降级存储都写不进去）。</summary>
    public const string Failed = "Failed";

    public static string GetDisplayName(string status) => status switch
    {
        Pending => "待处理",
        Normal => "正常",
        FallbackPending => "降级待补偿",
        Failed => "处理失败",
        _ => status
    };
}

/// <summary>
/// 学生照片档案。
///
/// 数据库只存**相对路径与元数据**，照片本体在 NAS（或降级期的本地盘）。
///
/// 表名：stu_photo
/// </summary>
public class StuPhoto : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    public Guid StudentId { get; set; }

    /// <summary>冗余学籍辅号，便于直接拼路径与展示。</summary>
    public string? StudentNo { get; set; }

    /// <summary>冗余学生姓名，列表展示用。</summary>
    public string? StudentName { get; set; }

    public Guid SchoolOrgId { get; set; }

    /// <summary>
    /// 学校的物化路径（冗余，便于按数据范围前缀过滤）。
    ///
    /// 与学生主表的 <c>SchoolPathCode</c> 同一设计：数据范围过滤必须是
    /// **一次前缀比较**，而不是"先查出一堆组织 Id 再 IN"。
    /// 照片是学生隐私数据，同样不能让人按任意学校 Id 查别人学校的照片。
    /// </summary>
    public string? SchoolPathCode { get; set; }

    /// <summary>相对路径，如 photo/4101020001/4101020001000123.jpg。</summary>
    public string RelativePath { get; set; } = default!;

    /// <summary>缩略图相对路径（尚未生成时为空）。</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>文件内容的 SHA256，用于秒传与去重。</summary>
    public string FileHash { get; set; } = default!;

    public long FileSize { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public string Status { get; set; } = PhotoStatuses.Pending;

    /// <summary>上传批次 Id，便于按批次查进度。</summary>
    public Guid BatchId { get; set; }

    public DateTime UploadedTime { get; set; }

    public Guid? UploadedByUserId { get; set; }

    public string? UploadedByName { get; set; }

    /// <summary>失败或降级原因。</summary>
    public string? ErrorMessage { get; set; }

    public StuPhoto()
    {
    }

    public StuPhoto(
        Guid id,
        Guid studentId,
        Guid schoolOrgId,
        string relativePath,
        string fileHash,
        Guid batchId) : base(id)
    {
        StudentId = studentId;
        SchoolOrgId = schoolOrgId;
        RelativePath = relativePath;
        FileHash = fileHash;
        BatchId = batchId;
    }
}

/// <summary>
/// 基于文件系统的照片存储实现。
///
/// 同一份代码通过不同的根目录参数分别充当「NAS 主存储」与「本地降级存储」：
///   - NAS 主存储：根目录是 SMB/NFS 挂载点（如 /mnt/nas/xjgl 或 Z:\xjgl）
///   - 本地降级存储：根目录是本机磁盘（如 D:\xjgl-photo-fallback）
///
/// 写入采用「先写临时文件再原子改名」，避免并发读到半截文件。
/// </summary>
public class FileSystemPhotoStorage : IPhotoStorage, IPhotoBlobReader
{
    private readonly string _rootPath;

    public FileSystemPhotoStorage(string name, string rootPath)
    {
        Name = name;
        _rootPath = Path.GetFullPath(rootPath);

        // 这里**故意吞掉异常**：NAS 挂载点在应用启动时未必已经就绪
        // （维护窗口、网络未通、容器启动顺序），此时如果让构造函数抛异常，
        // 整个应用就起不来了 —— 而照片存储恰恰是「可以暂时不可用」的那部分。
        // 目录不存在时，后续 WriteAsync 会失败，进而被熔断器识别并走本地降级。
        TryEnsureRoot();
    }

    public string Name { get; }

    public string RootPath => _rootPath;

    /// <summary>尽力确保根目录存在；失败时不抛，交由 WriteAsync 反馈为写入失败。</summary>
    private void TryEnsureRoot()
    {
        try
        {
            if (!Directory.Exists(_rootPath))
            {
                Directory.CreateDirectory(_rootPath);
            }
        }
        catch
        {
            // 忽略：NAS 尚未就绪属于预期情况
        }
    }

    public async Task<PhotoWriteResult> WriteAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = ResolvePath(relativePath);

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写临时文件，再原子改名，避免并发读取到半截文件
            var tempPath = fullPath + ".tmp";

            await using (var fileStream = File.Create(tempPath))
            {
                await content.CopyToAsync(fileStream, cancellationToken);
            }

            File.Move(tempPath, fullPath, overwrite: true);

            return PhotoWriteResult.Ok(relativePath);
        }
        catch (Exception ex)
        {
            return PhotoWriteResult.Failed($"{Name} 写入失败：{ex.Message}");
        }
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(ResolvePath(relativePath)));
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(relativePath);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    /// <summary>打开只读流；文件不存在返回 <c>null</c>。</summary>
    public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(relativePath);

        if (!File.Exists(fullPath))
        {
            return Task.FromResult<Stream?>(null);
        }

        // FileShare.Delete 是必须的：补偿回放会在读完之后立刻删掉这个降级副本，
        // 而 Windows 默认不允许删除仍被打开的文件（会抛“文件正被另一进程使用”）。
        return Task.FromResult<Stream?>(new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete));
    }

    /// <summary>
    /// 把相对路径解析为绝对路径，并阻止路径穿越。
    ///
    /// 校验本体在 <see cref="SafePath.ResolveWithin"/>（三处存储共用同一份实现）。
    /// 原先这里只做 `combined.StartsWith(_rootPath)` 前缀比较，而 `_rootPath` **不带
    /// 结尾分隔符** —— 于是 `../photos-evil/x.jpg` 这种"兄弟目录前缀"的写法能通过校验，
    /// 读写都"成功"，实际已经跑到根目录外面。详见 <see cref="SafePath"/> 的类注释。
    /// </summary>
    private string ResolvePath(string relativePath) =>
        SafePath.ResolveWithin(_rootPath, relativePath, "照片路径");
}

/// <summary>
/// NAS 主存储。根目录是 SMB/NFS 挂载点。
/// 单独成一个类型，是为了让依赖注入能够区分「主」与「降级」两个同构实现。
/// </summary>
public sealed class NasPhotoStorage : FileSystemPhotoStorage
{
    public NasPhotoStorage(string rootPath) : base("NAS 主存储", rootPath)
    {
    }
}

/// <summary>
/// 本地降级存储。NAS 熔断期间照片先落这里，等后台补偿任务回放。
/// </summary>
public sealed class LocalFallbackPhotoStorage : FileSystemPhotoStorage
{
    public LocalFallbackPhotoStorage(string rootPath) : base("本地降级存储", rootPath)
    {
    }
}
