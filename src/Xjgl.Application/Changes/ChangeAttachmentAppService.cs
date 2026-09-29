using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Guids;
using Volo.Abp.Timing;
using Volo.Abp.Users;
using Xjgl.Data;
using Xjgl.Imaging;

namespace Xjgl.Changes;

/// <summary>
/// 异动附件实现。
///
/// 三条约束决定了这里的写法：
///   1) **附件随申请单受数据范围约束**：每一个动作（上传 / 列表 / 下载 / 删除）
///      都要先回答"这张申请单你看得到吗"，判断统一走 <see cref="IChangeApplyAccessGuard"/>。
///      附件是独立路由，**不会**自动继承申请单接口的过滤条件。
///   2) **失败就是失败**：与照片不同，附件没有降级区、没有补偿回放
///      （理由见 <see cref="IChangeAttachmentStore"/>）。写不进去就直接报错，
///      绝不返回一个"看起来成功了"的结果。
///   3) **入库与落盘的顺序**：先落盘、再入库；入库失败就把刚落的盘删掉。
///      反过来（先入库再落盘）一旦落盘失败，库里就留下一条**指向不存在文件**的记录，
///      表现为"列表里有附件、点下载 404"——用户无法自行修复。
///      孤儿文件则只是运维清理项，代价小得多。
/// </summary>
public class ChangeAttachmentAppService : IChangeAttachmentAppService, ITransientDependency
{
    private readonly IXjglRepository<StuChangeAttachment> _attachments;
    private readonly IChangeApplyAccessGuard _guard;
    private readonly IChangeAttachmentStore _store;
    private readonly IImageProcessor _imageProcessor;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly ILogger<ChangeAttachmentAppService> _logger;

    public ChangeAttachmentAppService(
        IXjglRepository<StuChangeAttachment> attachments,
        IChangeApplyAccessGuard guard,
        IChangeAttachmentStore store,
        IImageProcessor imageProcessor,
        IGuidGenerator guidGenerator,
        ICurrentUser currentUser,
        IClock clock,
        ILogger<ChangeAttachmentAppService> logger)
    {
        _attachments = attachments;
        _guard = guard;
        _store = store;
        _imageProcessor = imageProcessor;
        _guidGenerator = guidGenerator;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ChangeAttachmentDto> UploadAsync(
        Guid applyId,
        string fileName,
        string? contentType,
        Stream content,
        long size)
    {
        // ---- 1) 数据范围：跨组织传不了 ----
        var apply = await _guard.GetInScopeAsync(applyId);

        // ---- 2) 扩展名白名单 ----
        // 先判扩展名再读字节：白名单外的东西根本不必进内存。
        if (!ChangeAttachmentConventions.IsAllowedExtension(fileName))
        {
            throw new BusinessException(XjglDomainErrorCodes.ChangeAttachmentFormatNotSupported)
                .WithData("reason",
                    $"不支持的附件格式「{Path.GetExtension(fileName)}」，" +
                    $"仅支持 {ChangeAttachmentConventions.AllowedExtensionsText}。");
        }

        var extension = ChangeAttachmentConventions.GetExtension(fileName)!;

        // ---- 3) 大小：先用声明值挡一次，省掉把 100MB 读进内存 ----
        EnsureSizeWithinLimit(size);

        // ---- 4) 读字节 ----
        // 上限只有 10MB，一次读进内存是安全的（而且图片探测与 PDF 魔数校验都要看字节）。
        // 与照片上传刻意不同：那条路径要一次收几百张，所以是"边写边释放"，
        // 附件一次只有一份材料，简单直接更重要。
        var bytes = await ReadAllBytesAsync(content);

        if (bytes.Length == 0)
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", "上传的附件是空文件，请重新选择。");
        }

        // IFormFile.Length 在个别客户端上可能与实际字节数不一致，以实读为准
        EnsureSizeWithinLimit(bytes.Length);

        // ---- 5) 内容校验（按格式分档，见下） ----
        await EnsureContentMatchesExtensionAsync(extension, bytes);

        // ---- 6) 落盘 ----
        var attachmentId = _guidGenerator.Create();

        var relativePath = await _store.SaveAsync(
            apply.Id,
            attachmentId,
            fileName,
            new MemoryStream(bytes, writable: false));

        // ---- 7) 入库（失败则回收刚写的文件） ----
        var entity = new StuChangeAttachment(
            attachmentId,
            apply.Id,
            // 只保留文件名本身，去掉客户端可能带来的路径部分
            Path.GetFileName(fileName),
            // 内容类型由扩展名白名单推导，**不用 contentType 参数**：
            // 客户端声明的内容类型一旦被采信并原样回给浏览器，下载就可能变成
            // 同源 HTML 注入（详见 ChangeAttachmentConventions）。
            ChangeAttachmentConventions.GetContentType(extension)!,
            bytes.Length,
            relativePath)
        {
            UploadedAt = _clock.Now,
            UploadedByUserId = _currentUser.Id,
            UploadedByName = _currentUser.UserName
        };

        try
        {
            await _attachments.InsertAsync(entity, autoSave: true);
        }
        catch
        {
            await TryDeleteFileAsync(relativePath);
            throw;
        }

        _logger.LogInformation(
            "[异动附件] 申请单 {ApplyNo} 上传了附件 {FileName}（{Size} 字节）。",
            apply.ApplyNo, entity.FileName, entity.FileSize);

        return ToDto(entity);
    }

