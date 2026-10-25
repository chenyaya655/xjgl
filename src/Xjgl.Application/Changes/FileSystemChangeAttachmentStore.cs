using Xjgl.Storage;

namespace Xjgl.Changes;

/// <summary>
/// 基于文件系统的异动附件存储。
///
/// 布局：<c>change/{申请单Id:N}/{附件Id:N}{扩展名}</c>
///   - **按申请单分目录**：单目录文件数过多会让文件系统列目录变慢，
///     而一个县一年的异动材料按单据分组后单目录只有几条；
///   - **文件名用 Id 而不是原始文件名**：原始文件名里可能有生僻字、重名、
///     甚至 <c>../</c>，用它做文件名既要处理编码又要防穿越——用 Id 则
///     "扩展名"是唯一来自用户输入的部分，拼出来天然安全，
///     展示用的原始名存在数据库里（下载时作为另存为的名字）。
///
/// 写入采用「先写 .tmp 再原子改名」：上传接口与下载接口是并发的，
/// 直接写目标文件的话，下载方可能读到只写了一半的 PDF ——
/// 而半截 PDF 在浏览器里表现为"文件损坏"，没人会想到是并发写。
/// （与 <c>FileSystemExportArtifactStore</c> 同一做法、同一理由。）
///
/// ⚠ 附件存在**本机磁盘**上：多副本部署必须挂共享卷，见 <see cref="IChangeAttachmentStore"/>。
/// </summary>
public sealed class FileSystemChangeAttachmentStore : IChangeAttachmentStore
{
    private readonly string _rootPath;

    public FileSystemChangeAttachmentStore(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);

        // 这里的异常**不吞**（与照片存储的做法相反，也是刻意的）：
        // 附件目录是本机磁盘（或已挂载好的共享卷）上的普通目录，
        // 创建不出来说明部署就是错的（路径写错、卷没挂上、权限不足）。
        // 此时应当**立刻炸出来**，而不是等教师传完一学期的材料才发现全都丢了——
        // 照片能"先降级、后补偿"，附件没有那条后路（见 IChangeAttachmentStore）。
        Directory.CreateDirectory(_rootPath);
    }

    public string RootPath => _rootPath;

    public async Task<string> SaveAsync(
        Guid applyId,
        Guid attachmentId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var extension = ChangeAttachmentConventions.GetExtension(fileName) ?? string.Empty;

        var relativePath =
            $"{ChangeAttachmentConventions.RootDirectory}/{applyId:N}/{attachmentId:N}{extension}";

        var fullPath = ResolvePath(relativePath);

        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 先写临时文件再原子改名，避免下载方读到半截文件
        var tempPath = fullPath + ".tmp";

        await using (var fileStream = File.Create(tempPath))
        {
            await content.CopyToAsync(fileStream, cancellationToken);
        }

        File.Move(tempPath, fullPath, overwrite: true);

        return relativePath;
    }

    public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolvePath(relativePath);

        if (!File.Exists(fullPath))
        {
            return Task.FromResult<Stream?>(null);
        }

        // FileShare.Delete：删除申请单会在下载进行中删掉这个文件，
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

        // 附件按单据分目录，删掉最后一份后目录会空着。
        // 这里顺手清掉空目录（尽力而为）：不清的话共享卷上会慢慢堆出
        // 成千上万个空目录，运维清理时无法区分"空目录"与"正在写的目录"。
        TryRemoveEmptyDirectory(Path.GetDirectoryName(fullPath));

        return Task.CompletedTask;
    }

    private static void TryRemoveEmptyDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // 目录非空（并发又传了一份）或没有权限：都不该让"删除附件"失败
        }
    }

    /// <summary>
    /// 相对路径 → 绝对路径，并阻止路径穿越。
    ///
    /// 与 <c>FileSystemPhotoStorage</c> / <c>FileSystemExportArtifactStore</c>
    /// **共用同一份校验**（<see cref="SafePath.ResolveWithin"/>）：
    /// 路径虽然由服务端生成，但它**经过数据库**——运维改库、数据迁移
    /// 都可能让 <c>RelativePath</c> 变成 <c>../../appsettings.json</c>。
    /// 在校验上不能假设"库里的值一定是干净的"。
    /// </summary>
    private string ResolvePath(string relativePath) =>
        SafePath.ResolveWithin(_rootPath, relativePath, "附件路径");
}
