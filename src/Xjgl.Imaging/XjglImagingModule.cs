using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Modularity;
using Xjgl.Imaging;

namespace Xjgl.Imaging;

/// <summary>
/// 图像处理模块（ImageSharp 实现）。
///
/// 与 PDF 渲染同样的分层：业务层只依赖 <see cref="IImageProcessor"/>，
/// 实现由 Host 在这里装配。
/// </summary>
[DependsOn(typeof(XjglDomainSharedModule))]
public class XjglImagingModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddTransient<IImageProcessor, ImageSharpProcessor>();
    }
}

/// <summary>
/// 基于 ImageSharp 的图像处理实现。
///
/// 两个实现细节值得记住：
///   1. **AutoOrient**：手机拍的照片往往带 EXIF 方向标记（像素其实是横着的），
///      不按标记旋转就生成缩略图，人像会歪 90°。H5 端用摄像头拍照上传，
///      这个坑几乎必然出现。
///   2. **不放大**：原图本来就小于缩略图上限时保持原尺寸，
///      强行放大只会让文件变大且更模糊。
/// </summary>
public class ImageSharpProcessor : IImageProcessor, ITransientDependency
{
    /// <inheritdoc />
    public async Task<PhotoImageSize?> ProbeAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        if (content.IsEmpty)
        {
            return null;
        }

        try
        {
            await using var stream = new MemoryStream(content.ToArray(), writable: false);

            // Identify 只读文件头，不解码整张图 —— 对几 MB 的照片也不会有明显开销
            var info = await Image.IdentifyAsync(stream, cancellationToken);

            return info == null ? null : new PhotoImageSize(info.Width, info.Height);
        }
        catch (Exception)
        {
            // 不是能识别的图片（改了后缀的文本、损坏的文件…）
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> CreateThumbnailAsync(
        ReadOnlyMemory<byte> content,
        int maxEdge = ImageProcessingDefaults.ThumbnailMaxEdge,
        CancellationToken cancellationToken = default)
    {
        if (content.IsEmpty || maxEdge <= 0)
        {
            return null;
        }

        try
        {
            await using var input = new MemoryStream(content.ToArray(), writable: false);

            using var image = await Image.LoadAsync(input, cancellationToken);

            image.Mutate(x =>
            {
                // 先按 EXIF 摆正，再缩放：顺序反了会得到"歪着的缩略图"
                x.AutoOrient();

                if (image.Width > maxEdge || image.Height > maxEdge)
                {
                    x.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,               // 保持长宽比，长边压到 maxEdge
                        Size = new Size(maxEdge, maxEdge),
                        Sampler = KnownResamplers.Lanczos3    // 人像缩放用 Lanczos，边缘更干净
                    });
                }
            });

            await using var output = new MemoryStream();

            await image.SaveAsJpegAsync(
                output,
                new JpegEncoder { Quality = ImageProcessingDefaults.ThumbnailQuality },
                cancellationToken);

            return output.ToArray();
        }
        catch (Exception)
        {
            // 缩略图生成失败不影响照片上传本身
            return null;
        }
    }
}
