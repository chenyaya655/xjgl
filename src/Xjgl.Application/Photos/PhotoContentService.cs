using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Xjgl.Data;
using Xjgl.Students;

namespace Xjgl.Photos;

/// <inheritdoc />
public class PhotoContentService : IPhotoContentService, ITransientDependency
{
    private readonly IStudentAccessGuard _guard;
    private readonly IXjglRepository<StuPhoto> _photos;
    private readonly NasPhotoStorage _primary;
    private readonly LocalFallbackPhotoStorage _fallback;
    private readonly ILogger<PhotoContentService> _logger;

    public PhotoContentService(
        IStudentAccessGuard guard,
        IXjglRepository<StuPhoto> photos,
        NasPhotoStorage primary,
        LocalFallbackPhotoStorage fallback,
        ILogger<PhotoContentService> logger)
    {
        _guard = guard;
        _photos = photos;
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PhotoContentResult?> GetAsync(
        Guid studentId,
        bool thumbnail,
        CancellationToken cancellationToken = default)
    {
        // 先判权限：照片（尤其缩略图会被列表批量拉取）是最容易被越权爬取的接口
        await _guard.GetInScopeAsync(studentId, cancellationToken);

        var photo = await _photos.FindAsync(p => p.StudentId == studentId, cancellationToken);

        if (photo == null || photo.RelativePath.IsNullOrWhiteSpace())
        {
            return null;
        }

        var wantThumbnail = thumbnail && !photo.ThumbnailPath.IsNullOrWhiteSpace();
        var path = wantThumbnail ? photo.ThumbnailPath! : photo.RelativePath;

        var content = await ReadAsync(path, cancellationToken);

        if (content == null && wantThumbnail)
        {
            // 缩略图缺失（老数据、生成失败、降级区被清理）→ **回落原图**。
            // 列表页拿到大图总好过一片空白；前端会按显示尺寸缩放。
            content = await ReadAsync(photo.RelativePath, cancellationToken);
            wantThumbnail = false;
        }

        if (content == null)
        {
            _logger.LogWarning("照片文件在存储中找不到：{Path}", path);

            return null;
        }

        return new PhotoContentResult
        {
            Content = content,
            ContentType = GuessContentType(photo.RelativePath),
            ETag = photo.FileHash,
            IsThumbnail = wantThumbnail
        };
    }

    /// <summary>先主存储、再本地降级区（与写入顺序相反）。</summary>
    private async Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        foreach (var storage in new IPhotoBlobReader[] { _primary, _fallback })
        {
            try
            {
                await using var stream = await storage.OpenReadAsync(relativePath, cancellationToken);

                if (stream == null)
                {
                    continue;
                }

                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);

                return buffer.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取照片失败：{Path}", relativePath);
            }
        }

        return null;
    }

    private static string GuessContentType(string relativePath)
    {
        return Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };
    }
}
