namespace Xjgl.Imaging;

/// <summary>图片的像素尺寸。</summary>
public sealed record PhotoImageSize(int Width, int Height);

/// <summary>
/// 图片处理（探测尺寸 / 生成缩略图）。
///
/// 只暴露应用层真正需要的两件事，而不是把图像库的能力整体透出来：
///   - 上传时要**校验"这确实是一张能解码的图片"**并记下原始尺寸；
///   - 列表页要的是缩略图，而不是把 NAS 上的原图（几百 KB 到几 MB）全部下载下来。
///
/// 与 PDF 渲染同样的分层做法：接口在这里（Domain.Shared），
/// 实现（ImageSharp）在 <c>Xjgl.Imaging</c>，由 Host 装配。
/// 这样业务层不依赖具体图像库，换库（或换成"上传时压缩"）只改一个项目。
/// </summary>
public interface IImageProcessor
{
    /// <summary>
    /// 探测尺寸。
    ///
    /// **不是可识别的图片时返回 null，而不是抛异常** ——
    /// 上传接口要能对"用户传了一个改了后缀的 txt"给出明确报错，
    /// 而不是让整个批次 500。
    /// </summary>
    Task<PhotoImageSize?> ProbeAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 生成缩略图（JPEG，长边不超过 <paramref name="maxEdge"/>）。
    ///
    /// 失败返回 null：缩略图是**锦上添花**，它生成不出来不该让"照片上传"这个主流程失败。
    /// </summary>
    Task<byte[]?> CreateThumbnailAsync(
        ReadOnlyMemory<byte> content,
        int maxEdge = ImageProcessingDefaults.ThumbnailMaxEdge,
        CancellationToken cancellationToken = default);
}

/// <summary>图像处理的默认参数。</summary>
public static class ImageProcessingDefaults
{
    /// <summary>缩略图长边上限（像素）。证件照在列表里显示约 60px，480 足够放大看清。</summary>
    public const int ThumbnailMaxEdge = 480;

    /// <summary>缩略图 JPEG 质量。82 在"看得清"与"体积小"之间比较平衡。</summary>
    public const int ThumbnailQuality = 82;

    /// <summary>允许上传的单张图片最大字节数（前端已压缩，这里是服务端兜底）。</summary>
    public const long MaxPhotoBytes = 5 * 1024 * 1024;
}
