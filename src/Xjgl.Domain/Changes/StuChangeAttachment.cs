using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Changes;

/// <summary>
/// 异动申请单的证明材料附件。
///
/// 为什么单独一张表而不是塞进申请单：一张申请单可以有多份材料
/// （转学要户口簿 + 房产证 + 接收函），而"必须上传附件"这个开关只关心
/// **有没有**（见 ChangeFlowAppService 的批准前校验）。
///
/// 与照片（<see cref="Photos.StuPhoto"/>）刻意不复用同一条存储链路：
/// 照片有"NAS 熔断 → 本地降级 → 后台补偿回放"的韧性机制，而那条补偿任务
/// **只扫描照片表**。附件若走同一条路，NAS 故障期间写入的附件会落在降级区
/// 且永远不会被回放——那是"以为存住了、其实没有"的静默丢失。
/// 详见 <see cref="IChangeAttachmentStore"/> 的说明。
///
/// 表名：stu_change_attachment
/// </summary>
public class StuChangeAttachment : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>所属异动申请单。</summary>
    public Guid ApplyId { get; set; }

    /// <summary>上传时的原始文件名（含扩展名），仅供展示与另存为。</summary>
    public string FileName { get; set; } = default!;

    /// <summary>
    /// 内容类型。**由扩展名白名单推导，不采信客户端声明**
    /// （理由见 <see cref="ChangeAttachmentConventions"/>）。
    /// </summary>
    public string ContentType { get; set; } = default!;

    /// <summary>字节数。列表页展示用，下载时不必再取一次文件元的长度。</summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 存储相对路径，如 <c>change/{applyId:N}/{attachmentId:N}.pdf</c>。
    ///
    /// 与照片同一取舍：库里只存相对路径，换根目录（本机盘换共享卷）不必改数据。
    /// </summary>
    public string RelativePath { get; set; } = default!;

    public Guid? UploadedByUserId { get; set; }

    public string? UploadedByName { get; set; }

    public DateTime UploadedAt { get; set; }

    public string? Remark { get; set; }

    public StuChangeAttachment()
    {
    }

    public StuChangeAttachment(
        Guid id,
        Guid applyId,
        string fileName,
        string contentType,
        long fileSize,
        string relativePath) : base(id)
    {
        ApplyId = applyId;
        FileName = fileName;
        ContentType = contentType;
        FileSize = fileSize;
        RelativePath = relativePath;
    }
}
