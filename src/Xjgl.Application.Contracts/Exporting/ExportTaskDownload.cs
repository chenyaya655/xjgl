namespace Xjgl.Exporting;

/// <summary>下载请求的判定结果。</summary>
public enum ExportTaskDownloadStatus
{
    /// <summary>可以下载。</summary>
    Ready,

    /// <summary>不存在、或不属于当前用户 —— 两者刻意合并，不区分。</summary>
    NotFound,

    /// <summary>任务还没成功（排队中/执行中/失败/已取消/已过期）。</summary>
    NotReady
}

/// <summary>下载结果。</summary>
/// <param name="Status">判定结果。</param>
/// <param name="Content">产物流（仅 <see cref="ExportTaskDownloadStatus.Ready"/> 时非空）。</param>
/// <param name="FileName">下载时呈现的文件名。</param>
/// <param name="ContentType">内容类型。</param>
/// <param name="Message">不可下载时给用户看的原因。</param>
public sealed record ExportTaskDownloadResult(
    ExportTaskDownloadStatus Status,
    Stream? Content,
    string? FileName,
    string? ContentType,
    string? Message);

/// <summary>
/// 产物下载。
///
/// 接口放在 Contracts 而实现放在 Application：`Xjgl.HttpApi` 只引用 Contracts
/// （见它的 .csproj），所以控制器能看到的只有这里。
/// 这与 <c>IPhotoContentService</c> 是同一套做法。
///
/// 它**不实现 IApplicationService**：产物是二进制流，走自动 API 会被
/// 序列化成 base64 塞进 JSON，几 MB 的 PDF 变成十几 MB 的字符串，
/// 浏览器也拿不到"另存为"的文件名。
/// </summary>
public interface IExportTaskDownloadService
{
    /// <summary>
    /// 取产物供下载。
    ///
    /// **只有发起人本人能拿到**；不是自己的任务一律按"不存在"处理 ——
    /// 返回 403 等于确认了"这个任务存在"，而任务 Id 会出现在日志、
    /// 聊天记录与工单截图里。
    /// </summary>
    Task<ExportTaskDownloadResult> GetAsync(Guid id, CancellationToken cancellationToken = default);
}
