using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Xjgl.Data;
using Xjgl.Imaging;

namespace Xjgl.Photos;

/// <inheritdoc />
public class PhotoThumbnailService : IPhotoThumbnailService, ITransientDependency
{
    private readonly IXjglRepository<StuPhoto> _photos;
    private readonly IPhotoStorage _storage;
    private readonly NasPhotoStorage _primary;
    private readonly LocalFallbackPhotoStorage _fallback;
    private readonly IImageProcessor _imageProcessor;
    private readonly ILogger<PhotoThumbnailService> _logger;

    public PhotoThumbnailService(
        IXjglRepository<StuPhoto> photos,
        IPhotoStorage storage,
        NasPhotoStorage primary,
        LocalFallbackPhotoStorage fallback,
        IImageProcessor imageProcessor,
        ILogger<PhotoThumbnailService> logger)
    {
        _photos = photos;
        _storage = storage;
        _primary = primary;
        _fallback = fallback;
        _imageProcessor = imageProcessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PhotoThumbnailResultDto> EnsureThumbnailsAsync(
        int maxCount = 200,
        Guid? schoolOrgId = null,
        CancellationToken cancellationToken = default)
    {
        var result = new PhotoThumbnailResultDto();

        if (maxCount <= 0)
        {
            maxCount = 200;
        }

        // 只挑"有原图、但台账里没有缩略图"的记录。
        // 状态限定在 Normal / FallbackPending：Pending 是还没落盘的中间态，
        // Failed 的原图可能根本不存在 —— 对它们生成缩略图只会每轮白跑一遍。
        //
        // schoolOrgId 有值时只处理该校（见接口注释：读不到原图的记录会永久占据队列，
        // 指定学校是"就修这一所"的入口，也是让测试确定的手段）。
        var pending = await _photos.GetPagedListAsync(
            p => p.ThumbnailPath == null
                 && p.RelativePath != null
                 && (p.Status == PhotoStatuses.Normal || p.Status == PhotoStatuses.FallbackPending)
                 && (schoolOrgId == null || p.SchoolOrgId == schoolOrgId),
            // ⚠ 必须是**新的优先**（DESC），不能是 UploadedTime ASC。
            //
            // 这条队列有一个会**永久堵住**的失效模式：原图读不到的记录（存储被清理、
            // 文件被手工删过）状态不变，于是**每一轮都会被重新扫到**。
            // 用 ASC（先来先处理）时，陈年的坏数据永远排在前面：
            // 本机实测有 415 条这种记录 > 单轮上限 200，结果是
            // "新上传的照片一直没有缩略图"，而后台任务每轮都在跑、日志里也不报错。
            // 改成 DESC 之后，刚上传的照片**第一轮就能拿到缩略图**，
            // 坏数据只是永远排在队尾（它们本来也永远生成不出来）。
            //
            // 列表页对没有缩略图的照片会回落到原图（接口已做回落），所以
            // "老照片暂时没有缩略图"是可接受的，"新照片没有"才是不可接受的。
            "UploadedTime DESC",
            0,
            maxCount,
            cancellationToken);

        result.Scanned = pending.Count;

        if (pending.Count == 0)
        {
            result.Message = "没有需要补齐缩略图的照片。";
            return result;
        }

        var touched = new List<StuPhoto>();

        foreach (var photo in pending)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await GenerateOneAsync(photo, result, touched, cancellationToken);
        }

        if (touched.Count > 0)
        {
            await _photos.UpdateManyAsync(touched, autoSave: true, cancellationToken);
        }

        result.Message =
            $"本轮扫描 {result.Scanned} 张：已补齐 {result.Generated} 张，" +
            $"内容无法解码 {result.Undecodable} 张，原图缺失 {result.MissingOriginal} 张。";

        _logger.LogInformation(
            "[缩略图补齐] 扫描 {Scanned}、补齐 {Generated}、不可解码 {Undecodable}、原图缺失 {Missing}。",
            result.Scanned, result.Generated, result.Undecodable, result.MissingOriginal);

        return result;
    }

    private async Task GenerateOneAsync(
        StuPhoto photo,
        PhotoThumbnailResultDto result,
        List<StuPhoto> touched,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await ReadOriginalAsync(photo.RelativePath, cancellationToken);

            if (content == null)
            {
                // 原图丢了是"缺照片"问题，不是缩略图问题 —— 不改状态，
                // 补偿任务与缺照片台账各自有自己的口径去处理它。
                result.MissingOriginal++;

                _logger.LogWarning("[缩略图补齐] 原图读不到，跳过：{Path}", photo.RelativePath);

                return;
            }

            var thumbnail = await _imageProcessor.CreateThumbnailAsync(content, cancellationToken: cancellationToken);

            if (thumbnail == null)
            {
                result.Undecodable++;

                // 老数据里可能有"改了后缀的文本"（内容校验落地之前上传的），
                // 它们永远生成不出缩略图。每轮都会重扫到它们，
                // 所以这条日志刻意是 Warning：数量长期不降就说明该清理这批数据了。
                _logger.LogWarning("[缩略图补齐] 内容不是可解码的图片：{Path}", photo.RelativePath);

                return;
            }

            var thumbnailPath = PhotoPathRule.BuildThumbnail(photo.SchoolOrgId, photo.StudentId);

            using var stream = new MemoryStream(thumbnail, writable: false);

            // 走容错的 IPhotoStorage：NAS 不可用时它会自动落本地降级区并标记 Degraded，
            // 由补偿任务在 NAS 恢复后连同原图一起回放（补偿里已处理缩略图）。
            // 因此这里**不需要**先探活 NAS —— 探活是"把降级区的东西搬回 NAS"才需要的判断。
            var write = await _storage.WriteAsync(thumbnailPath, stream, cancellationToken);

            if (!write.Success)
            {
                _logger.LogWarning("[缩略图补齐] 写入失败：{Path}（{Error}）", thumbnailPath, write.Error);

                return;
            }

            photo.ThumbnailPath = thumbnailPath;

            // 顺手补尺寸：老照片的 Width/Height 往往也是 0，而我们此刻已经解码过了，
            // 不补的话列表页拿到缩略图却仍不知道原图多大（导出清单里那两列会一直是 0）。
            if (photo.Width <= 0 || photo.Height <= 0)
            {
                var size = await _imageProcessor.ProbeAsync(content, cancellationToken);

                if (size != null)
                {
                    photo.Width = size.Width;
                    photo.Height = size.Height;
                }
            }

            touched.Add(photo);
            result.Generated++;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 单张失败绝不能让整轮中断：绝大多数情况是单张文件损坏。
            _logger.LogWarning(ex, "[缩略图补齐] 处理异常：{Path}", photo.RelativePath);
        }
    }

    /// <summary>先主存储、再本地降级区（与写入顺序相反）。</summary>
    private async Task<byte[]?> ReadOriginalAsync(string relativePath, CancellationToken cancellationToken)
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
                _logger.LogWarning(ex, "[缩略图补齐] 读取原图失败：{Path}", relativePath);
            }
        }

        return null;
    }
}
