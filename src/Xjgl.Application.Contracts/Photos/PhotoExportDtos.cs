using System.ComponentModel.DataAnnotations;

namespace Xjgl.Photos;

/// <summary>
/// 照片打包导出的条件。
///
/// 与照片台账页的筛选口径**保持一致**：导出的就是"你当前看到的这批"。
/// 刻意不支持按年级/班级过滤照片本身 —— 那需要先把学生范围查出来再反查照片，
/// 而实际使用中"按班收照片"用**上传批次**就能精确表达（一个班一次上传 = 一个批次）。
/// </summary>
public class PhotoExportQueryDto
{
    public Guid? SchoolOrgId { get; set; }

    /// <summary>按上传批次导出（批量上传时前端生成的批次 Id）。</summary>
    public Guid? BatchId { get; set; }

    /// <summary>按状态过滤（Normal / FallbackPending / Pending / Failed）。</summary>
    [StringLength(32)]
    public string? Status { get; set; }

    /// <summary>关键字（学籍辅号或姓名）。</summary>
    [StringLength(64)]
    public string? Keyword { get; set; }

    /// <summary>最多导出张数（默认 500，上限 2000）。</summary>
    [Range(1, 2000)]
    public int MaxCount { get; set; } = 500;

    /// <summary>导出缩略图而不是原图（体积小得多，适合快速核对名单）。</summary>
    public bool UseThumbnail { get; set; }

    /// <summary>是否附一份「缺照片名单.csv」（默认附）。</summary>
    public bool IncludeMissingList { get; set; } = true;

    /// <summary>缺照片名单的年级/班级过滤（只作用于那份 CSV）。</summary>
    public Guid? MissingGradeId { get; set; }

    public Guid? MissingClassId { get; set; }
}

/// <summary>打包导出的结果。</summary>
public sealed class PhotoExportResult
{
    public PhotoExportResult(byte[] content, string fileName, int photoCount, int skippedCount)
    {
        Content = content;
        FileName = fileName;
        PhotoCount = photoCount;
        SkippedCount = skippedCount;
    }

    public byte[] Content { get; }

    public string FileName { get; }

    /// <summary>成功放进压缩包的照片数。</summary>
    public int PhotoCount { get; }

    /// <summary>台账里有记录、但存储里读不到的（会写进清单里，而不是悄悄少一张）。</summary>
    public int SkippedCount { get; }
}

/// <summary>
/// 照片打包导出（zip）。
///
/// 与打印、取图同样是"产出二进制"的能力，因此不实现 <c>IApplicationService</c>：
/// 由控制器设置响应头，而不是把几 MB 的 zip base64 塞进 JSON。
/// </summary>
public interface IPhotoExportService
{
    /// <summary>按条件打包导出；压缩包内附清单 CSV。</summary>
    Task<PhotoExportResult> ExportAsync(
        PhotoExportQueryDto input,
        CancellationToken cancellationToken = default);
}
