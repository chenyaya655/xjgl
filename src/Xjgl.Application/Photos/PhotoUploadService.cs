using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Guids;
using Volo.Abp.Timing;
using Volo.Abp.Users;
using Xjgl.Data;
using Xjgl.Imaging;
using Xjgl.Photos.Dtos;
using Xjgl.Students;

namespace Xjgl.Photos;

/// <summary>
/// 照片上传实现 —— 照片管理的吞吐核心。
///
/// 三条硬约束决定了这里的写法：
///   1) **NAS 会卡**：所有写入走 <see cref="IPhotoStorage"/>（带熔断 + 舱壁 + 超时 + 降级），
///      写不进 NAS 就落本地待补偿，**绝不把故障抛给正在传照片的教师**。
///   2) **SqlSugar 客户端不是线程安全的**：所以把流程拆成
///      「先并发写存储（慢，纯 IO）」+「再串行落库（快）」两段。
///   3) **一次几百张会撑爆内存**：单批上限 <see cref="MaxBatchSize"/>，
///      且并发度受 <see cref="StorageConcurrency"/> 限制，
///      照片字节在写完后立刻释放。
/// </summary>
public class PhotoUploadService : IPhotoUploadService, ITransientDependency
{
    /// <summary>单批最大张数。超过请前端分批。</summary>
    public const int MaxBatchSize = PhotoLimits.MaxBatchSize;

    /// <summary>同时在途的存储写入数。NAS 是共享设备，压太狠反而整体变慢。</summary>
    public const int StorageConcurrency = PhotoLimits.StorageConcurrency;

    private readonly IXjglRepository<StuStudent> _students;
    private readonly IXjglRepository<StuPhoto> _photos;
    private readonly IPhotoStorage _storage;
    private readonly IImageProcessor _imageProcessor;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly ILogger<PhotoUploadService> _logger;

    public PhotoUploadService(
        IXjglRepository<StuStudent> students,
        IXjglRepository<StuPhoto> photos,
        IPhotoStorage storage,
        IImageProcessor imageProcessor,
        IGuidGenerator guidGenerator,
        ICurrentUser currentUser,
        IClock clock,
        ILogger<PhotoUploadService> logger)
    {
        _students = students;
        _photos = photos;
        _storage = storage;
        _imageProcessor = imageProcessor;
        _guidGenerator = guidGenerator;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;
    }

    public async Task<PhotoUploadItemResultDto> UploadOneAsync(
        PhotoUploadRequest request,
        Guid? batchId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await UploadBatchAsync(
            batchId ?? _guidGenerator.Create(),
            new[] { request },
            cancellationToken);

        return result.Items[0];
    }

    public async Task<PhotoUploadResultDto> UploadBatchAsync(
        Guid batchId,
        IReadOnlyList<PhotoUploadRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (batchId == Guid.Empty)
        {
            batchId = _guidGenerator.Create();
        }

        var result = new PhotoUploadResultDto
        {
            BatchId = batchId,
            Total = requests.Count
        };

        if (requests.Count == 0)
        {
            result.Message = "没有需要上传的照片。";
            return result;
        }

        if (requests.Count > MaxBatchSize)
        {
            throw new UserFriendlyException(
                $"单次最多上传 {MaxBatchSize} 张照片，本次 {requests.Count} 张，请分批上传。");
        }

        // ---- 阶段 0：一次性把学生与已有照片读出来，避免逐张查库 ----
        var studentIds = requests.Select(r => r.StudentId).Distinct().ToList();

        var students = (await _students.GetListAsync(s => studentIds.Contains(s.Id), cancellationToken))
            .ToDictionary(s => s.Id);

        var existingPhotos = (await _photos.GetListAsync(p => studentIds.Contains(p.StudentId), cancellationToken))
            .ToDictionary(p => p.StudentId);

        // ---- 阶段 1：校验 + 读字节 + 算哈希（此时还不碰存储与数据库）----
        var items = new List<PreparedItem>(requests.Count);

        foreach (var request in requests)
        {
            items.Add(await PrepareAsync(request, students, existingPhotos, result, cancellationToken));
        }

        // ---- 阶段 2：并发写存储（慢，纯 IO，不碰数据库）----
        await WriteStorageInParallelAsync(items, cancellationToken);

        // ---- 阶段 3：串行落库（SqlSugar 客户端非线程安全）----
        await PersistAsync(items, batchId, result, cancellationToken);

        result.Message = BuildMessage(result);

        _logger.LogInformation(
            "[照片上传] 批次 {BatchId}：共 {Total} 张，写入 {Succeeded}、秒传 {Skipped}、降级 {Degraded}、失败 {Failed}。",
            batchId, result.Total, result.Succeeded, result.Skipped, result.Degraded, result.Failed);

        return result;
    }

