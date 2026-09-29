namespace Xjgl.Changes;

/// <summary>
/// 异动附件的上传约定：大小上限、扩展名白名单、扩展名 → 内容类型。
///
/// 【为什么这份约定放在 Domain.Shared】
/// 三处都要用它，而且**必须是同一份**：
///   1) 应用服务的上传校验（能不能收）；
///   2) 存储实现拼物理文件名时的扩展名（存成什么后缀）；
///   3) 下载接口回给浏览器的 Content-Type（怎么发出去）。
/// 分散写三份的后果是"校验用一份白名单、存储用另一份"，表现为
/// "传得进去却下不下来"或者更糟——"白名单外的东西也能落盘"。
///
/// 白名单是**服务端**的策略：客户端传来的 Content-Type 只作参考，
/// 绝不作为放行依据（见 <see cref="GetContentType"/> 的说明）。
/// </summary>
public static class ChangeAttachmentConventions
{
    /// <summary>单个附件的字节上限：10 MB。</summary>
    public const long MaxFileSizeBytes = 10 * 1024 * 1024;

    /// <summary>附件在存储根目录下的子目录名。</summary>
    public const string RootDirectory = "change";

    /// <summary>
    /// 扩展名 → 内容类型。**这张表同时就是扩展名白名单**：
    /// 只要不在这里，一律不收（含可执行文件、压缩包、.html 等）。
    ///
    /// 【为什么内容类型只能由扩展名推导，不能采信客户端】
    /// 下载接口会把库里存的 ContentType 原样回给浏览器。
    /// 如果采信客户端声明，攻击者可以传一个 `.jpg` 的文件却声明
    /// `text/html`——浏览器按 HTML 渲染，就得到一个**同源**的脚本执行点
    /// （存储型 XSS）。扩展名是经过白名单校验的，用它推导出的类型才是可信的。
    /// </summary>
    private static readonly Dictionary<string, string> ContentTypeByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".bmp"] = "image/bmp",
            [".pdf"] = "application/pdf",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };

    /// <summary>允许上传的扩展名（展示与报错文案用，判定一律走 <see cref="IsAllowedExtension"/>）。</summary>
    public static IReadOnlyCollection<string> AllowedExtensions => ContentTypeByExtension.Keys;

    /// <summary>允许的扩展名，逗号分隔的报错文案（例如「.jpg / .png …」）。</summary>
    public static string AllowedExtensionsText =>
        string.Join(" / ", ContentTypeByExtension.Keys.OrderBy(e => e, StringComparer.Ordinal));

    /// <summary>PDF 的魔数：文件头必须是 <c>%PDF-</c>。</summary>
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    /// <summary>取规范化（小写）扩展名；没有扩展名时返回 <c>null</c>。</summary>
    public static string? GetExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // 只取扩展名（不看目录部分）：物理文件名由 Id 拼成，扩展名是唯一来自用户名的部分，
        // 因此这里取出来的东西绝不会带路径分隔符，拼进路径也就没有穿越风险。
        var extension = Path.GetExtension(fileName);

        return string.IsNullOrWhiteSpace(extension) ? null : extension.ToLowerInvariant();
    }

    /// <summary>扩展名是否在白名单内。</summary>
    public static bool IsAllowedExtension(string? fileName)
    {
        var extension = GetExtension(fileName);

        return extension != null && ContentTypeByExtension.ContainsKey(extension);
    }

    /// <summary>
    /// 由扩展名推导规范内容类型；扩展名不在白名单时返回 <c>null</c>。
    /// </summary>
    public static string? GetContentType(string? extension)
    {
        return extension != null && ContentTypeByExtension.TryGetValue(extension, out var contentType)
            ? contentType
            : null;
    }

    /// <summary>是否是需要「真的能解码」校验的图片格式（走 <c>IImageProcessor.ProbeAsync</c>）。</summary>
    public static bool IsImage(string? extension)
    {
        return extension is ".jpg" or ".jpeg" or ".png" or ".bmp";
    }

    /// <summary>是否是需要校验 <c>%PDF-</c> 魔数的 PDF。</summary>
    public static bool IsPdf(string? extension)
    {
        return extension == ".pdf";
    }

    /// <summary>
    /// 是否是 Office 二进制/OOXML 文档（.doc/.docx/.xls/.xlsx）。
    ///
    /// ⚠ **这些格式只做「扩展名 + 大小」校验，不做内容嗅探**。
    /// 逐格式解析（OLE 复合文档头、zip 内的 <c>[Content_Types].xml</c>、
    /// 加密文档、伪装成 xlsx 的 zip 炸弹…）是一整套独立的文件格式工程，
    /// 不在本功能的范围内。这里如实标注，而不是让人以为"已经校验过内容了"：
    /// 附件最终是给审核人下载人工查看的，不参与任何自动解析。
    /// </summary>
    public static bool IsOfficeDocument(string? extension)
    {
        return extension is ".doc" or ".docx" or ".xls" or ".xlsx";
    }

    /// <summary>内容是否以 <c>%PDF-</c> 开头。</summary>
    public static bool HasPdfMagic(ReadOnlySpan<byte> content)
    {
        return content.Length >= PdfMagic.Length && content[..PdfMagic.Length].SequenceEqual(PdfMagic);
    }
}
