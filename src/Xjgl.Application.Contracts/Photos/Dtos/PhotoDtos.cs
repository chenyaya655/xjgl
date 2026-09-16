using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Dtos;

namespace Xjgl.Photos.Dtos;

/// <summary>照片档案。</summary>
public class PhotoDto
{
    public Guid Id { get; set; }

    public Guid StudentId { get; set; }

    public string? StudentNo { get; set; }

    public string? StudentName { get; set; }

    public Guid SchoolOrgId { get; set; }

    public string RelativePath { get; set; } = default!;

    public string? ThumbnailPath { get; set; }

    public string FileHash { get; set; } = default!;

    public long FileSize { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public string Status { get; set; } = default!;

    public string StatusText { get; set; } = default!;

    public Guid BatchId { get; set; }

    public DateTime UploadedTime { get; set; }

    public string? UploadedByName { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreationTime { get; set; }
}

/// <summary>照片查询条件。</summary>
public class PhotoQueryDto : PagedAndSortedResultRequestDto
{
    /// <summary>学生 Id（精确）。</summary>
    public Guid? StudentId { get; set; }

    /// <summary>学校组织 Id（精确）。</summary>
    public Guid? SchoolOrgId { get; set; }

    /// <summary>上传批次 Id（精确）。</summary>
    public Guid? BatchId { get; set; }

    /// <summary>状态：Pending / Normal / FallbackPending / Failed。</summary>
    public string? Status { get; set; }

    /// <summary>学籍辅号或姓名，模糊匹配。</summary>
    public string? Keyword { get; set; }
}

/// <summary>缺照片的学生。</summary>
public class MissingPhotoStudentDto
{
    public Guid StudentId { get; set; }

    public string? StudentNo { get; set; }

    public string? Name { get; set; }

    public Guid SchoolOrgId { get; set; }

    public string? SchoolName { get; set; }

    public string? GradeName { get; set; }

    public string? ClassName { get; set; }

    public string? Status { get; set; }
}

/// <summary>缺照片名单查询条件。</summary>
public class MissingPhotoQueryDto : PagedAndSortedResultRequestDto
{
    public Guid? SchoolOrgId { get; set; }

    public Guid? GradeId { get; set; }

    public Guid? ClassId { get; set; }

    public string? Keyword { get; set; }
}

/// <summary>单张照片的上传入参（来自 <c>IFormFile</c> 或后台任务）。</summary>
public class PhotoUploadRequest
{
    [Required]
    public Guid StudentId { get; set; }

    /// <summary>原始文件名，仅用于取扩展名。</summary>
    public string? FileName { get; set; }

    /// <summary>文件内容。</summary>
    public Stream Content { get; set; } = default!;

    /// <summary>内容长度（字节）。未知时可传 0，服务端会按实际读取的结果落库。</summary>
    public long Length { get; set; }
}

/// <summary>单个上传结果。</summary>
public class PhotoUploadItemResultDto
{
    public Guid StudentId { get; set; }

    public string? StudentNo { get; set; }

    public bool Success { get; set; }

    /// <summary>是否命中秒传（内容哈希已存在，未重复写入存储）。</summary>
    public bool Skipped { get; set; }

    /// <summary>是否降级写入（主存储熔断，落到了本地盘，等待补偿）。</summary>
    public bool Degraded { get; set; }

    public Guid? PhotoId { get; set; }

    public string? RelativePath { get; set; }

    public string? ErrorMessage { get; set; }
}

/// <summary>批量上传结果。</summary>
public class PhotoUploadResultDto
{
    public Guid BatchId { get; set; }

    public int Total { get; set; }

    /// <summary>真正写入存储的条数。</summary>
    public int Succeeded { get; set; }

    /// <summary>命中秒传、未重复写入的条数。</summary>
    public int Skipped { get; set; }

    /// <summary>降级写入的条数。</summary>
    public int Degraded { get; set; }

    public int Failed { get; set; }

    public List<PhotoUploadItemResultDto> Items { get; set; } = new();

    /// <summary>给前端直接展示的一句话结论。</summary>
    public string Message { get; set; } = default!;
}

/// <summary>照片存储健康度。</summary>
public class PhotoStorageHealthDto
{
    /// <summary>主存储（NAS）名称。</summary>
    public string PrimaryName { get; set; } = default!;

    /// <summary>主存储当前是否可写。</summary>
    public bool PrimaryAvailable { get; set; }

    /// <summary>降级存储（本地盘）名称。</summary>
    public string FallbackName { get; set; } = default!;

    public bool FallbackAvailable { get; set; }

    /// <summary>熔断器状态：Closed / Open / HalfOpen。</summary>
    public string CircuitState { get; set; } = default!;

    /// <summary>熔断器状态中文说明。</summary>
    public string CircuitStateText { get; set; } = default!;

    /// <summary>降级待补偿的照片条数。</summary>
    public long FallbackPendingCount { get; set; }

    /// <summary>处理失败的照片条数。</summary>
    public long FailedCount { get; set; }

    public DateTime CheckedTime { get; set; }
}

/// <summary>补偿回放结果。</summary>
public class PhotoCompensationResultDto
{
    /// <summary>本轮扫描到的「降级待补偿」条数。</summary>
    public int Scanned { get; set; }

    /// <summary>成功回放到主存储的条数。</summary>
    public int Replayed { get; set; }

    /// <summary>仍然失败、继续保持待补偿的条数。</summary>
    public int StillPending { get; set; }

    /// <summary>回放过程中被判定为超期/不可恢复、置为失败的条数。</summary>
    public int Failed { get; set; }

    /// <summary>是否因为主存储不可用而提前中止。</summary>
    public bool Aborted { get; set; }

    public string Message { get; set; } = default!;
}