    public async Task<List<ChangeAttachmentDto>> GetListAsync(Guid applyId)
    {
        // 读权限：申请查询 **或** 审核查询，任一即可（理由见
        // IChangeApplyAccessGuard.EnsureReadPermissionAsync —— 审核员手上可能只有
        // Chg.Audit.*，而他要看材料才能批）。
        await _guard.EnsureReadPermissionAsync();

        // 列表同样要过范围：否则"申请单读不到、附件列表却读得到"
        await _guard.GetInScopeAsync(applyId);

        var attachments = await _attachments.GetSortedListAsync(
            a => a.ApplyId == applyId, "UploadedAt ASC, CreationTime ASC");

        return attachments.Select(ToDto).ToList();
    }

    public async Task<(Stream Content, string FileName, string ContentType)> DownloadAsync(Guid id)
    {
        // 记录不存在 → EntityNotFoundException（ABP 会映成 404）。
        // 这与仓库里"按 Id 取不到就抛 EntityNotFoundException"的惯例一致，
        // 也让下载接口的语义正确：不存在的附件是 404，不是 403。
        var attachment = await _attachments.FindAsync(id)
            ?? throw new EntityNotFoundException(typeof(StuChangeAttachment), id);

        // 读权限：与列表同一条（申请查询或审核查询）
        await _guard.EnsureReadPermissionAsync();

        // 按 Id 下载也必须回查父申请单的范围：附件 Id 是可枚举的，
        // 只靠"知道 Id"就放行等于没有数据范围。
        await _guard.GetInScopeAsync(attachment.ApplyId);

        // 记录在、文件没了：这是**存储层**的问题（共享卷没挂上 / 文件被人工清理），
        // 与"附件不存在"是两件事，因此用单独的码 + 能照着排查的原因。
        var content = await _store.OpenReadAsync(attachment.RelativePath)
            ?? throw new BusinessException(XjglDomainErrorCodes.ChangeAttachmentNotFound)
                .WithData("reason",
                    "附件记录存在，但存储中的文件已丢失（可能未挂载共享卷，或文件被人工清理）");

        return (content, attachment.FileName, attachment.ContentType);
    }

    public async Task DeleteAsync(Guid id)
    {
        var attachment = await _attachments.FindAsync(id)
            ?? throw new EntityNotFoundException(typeof(StuChangeAttachment), id);

        var apply = await _guard.GetInScopeAsync(attachment.ApplyId);

        // 先删文件、再软删记录。
        //
        // 【文件删不掉时为什么仍然继续删记录】
        // 记录才是用户看得见的东西（"我要把这份材料撤下来"）。
        // 若因为一次 IO 失败就中止，用户会看到"删不掉"却没有任何别的入口可重试；
        // 而留下的是一条**指向已无意义文件**的记录，比一个孤儿文件更难解释。
        // 因此文件删除失败只记警告（带路径，运维可清理），记录照删。
        await TryDeleteFileAsync(attachment.RelativePath);

        await _attachments.DeleteAsync(attachment, autoSave: true);

        _logger.LogInformation(
            "[异动附件] 申请单 {ApplyNo} 删除了附件 {FileName}。",
            apply.ApplyNo, attachment.FileName);
    }

