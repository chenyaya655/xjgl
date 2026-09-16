namespace Xjgl.Photos;

/// <summary>
/// 照片在存储中的相对路径规则。
///
/// 布局：<c>photo/{学校组织Id:N}/{学生Id:N}{扩展名}</c>
///
/// 设计取舍：
///   - **按学校分目录**：单目录文件数过多会让 SMB/NFS 的列目录变得极慢，
///     一个县几百所学校、每所几千张，按学校分目录后单目录量级降到千，可接受。
///   - **文件名用 Id 而不是学籍号**：避免把学生身份信息泄露到 NAS 的目录名里，
///     运维直接看 NAS 也拿不到姓名/学籍号；同时 Id 天然唯一、不含生僻字，
///     不会踩到文件名编码的坑。
///   - **一名学生一个路径**：重新上传直接覆盖，天然幂等，
///     不会因为反复上传而在 NAS 上堆出多份历史照片。
/// </summary>
public static class PhotoPathRule
{
    /// <summary>照片根目录名。</summary>
    public const string RootDirectory = "photo";

    /// <summary>默认扩展名（学生证件照绝大多数是 jpg）。</summary>
    public const string DefaultExtension = ".jpg";

    /// <summary>允许上传的图片扩展名白名单。</summary>
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };

    /// <summary>构建照片相对路径。</summary>
    public static string Build(Guid schoolOrgId, Guid studentId, string? fileName = null)
    {
        return $"{RootDirectory}/{schoolOrgId:N}/{studentId:N}{NormalizeExtension(fileName)}";
    }

    /// <summary>构建缩略图相对路径。</summary>
    public static string BuildThumbnail(Guid schoolOrgId, Guid studentId)
    {
        return $"{RootDirectory}/{schoolOrgId:N}/thumb/{studentId:N}{DefaultExtension}";
    }

    /// <summary>取扩展名；不在白名单内一律回落为 .jpg。</summary>
    public static string NormalizeExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return DefaultExtension;
        }

        var extension = Path.GetExtension(fileName);

        if (string.IsNullOrWhiteSpace(extension))
        {
            return DefaultExtension;
        }

        // .jpeg 统一存成 .jpg，避免同一批照片出现两种后缀
        if (extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultExtension;
        }

        return AllowedExtensions.Contains(extension) ? extension.ToLowerInvariant() : DefaultExtension;
    }

    /// <summary>是否允许该扩展名（用于上传前置校验，给出明确报错而不是静默改成 .jpg）。</summary>
    public static bool IsAllowedExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        return !string.IsNullOrWhiteSpace(extension) && AllowedExtensions.Contains(extension);
    }
}
