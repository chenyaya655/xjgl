using System.Linq.Expressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Volo.Abp.Application.Dtos;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Timing;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Permissions;
using Xjgl.Photos.Dtos;
using Xjgl.Students;

namespace Xjgl.Photos;

/// <summary>
/// 照片管理：台账查询、删除、缺照片名单、存储健康度、补偿触发。
/// </summary>
[Authorize]
public class PhotoAppService : XjglAppService, IPhotoAppService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IXjglRepository<StuPhoto> _photos;
    private readonly IXjglRepository<StuStudent> _students;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IXjglRepository<SysGrade> _grades;
    private readonly IXjglRepository<SysClass> _classes;
    private readonly ResilientPhotoStorage _resilient;
    private readonly NasPhotoStorage _primary;
    private readonly LocalFallbackPhotoStorage _fallback;
    private readonly IPhotoCompensationService _compensation;
    private readonly IClock _clock;

    public PhotoAppService(
        IXjglRepository<StuPhoto> photos,
        IXjglRepository<StuStudent> students,
        IXjglRepository<SysOrg> orgs,
        IXjglRepository<SysGrade> grades,
        IXjglRepository<SysClass> classes,
        ResilientPhotoStorage resilient,
        NasPhotoStorage primary,
        LocalFallbackPhotoStorage fallback,
        IPhotoCompensationService compensation,
        IClock clock)
    {
        _photos = photos;
        _students = students;
        _orgs = orgs;
        _grades = grades;
        _classes = classes;
        _resilient = resilient;
        _primary = primary;
        _fallback = fallback;
        _compensation = compensation;
        _clock = clock;
    }

    // ------------------------------------------------------------------
    // 台账查询
    // ------------------------------------------------------------------

    [Authorize(MetadataPermissions.Pho.Photo.Query)]
    public async Task<PagedResultDto<PhotoDto>> GetListAsync(PhotoQueryDto input)
    {
        var predicate = BuildPredicate(input);
        var sorting = string.IsNullOrWhiteSpace(input.Sorting) ? "UploadedTime DESC" : input.Sorting;

        var totalCount = await _photos.GetCountAsync(predicate);
        var items = await _photos.GetPagedListAsync(
            predicate,
            sorting,
            input.SkipCount,
            input.MaxResultCount);

        return new PagedResultDto<PhotoDto>(totalCount, items.Select(ToDto).ToList());
    }

    [Authorize(MetadataPermissions.Pho.Photo.Query)]
    public async Task<PhotoDto> GetAsync(Guid id)
    {
        var entity = await _photos.GetAsync(id);
        return ToDto(entity);
    }

    /// <summary>缺照片的学生名单。</summary>
    ///
    /// 判据是学生主表的 <c>PhotoPath</c> 为空，而不是「stu_photo 里没有行」。
    /// 原因：缺照片名单必须能正确分页与统计总数，而「左连接后过滤」
    /// 在仓储抽象里做不到（<c>IXjglRepository</c> 不支持 join），
    /// 只能退化成「先分页再过滤」—— 那样总数和翻页都会错。
    /// 上传/删除照片时同步维护 <c>PhotoPath</c>，代价是两处写、
    /// 换来的是一个可索引、可分页的判据。
    [Authorize(MetadataPermissions.Pho.Photo.Query)]
    public async Task<PagedResultDto<MissingPhotoStudentDto>> GetMissingPhotoListAsync(MissingPhotoQueryDto input)
    {
        var predicate = BuildMissingPredicate(input);
        var sorting = string.IsNullOrWhiteSpace(input.Sorting) ? "StudentNo ASC" : input.Sorting;

        var totalCount = await _students.GetCountAsync(predicate);
        var students = await _students.GetPagedListAsync(
            predicate,
            sorting,
            input.SkipCount,
            input.MaxResultCount);

        if (students.Count == 0)
        {
            return new PagedResultDto<MissingPhotoStudentDto>(totalCount, new List<MissingPhotoStudentDto>());
        }

        var orgIds = students.Select(s => s.SchoolOrgId).Distinct().ToList();
        var gradeIds = students.Where(s => s.GradeId.HasValue).Select(s => s.GradeId!.Value).Distinct().ToList();
        var classIds = students.Where(s => s.ClassId.HasValue).Select(s => s.ClassId!.Value).Distinct().ToList();

        var orgs = (await _orgs.GetListAsync(o => orgIds.Contains(o.Id))).ToDictionary(o => o.Id);
        var grades = gradeIds.Count == 0
            ? new Dictionary<Guid, SysGrade>()
            : (await _grades.GetListAsync(g => gradeIds.Contains(g.Id))).ToDictionary(g => g.Id);
        var classes = classIds.Count == 0
            ? new Dictionary<Guid, SysClass>()
            : (await _classes.GetListAsync(c => classIds.Contains(c.Id))).ToDictionary(c => c.Id);

        var items = students.Select(s => new MissingPhotoStudentDto
        {
            StudentId = s.Id,
            StudentNo = s.StudentNo,
            Name = s.Name,
            SchoolOrgId = s.SchoolOrgId,
            SchoolName = orgs.GetValueOrDefault(s.SchoolOrgId)?.Name,
            GradeName = s.GradeId.HasValue ? grades.GetValueOrDefault(s.GradeId.Value)?.GradeName : null,
            ClassName = s.ClassId.HasValue ? classes.GetValueOrDefault(s.ClassId.Value)?.ClassName : null,
            Status = s.StudentStatusCode
        }).ToList();

        return new PagedResultDto<MissingPhotoStudentDto>(totalCount, items);
    }

    // ------------------------------------------------------------------
    // 删除
    // ------------------------------------------------------------------

    [Authorize(MetadataPermissions.Pho.Photo.Delete)]
    public async Task DeleteAsync(Guid id)
    {
        var entity = await _photos.GetAsync(id);
        await RemoveAsync(new[] { entity });
    }

    [Authorize(MetadataPermissions.Pho.Photo.Delete)]
    public async Task<int> DeleteManyAsync(List<Guid> ids)
    {
        if (ids == null || ids.Count == 0)
        {
            return 0;
        }

        var entities = await _photos.GetListAsync(p => ids.Contains(p.Id));

        if (entities.Count == 0)
        {
            return 0;
        }

        await RemoveAsync(entities);

        return entities.Count;
    }

    private async Task RemoveAsync(IReadOnlyList<StuPhoto> entities)
    {
        // 1) 先删存储里的文件（同时清 NAS 与本地降级区），文件删不掉不影响台账删除
        foreach (var entity in entities)
        {
            await _resilient.DeleteAsync(entity.RelativePath);

            if (!string.IsNullOrWhiteSpace(entity.ThumbnailPath))
            {
                await _resilient.DeleteAsync(entity.ThumbnailPath);
            }
        }

        // 2) 再删台账
        await _photos.DeleteManyAsync(entities.Select(e => e.Id).ToList(), autoSave: true);

        // 3) 清掉学生主表上的照片标记，否则「缺照片名单」永远看不到这些学生
        var studentIds = entities.Select(e => e.StudentId).Distinct().ToList();
        var students = await _students.GetListAsync(s => studentIds.Contains(s.Id));

        var touched = new List<StuStudent>();

        foreach (var student in students)
        {
            var stillHasPhoto = await _photos.AnyAsync(
                p => p.StudentId == student.Id
                     && (p.Status == PhotoStatuses.Normal || p.Status == PhotoStatuses.FallbackPending));

            if (!stillHasPhoto && !string.IsNullOrWhiteSpace(student.PhotoPath))
            {
                student.PhotoPath = null;
                touched.Add(student);
            }
        }

        if (touched.Count > 0)
        {
            await _students.UpdateManyAsync(touched, autoSave: true);
        }
    }

    // ------------------------------------------------------------------
    // 存储健康度与补偿
    // ------------------------------------------------------------------

    [Authorize(MetadataPermissions.Pho.Photo.Query)]
    public async Task<PhotoStorageHealthDto> GetStorageHealthAsync()
    {
        var snapshot = _resilient.GetCircuitSnapshot();

        return new PhotoStorageHealthDto
        {
            PrimaryName = _primary.Name,
            PrimaryAvailable = await ProbeAsync(_primary),
            FallbackName = _fallback.Name,
            FallbackAvailable = await ProbeAsync(_fallback),
            CircuitState = snapshot.State.ToString(),
            CircuitStateText = DescribeCircuit(snapshot.State),
            FallbackPendingCount = await _photos.GetCountAsync(p => p.Status == PhotoStatuses.FallbackPending),
            FailedCount = await _photos.GetCountAsync(p => p.Status == PhotoStatuses.Failed),
            CheckedTime = _clock.Now
        };
    }

    [Authorize(MetadataPermissions.Pho.Photo.Compensate)]
    public Task<PhotoCompensationResultDto> CompensateAsync()
    {
        return _compensation.CompensateAsync();
    }

    /// <summary>
    /// 真实写一个探针文件来判断存储是否可写。
    ///
    /// 为什么不只判「目录是否存在」：NAS 挂载点掉线后目录仍然在，
    /// 只是写入会卡住或报错 —— 只看目录存在会给出「一切正常」的假象。
    /// 探针直接打主/降级存储本体（绕过熔断器），否则熔断打开时
    /// 会永远探不出主存储已经恢复。
    /// </summary>
    private static async Task<bool> ProbeAsync(IPhotoStorage storage)
    {
        var probePath = $"health/probe-{Guid.NewGuid():N}.txt";

        try
        {
            using var cts = new CancellationTokenSource(ProbeTimeout);
            using var content = new MemoryStream(new byte[] { 0x58, 0x4A, 0x47, 0x4C });

            var write = await storage.WriteAsync(probePath, content, cts.Token);

            if (!write.Success)
            {
                return false;
            }

            await storage.DeleteAsync(probePath, cts.Token);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeCircuit(PhotoCircuitState state) => state switch
    {
        PhotoCircuitState.Closed => "正常（可直接写入主存储）",
        PhotoCircuitState.Open => "已熔断（照片暂存本地，等待自动补传）",
        PhotoCircuitState.HalfOpen => "半开试探中（正在尝试恢复）",
        _ => state.ToString()
    };

    // ------------------------------------------------------------------
    // 谓词与映射
    // ------------------------------------------------------------------

    private static Expression<Func<StuPhoto, bool>>? BuildPredicate(PhotoQueryDto input)
    {
        var hasStudent = input.StudentId.HasValue;
        var hasSchool = input.SchoolOrgId.HasValue;
        var hasBatch = input.BatchId.HasValue;
        var hasStatus = !string.IsNullOrWhiteSpace(input.Status);
        var hasKeyword = !string.IsNullOrWhiteSpace(input.Keyword);

        if (!hasStudent && !hasSchool && !hasBatch && !hasStatus && !hasKeyword)
        {
            return null;
        }

        var studentId = input.StudentId ?? Guid.Empty;
        var schoolOrgId = input.SchoolOrgId ?? Guid.Empty;
        var batchId = input.BatchId ?? Guid.Empty;
        var status = input.Status?.Trim();
        var keyword = input.Keyword?.Trim();

        return p =>
            (!hasStudent || p.StudentId == studentId)
            && (!hasSchool || p.SchoolOrgId == schoolOrgId)
            && (!hasBatch || p.BatchId == batchId)
            && (!hasStatus || p.Status == status)
            && (!hasKeyword
                || (p.StudentNo != null && p.StudentNo.Contains(keyword!))
                || (p.StudentName != null && p.StudentName.Contains(keyword!)));
    }

    private static Expression<Func<StuStudent, bool>>? BuildMissingPredicate(MissingPhotoQueryDto input)
    {
        var hasSchool = input.SchoolOrgId.HasValue;
        var hasGrade = input.GradeId.HasValue;
        var hasClass = input.ClassId.HasValue;
        var hasKeyword = !string.IsNullOrWhiteSpace(input.Keyword);

        var schoolOrgId = input.SchoolOrgId ?? Guid.Empty;
        var gradeId = input.GradeId ?? Guid.Empty;
        var classId = input.ClassId ?? Guid.Empty;
        var keyword = input.Keyword?.Trim();

        return s =>
            (s.PhotoPath == null || s.PhotoPath == "")
            && (!hasSchool || s.SchoolOrgId == schoolOrgId)
            && (!hasGrade || s.GradeId == gradeId)
            && (!hasClass || s.ClassId == classId)
            && (!hasKeyword || s.Name.Contains(keyword!) || s.StudentNo.Contains(keyword!));
    }

    private static PhotoDto ToDto(StuPhoto entity) => new()
    {
        Id = entity.Id,
        StudentId = entity.StudentId,
        StudentNo = entity.StudentNo,
        StudentName = entity.StudentName,
        SchoolOrgId = entity.SchoolOrgId,
        RelativePath = entity.RelativePath,
        ThumbnailPath = entity.ThumbnailPath,
        FileHash = entity.FileHash,
        FileSize = entity.FileSize,
        Width = entity.Width,
        Height = entity.Height,
        Status = entity.Status,
        StatusText = PhotoStatuses.GetDisplayName(entity.Status),
        BatchId = entity.BatchId,
        UploadedTime = entity.UploadedTime,
        UploadedByName = entity.UploadedByName,
        ErrorMessage = entity.ErrorMessage,
        CreationTime = entity.CreationTime
    };
}

/// <summary>
/// 降级照片的补偿回放。
///
/// 场景：NAS 抖动/维护期间，照片被 <see cref="ResilientPhotoStorage"/> 暂存到本地盘，
/// 台账状态是 <see cref="PhotoStatuses.FallbackPending"/>。
/// NAS 恢复后本服务把它们逐个搬回 NAS，成功后删掉本地副本并把状态改成正常。
///
/// 幂等：重复回放同一张照片只是覆盖写一次，不会产生重复文件（路径由学生 Id 决定）。
/// </summary>
public class PhotoCompensationService : IPhotoCompensationService, ITransientDependency
{
    /// <summary>连续失败多少次就认为 NAS 又挂了，提前收工。</summary>
    private const int AbortAfterConsecutiveFailures = 3;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IXjglRepository<StuPhoto> _photos;
    private readonly NasPhotoStorage _primary;
    private readonly LocalFallbackPhotoStorage _fallback;
    private readonly ILogger<PhotoCompensationService> _logger;

    public PhotoCompensationService(
        IXjglRepository<StuPhoto> photos,
        NasPhotoStorage primary,
        LocalFallbackPhotoStorage fallback,
        ILogger<PhotoCompensationService> logger)
    {
        _photos = photos;
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<PhotoCompensationResultDto> CompensateAsync(
        int maxCount = 500,
        CancellationToken cancellationToken = default)
    {
        var result = new PhotoCompensationResultDto();

        if (maxCount <= 0)
        {
            maxCount = 500;
        }

        // 1) 先探活：NAS 还没好就立刻返回，不空转、不刷日志
        if (!await ProbeAsync(cancellationToken))
        {
            result.Aborted = true;
            result.Message = "主存储（NAS）当前不可写，本轮补偿已跳过，稍后自动重试。";

            _logger.LogWarning("[照片补偿] NAS 探针失败，跳过本轮。");

            return result;
        }

        // 2) 取最早降级的那批，先来的先补
        var pending = await _photos.GetPagedListAsync(
            p => p.Status == PhotoStatuses.FallbackPending,
            "UploadedTime ASC",
            0,
            maxCount,
            cancellationToken);

        result.Scanned = pending.Count;

        if (pending.Count == 0)
        {
            result.Message = "没有需要补偿的照片。";
            return result;
        }

        var touched = new List<StuPhoto>();
        var consecutiveFailures = 0;

        foreach (var photo in pending)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // 本地副本没了 → 这张照片已经无法自动恢复，标记失败交人工处理
            await using var stream = await _fallback.OpenReadAsync(photo.RelativePath, cancellationToken);

            if (stream == null)
            {
                photo.Status = PhotoStatuses.Failed;
                photo.ErrorMessage = "降级副本已丢失，无法自动补传，请通知学校重新上传。";
                touched.Add(photo);
                result.Failed++;

                _logger.LogWarning("[照片补偿] 降级副本缺失：{Path}", photo.RelativePath);

                continue;
            }

            var write = await _primary.WriteAsync(photo.RelativePath, stream, cancellationToken);

            if (!write.Success)
            {
                consecutiveFailures++;
                photo.ErrorMessage = $"补传失败：{write.Error}";
                touched.Add(photo);
                result.StillPending++;

                _logger.LogWarning("[照片补偿] 补传失败：{Path}（{Error}）", photo.RelativePath, write.Error);

                if (consecutiveFailures >= AbortAfterConsecutiveFailures)
                {
                    result.Aborted = true;
                    _logger.LogWarning("[照片补偿] 连续 {Count} 次失败，判定 NAS 仍未恢复，中止本轮。", consecutiveFailures);
                    break;
                }

                continue;
            }

            consecutiveFailures = 0;

            // 回放成功：删本地副本 + 状态转正常
            await _fallback.DeleteAsync(photo.RelativePath, cancellationToken);

            photo.Status = PhotoStatuses.Normal;
            photo.ErrorMessage = null;
            touched.Add(photo);
            result.Replayed++;
        }

        if (touched.Count > 0)
        {
            await _photos.UpdateManyAsync(touched, autoSave: true, cancellationToken);
        }

        result.Message = result.Aborted
            ? $"本轮扫描 {result.Scanned} 张：已补传 {result.Replayed} 张，{result.StillPending} 张待下轮，{result.Failed} 张转为失败。NAS 仍不稳定，已提前中止。"
            : $"本轮扫描 {result.Scanned} 张：已补传 {result.Replayed} 张，{result.StillPending} 张待下轮，{result.Failed} 张转为失败。";

        _logger.LogInformation(
            "[照片补偿] 扫描 {Scanned}、补传 {Replayed}、待补 {Pending}、失败 {Failed}、中止 {Aborted}。",
            result.Scanned, result.Replayed, result.StillPending, result.Failed, result.Aborted);

        return result;
    }

    private async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        var probePath = $"health/probe-{Guid.NewGuid():N}.txt";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);

            using var content = new MemoryStream(new byte[] { 0x58, 0x4A, 0x47, 0x4C });

            var write = await _primary.WriteAsync(probePath, content, cts.Token);

            if (!write.Success)
            {
                return false;
            }

            await _primary.DeleteAsync(probePath, cts.Token);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[照片补偿] NAS 探针异常。");
            return false;
        }
    }
}