    // ------------------------------------------------------------------
    // 内部实现
    // ------------------------------------------------------------------

    private static void EnsureSizeWithinLimit(long size)
    {
        if (size <= ChangeAttachmentConventions.MaxFileSizeBytes)
        {
            return;
        }

        throw new BusinessException(XjglDomainErrorCodes.ChangeAttachmentTooLarge)
            .WithData("reason",
                $"附件大小 {FormatSize(size)} 超过上限 " +
                $"{FormatSize(ChangeAttachmentConventions.MaxFileSizeBytes)}，请压缩后重新上传。");
    }

    /// <summary>
    /// 按格式分档校验内容 —— **不做"看起来校验过了"的假动作**：
    ///
    ///   图片（jpg/jpeg/png/bmp）：复用照片那条探测（<see cref="IImageProcessor.ProbeAsync"/>），
    ///       必须真的能解码。这一档是硬的：照片模块早就踩过"改了后缀的 txt 也能存进去"，
    ///       结果是列表白图、打印空白，而查不出哪里错了（债 20）。
    ///   PDF：校验文件头 `%PDF-`。这只是魔数，不是完整解析——但足以挡住
    ///       "把别的文件改名成 .pdf"这种最常见的误传。
    ///   Office（doc/docx/xls/xlsx）：**只做扩展名 + 大小校验，不做内容嗅探**。
    ///       逐格式解析（OLE 复合文档、OOXML 的 zip 结构、加密文档、zip 炸弹…）
    ///       是一整套独立的工程，不在本功能范围内。这些附件只供人工下载查看，
    ///       不参与任何自动解析，因此风险面可控——这里如实标注，而不是
    ///       让人以为"内容已经校验过了"。
    /// </summary>
    private async Task EnsureContentMatchesExtensionAsync(string extension, byte[] bytes)
    {
        if (ChangeAttachmentConventions.IsImage(extension))
        {
            var size = await _imageProcessor.ProbeAsync(bytes);

            if (size == null)
            {
                throw new BusinessException(XjglDomainErrorCodes.ChangeAttachmentFormatNotSupported)
                    .WithData("reason",
                        "文件内容不是可识别的图片（常见原因：把 txt / pdf / 压缩包等改了扩展名）。" +
                        "请上传真正的 jpg / jpeg / png / bmp 文件。");
            }

            return;
        }

        if (ChangeAttachmentConventions.IsPdf(extension)
            && !ChangeAttachmentConventions.HasPdfMagic(bytes))
        {
            throw new BusinessException(XjglDomainErrorCodes.ChangeAttachmentFormatNotSupported)
                .WithData("reason", "文件内容不是 PDF（缺少 %PDF- 文件头），请确认文件没有损坏或被改过扩展名。");
        }
    }

    /// <summary>尽力删除存储中的文件；失败只告警，不抛（调用方仍需继续自己的流程）。</summary>
    private async Task TryDeleteFileAsync(string relativePath)
    {
        try
        {
            await _store.DeleteAsync(relativePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "[异动附件] 删除存储文件失败，已留下孤儿文件：{RelativePath}", relativePath);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        if (stream is MemoryStream memoryStream && memoryStream.TryGetBuffer(out var buffer))
        {
            return buffer.AsSpan(0, (int)memoryStream.Length).ToArray();
        }

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static string FormatSize(long bytes)
    {
        return bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.##} MB"
            : $"{bytes / 1024d:0.##} KB";
    }

    private static ChangeAttachmentDto ToDto(StuChangeAttachment a) => new()
    {
        Id = a.Id,
        ApplyId = a.ApplyId,
        FileName = a.FileName,
        ContentType = a.ContentType,
        FileSize = a.FileSize,
        UploadedByUserId = a.UploadedByUserId,
        UploadedByName = a.UploadedByName,
        UploadedAt = a.UploadedAt,
        Remark = a.Remark
    };
}