    /// <summary>一个上传项在流水线中的中间状态。</summary>
    private sealed class PreparedItem
    {
        public PhotoUploadRequest Request { get; init; } = default!;

        public StuStudent? Student { get; init; }

        public StuPhoto? Existing { get; init; }

        public PhotoUploadItemResultDto Result { get; init; } = default!;

        /// <summary>需要写存储时才有内容；写完立刻置空以释放内存。</summary>
        public byte[]? Content { get; set; }

        public string? Hash { get; init; }

        public string? RelativePath { get; init; }

        /// <summary>缩略图的相对路径（规则见 <see cref="PhotoPathRule.BuildThumbnail"/>）。</summary>
        public string? ThumbnailPath { get; init; }

        /// <summary>原图尺寸；内容不是可解码的图片时为 null（此时不生成缩略图，但上传照常完成）。</summary>
        public PhotoImageSize? Size { get; set; }

        /// <summary>缩略图是否已成功写入存储。</summary>
        public bool ThumbnailWritten { get; set; }

        /// <summary>缩略图写入失败的原因（只写日志，不影响上传结果）。</summary>
        public string? ThumbnailError { get; set; }

        /// <summary>内容字节数。写完存储后 <see cref="Content"/> 会被释放，因此单独留一份长度。</summary>
        public long ContentLength { get; init; }

        public bool NeedWrite { get; set; }

        public PhotoWriteResult? Write { get; set; }
    }

    private async Task<PreparedItem> PrepareAsync(
        PhotoUploadRequest request,
        IReadOnlyDictionary<Guid, StuStudent> students,
        IReadOnlyDictionary<Guid, StuPhoto> existingPhotos,
        PhotoUploadResultDto result,
        CancellationToken cancellationToken)
    {
        var itemResult = new PhotoUploadItemResultDto { StudentId = request.StudentId };

        var prepared = new PreparedItem
        {
            Request = request,
            Result = itemResult,
            Student = students.GetValueOrDefault(request.StudentId),
            Existing = existingPhotos.GetValueOrDefault(request.StudentId)
        };

        if (prepared.Student == null)
        {
            itemResult.ErrorMessage = "学生不存在。";
            result.Failed++;
            result.Items.Add(itemResult);
            return prepared;
        }

        itemResult.StudentNo = prepared.Student.StudentNo;

        if (!PhotoPathRule.IsAllowedExtension(request.FileName))
        {
            itemResult.ErrorMessage =
                $"不支持的图片格式「{Path.GetExtension(request.FileName)}」，仅支持 jpg / jpeg / png / bmp。";
            result.Failed++;
            result.Items.Add(itemResult);
            return prepared;
        }

        byte[] content;
        try
        {
            content = await ReadAllBytesAsync(request.Content, cancellationToken);
        }
        catch (Exception ex)
        {
            itemResult.ErrorMessage = $"读取上传内容失败：{ex.Message}";
            result.Failed++;
            result.Items.Add(itemResult);
            return prepared;
        }

        if (content.Length == 0)
        {
            itemResult.ErrorMessage = "上传的文件是空的。";
            result.Failed++;
            result.Items.Add(itemResult);
            return prepared;
        }

        var hash = Convert.ToHexString(SHA256.HashData(content));
        var relativePath = PhotoPathRule.Build(
            prepared.Student.SchoolOrgId,
            prepared.Student.Id,
            request.FileName);

        // 尺寸探测放在这里（内容还在手上），缩略图的**生成**放到并发写存储阶段：
        // 解码 + 缩放是 CPU 活，放在校验阶段会让"几百张照片"的批次在写存储之前就卡住。
        // 探测不出来（改了后缀的文本、损坏文件）也不拒绝上传，只是没有尺寸与缩略图 ——
        // 内容校验是另一件事，见技术债「上传内容未做真实性校验」。
        var size = await _imageProcessor.ProbeAsync(content, cancellationToken);

        prepared.Size = size;

        // 秒传：同一名学生、内容哈希一致、且上次是正常/降级成功态 → 不重复写存储
        if (prepared.Existing != null
            && prepared.Existing.FileHash == hash
            && (prepared.Existing.Status == PhotoStatuses.Normal
                || prepared.Existing.Status == PhotoStatuses.FallbackPending))
        {
            itemResult.Success = true;
            itemResult.Skipped = true;
            itemResult.PhotoId = prepared.Existing.Id;
            itemResult.RelativePath = prepared.Existing.RelativePath;
            itemResult.ThumbnailPath = prepared.Existing.ThumbnailPath;
            itemResult.Width = prepared.Existing.Width;
            itemResult.Height = prepared.Existing.Height;
            result.Skipped++;
            result.Items.Add(itemResult);
            return prepared;
        }

        var writeItem = new PreparedItem
        {
            Request = request,
            Result = itemResult,
            Student = prepared.Student,
            Existing = prepared.Existing,
            Content = content,
            Hash = hash,
            RelativePath = relativePath,
            ThumbnailPath = PhotoPathRule.BuildThumbnail(prepared.Student.SchoolOrgId, prepared.Student.Id),
            // 注意：探测结果必须**显式带过来**。writeItem 是另一个对象，
            // 只给 prepared 赋值的话，落库阶段读到的仍是 null —— 尺寸与缩略图会静默丢失
            // （这个坑被集成测试逮住：result.Width 一直是 0）
            Size = size,
            ContentLength = content.Length,
            NeedWrite = true
        };

        result.Items.Add(itemResult);
        return writeItem;
    }

