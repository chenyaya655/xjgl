using Volo.Abp.Application.Dtos;

namespace Xjgl.Changes;

/// <summary>
/// 异动附件 DTO。
///
/// **刻意不含 <c>RelativePath</c>**：那是服务端的物理存储路径，
/// 前端拿它没用，泄露出去只会让"改个参数试试别的文件"变得有迹可循；
/// 下载一律走附件 Id。
/// </summary>
public class ChangeAttachmentDto : EntityDto<Guid>
{
    public Guid ApplyId { get; set; }

    /// <summary>上传时的原始文件名（另存为时用它，不是存储里的物理名）。</summary>
    public string FileName { get; set; } = default!;

    public string ContentType { get; set; } = default!;

    public long FileSize { get; set; }

    public Guid? UploadedByUserId { get; set; }

    public string? UploadedByName { get; set; }

    public DateTime UploadedAt { get; set; }

    public string? Remark { get; set; }
}

/// <summary>
/// 异动附件服务（流式上传 / 二进制下载）。
///
/// 为什么不继承 <c>IApplicationService</c>：ABP 会为应用服务自动生成 API 控制器，
/// 而本接口的参数/返回值含 <see cref="Stream"/>，自动生成会失败或不安全
/// （与 <c>IPhotoUploadService</c> 完全同一个理由）。
/// HTTP 入口是手写的 <c>ChangeAttachmentController</c>（multipart 上传 + 二进制下载）。
/// </summary>
public interface IChangeAttachmentAppService
{
    /// <summary>
    /// 上传附件。
    ///
    /// <paramref name="fileName"/> 是客户端传来的原始文件名：**只有扩展名会被使用**
    /// （且必须落在白名单内），其余部分仅作为展示名入库。
    /// <paramref name="contentType"/> 不参与任何判定（见 ChangeAttachmentConventions）。
    /// </summary>
    Task<ChangeAttachmentDto> UploadAsync(
        Guid applyId,
        string fileName,
        string? contentType,
        Stream content,
        long size);

    /// <summary>某张申请单的附件列表（按上传时间正序）。</summary>
    Task<List<ChangeAttachmentDto>> GetListAsync(Guid applyId);

    /// <summary>
    /// 下载附件：返回文件流 + 原始文件名 + 内容类型。
    /// 记录不存在时抛 <c>EntityNotFoundException</c>（HTTP 404）；
    /// 记录在而存储里的文件已丢失时抛 <c>Xjgl:00612</c>。
    /// </summary>
    Task<(Stream Content, string FileName, string ContentType)> DownloadAsync(Guid id);

    /// <summary>删除附件（记录与文件一起删）；记录不存在时抛 <c>EntityNotFoundException</c>。</summary>
    Task DeleteAsync(Guid id);
}
