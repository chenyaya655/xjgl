using Xjgl.Storage;

namespace Xjgl.Exporting;

/// <summary>产物写入结果。</summary>
/// <param name="RelativePath">相对路径（存库用）。</param>
/// <param name="Size">字节数。</param>
public sealed record ExportArtifactWriteResult(string RelativePath, long Size);

/// <summary>
/// 导出/打印产物的存储。
///
/// 与照片存储分开而不是复用 <c>IPhotoStorage</c>：
///   - 照片要熔断降级到本地盘（NAS 会卡），产物**不需要**降级 ——
///     它本来就是"生成一次、下载一次、过后删掉"的临时件，
///     给临时件套一层熔断器只会让"NAS 挂了导致导出失败"变成一个更难解释的现象；
///   - 照片按学生组织路径分目录并长期保留，产物按任务 Id 平铺并定期清理，生命周期完全不同。
///
/// 抽成接口是为了让 Host 的 worker 与 HttpApi 的下载接口共用同一套路径规则 ——
/// 两处各拼一次路径，迟早出现"写进去的路径读不出来"。
/// </summary>
public interface IExportArtifactStore
{
    /// <summary>存储根目录（绝对路径），仅用于日志与诊断。</summary>
    string RootPath { get; }

    /// <summary>
    /// 写入产物。
    /// <paramref name="taskId"/> 决定物理文件名，<paramref name="contentType"/> 决定扩展名
    /// —— 两者都来自服务端，不接受客户端输入。
    /// </summary>
    Task<ExportArtifactWriteResult> WriteAsync(
        Guid taskId,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>打开只读流；文件不存在返回 <c>null</c>。</summary>
    Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>删除产物；文件不存在视为成功（清理与取消都要求幂等）。</summary>
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// 基于文件系统的产物存储。
///
/// 写入采用「先写 .tmp 再原子改名」：下载接口与 worker 是并发的，
/// 直接写目标文件的话，下载方可能读到只写了一半的 zip ——
/// 而半截 zip 在浏览器里表现为"文件损坏"，没人会想到是并发写。
/// </summary>
public sealed class FileSystemExportArtifactStore : IExportArtifactStore
{
    /// <summary>
    /// 内容类型 → 扩展名。**白名单**：物理文件名只能由这张表推导，
    /// 不能用文件名里的扩展名（那是数据库里的展示值，可能被历史数据带歪）。
    /// </summary>
    private static readonly Dictionary<string, string> ExtensionByContentType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["text/csv"] = ".csv",
            ["application/pdf"] = ".pdf",
            ["application/zip"] = ".zip"
        };

    private readonly string _rootPath;

    public FileSystemExportArtifactStore(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);

        // 这里的异常**不吞**（与照片存储相反）：产物目录是本机磁盘上的普通目录，
        // 创建不出来说明部署就是错的（权限、路径写错），此时应当立刻炸出来，
        // 而不是等到用户点了导出才发现"任务全部失败"。
        Directory.CreateDirectory(_rootPath);
    }

    public string RootPath => _rootPath;

    public async Task<ExportArtifactWriteResult> WriteAsync(
        Guid taskId,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        if (!ExtensionByContentType.TryGetValue(contentType, out var extension))
        {
            throw new InvalidOperationException($"不支持的内容类型「{contentType}」，无法推断产物扩展名。");
        }

        var relativePath = $"{taskId:N}{extension}";
        var fullPath = ResolvePath(relativePath);

        // 先写临时文件再原子改名，避免下载方读到半截文件
        var tempPath = fullPath + ".tmp";

        await using (var fileStream = File.Create(tempPath))
        {
            await content.CopyToAsync(fileStream, cancellationToken);
        }

        File.Move(tempPath, fullPath, overwrite: true);

        return new ExportArtifactWriteResult(relativePath, new FileInfo(fullPath).Length);
    }

    public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(relativePath);

        if (!File.Exists(fullPath))
        {
            return Task.FromResult<Stream?>(null);
        }

        // FileShare.Delete：过期清理可能在下载进行中删掉这个文件，
        // Windows 默认不允许删除仍被打开的文件，会抛"文件正被另一进程使用"。
        return Task.FromResult<Stream?>(new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete));
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

    /// <summary>
    /// 相对路径 → 绝对路径，并阻止路径穿越。
    ///
    /// 与 <c>FileSystemPhotoStorage</c> / <c>FileSystemChangeAttachmentStore</c>
    /// **共用同一份校验**（<see cref="SafePath.ResolveWithin"/>）：
    /// 产物路径虽然由服务端生成，但它**经过数据库**——运维改库、数据迁移、SQL 注入
    /// 都可能让 <c>RelativeArtifactPath</c> 变成 <c>../../appsettings.json</c>。
    /// 在校验上不能假设"库里的值一定是干净的"。
    /// </summary>
    private string ResolvePath(string relativePath) =>
        SafePath.ResolveWithin(_rootPath, relativePath, "产物路径");
}