    private async Task WriteStorageInParallelAsync(
        List<PreparedItem> items,
        CancellationToken cancellationToken)
    {
        var pending = items.Where(i => i.NeedWrite).ToList();

        if (pending.Count == 0)
        {
            return;
        }

        // 用信号量限制在途写入数；每个写入只碰存储，不碰数据库
        using var gate = new SemaphoreSlim(StorageConcurrency, StorageConcurrency);

        var tasks = pending.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);

            try
            {
                using var stream = new MemoryStream(item.Content!, writable: false);
                item.Write = await _storage.WriteAsync(item.RelativePath!, stream, cancellationToken);

                if (item.Write.Success)
                {
                    await WriteThumbnailAsync(item, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                item.Write = PhotoWriteResult.Failed(ex.Message);
            }
            finally
            {
                // 字节用完即弃，避免一整批照片同时驻留内存
                item.Content = null;
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 生成并写入缩略图。
    ///
    /// **缩略图是锦上添花，失败绝不能影响上传**：它生成不出来时只记日志，
    /// 原图照样已经写好了、台账照样登记 —— 列表页大不了继续加载原图。
    /// 这也是为什么这里吞掉所有异常而不是向上抛。
    /// </summary>
    private async Task WriteThumbnailAsync(PreparedItem item, CancellationToken cancellationToken)
    {
        if (item.ThumbnailPath == null || item.Content == null)
        {
            return;
        }

        if (item.Size == null)
        {
            // 不是可解码的图片（例如只改了后缀的文件）：没有缩略图可生成
            return;
        }

        try
        {
            var thumbnail = await _imageProcessor.CreateThumbnailAsync(item.Content, cancellationToken: cancellationToken);

            if (thumbnail == null)
            {
                return;
            }

            using var thumbStream = new MemoryStream(thumbnail, writable: false);
            var write = await _storage.WriteAsync(item.ThumbnailPath, thumbStream, cancellationToken);

            item.ThumbnailWritten = write.Success;
            item.ThumbnailError = write.Success ? null : write.Error;
        }
        catch (Exception ex)
        {
            item.ThumbnailError = ex.Message;

            _logger.LogWarning(ex, "生成缩略图失败：{Path}", item.RelativePath);
        }
    }

    private async Task PersistAsync(
        List<PreparedItem> items,
        Guid batchId,
        PhotoUploadResultDto result,
        CancellationToken cancellationToken)
    {
        var now = _clock.Now;
        var userId = _currentUser.Id;
        var userName = _currentUser.UserName;

        var toInsert = new List<StuPhoto>();
        var toUpdate = new List<StuPhoto>();
        var touchedStudents = new Dictionary<Guid, StuStudent>();

        foreach (var item in items.Where(i => i.NeedWrite))
        {
            var write = item.Write!;

            if (!write.Success)
            {
                result.Failed++;
                item.Result.ErrorMessage = write.Error;

                // 失败也落一条记录：否则「传了但没成功」在系统里查无此事，运维无法重试
                UpsertRow(item, PhotoStatuses.Failed, write.Error, batchId, now, userId, userName, toInsert, toUpdate);
                continue;
            }

            if (write.Degraded)
            {
                result.Degraded++;
                item.Result.Success = true;
                item.Result.Degraded = true;
                // 降级也算成功，但必须把原因带回给调用方：
                // 前端要能提示「存储暂不可用，已暂存，系统会自动补传」
                item.Result.ErrorMessage = write.Error;
            }
            else
            {
                result.Succeeded++;
                item.Result.Success = true;
            }

            var status = write.Degraded ? PhotoStatuses.FallbackPending : PhotoStatuses.Normal;

            var row = UpsertRow(item, status, write.Error, batchId, now, userId, userName, toInsert, toUpdate);

            item.Result.PhotoId = row.Id;
            item.Result.RelativePath = row.RelativePath;
            item.Result.ThumbnailPath = row.ThumbnailPath;
            item.Result.Width = row.Width;
            item.Result.Height = row.Height;

            // 学生主表上的 PhotoPath 是「有没有照片」的唯一判据（缺照片台账靠它分页）
            item.Student!.PhotoPath = row.RelativePath;
            touchedStudents[item.Student.Id] = item.Student;
        }

        if (toInsert.Count > 0)
        {
            await _photos.InsertManyAsync(toInsert, autoSave: true, cancellationToken);
        }

        if (toUpdate.Count > 0)
        {
            await _photos.UpdateManyAsync(toUpdate, autoSave: true, cancellationToken);
        }

        if (touchedStudents.Count > 0)
        {
            await _students.UpdateManyAsync(touchedStudents.Values, autoSave: true, cancellationToken);
        }
    }

    private StuPhoto UpsertRow(
        PreparedItem item,
        string status,
        string? error,
        Guid batchId,
        DateTime now,
        Guid? userId,
        string? userName,
        List<StuPhoto> toInsert,
        List<StuPhoto> toUpdate)
    {
        var row = item.Existing;

        if (row == null)
        {
            row = new StuPhoto(
                _guidGenerator.Create(),
                item.Student!.Id,
                item.Student.SchoolOrgId,
                item.RelativePath!,
                item.Hash!,
                batchId);

            toInsert.Add(row);
        }
        else
        {
            row.RelativePath = item.RelativePath!;
            row.FileHash = item.Hash!;
            toUpdate.Add(row);
        }

        row.StudentNo = item.Student!.StudentNo;
        row.StudentName = item.Student.Name;
        row.SchoolOrgId = item.Student.SchoolOrgId;
        // 冗余学校物化路径：数据范围过滤靠它做一次前缀比较
        row.SchoolPathCode = item.Student.SchoolPathCode;
        row.Status = status;
        row.ErrorMessage = error;
        row.BatchId = batchId;
        row.UploadedTime = now;
        row.UploadedByUserId = userId;
        row.UploadedByName = userName;
        row.FileSize = item.ContentLength;

        // 尺寸与缩略图：探测/生成失败时保持原值（0 / null），
        // 而不是把上一次成功的值抹掉 —— 重新上传一张坏图不该让旧缩略图也消失
        if (item.Size != null)
        {
            row.Width = item.Size.Width;
            row.Height = item.Size.Height;
        }

        if (item.ThumbnailWritten)
        {
            row.ThumbnailPath = item.ThumbnailPath;
        }

        return row;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream is MemoryStream memoryStream && memoryStream.TryGetBuffer(out var buffer))
        {
            return buffer.AsSpan(0, (int)memoryStream.Length).ToArray();
        }

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return ms.ToArray();
    }

    private static string BuildMessage(PhotoUploadResultDto result)
    {
        if (result.Total == 0)
        {
            return "没有需要上传的照片。";
        }

        var parts = new List<string> { $"成功 {result.Succeeded} 张" };

        if (result.Skipped > 0)
        {
            parts.Add($"秒传 {result.Skipped} 张");
        }

        if (result.Degraded > 0)
        {
            parts.Add($"降级暂存 {result.Degraded} 张（存储暂不可用，系统会自动补传）");
        }

        if (result.Failed > 0)
        {
            parts.Add($"失败 {result.Failed} 张");
        }

        return $"共 {result.Total} 张：{string.Join("、", parts)}。";
    }
}
