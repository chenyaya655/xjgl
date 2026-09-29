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
    public PhotoExportResult(
        byte[]? content,
        string fileName,
        int photoCount,
        int skippedCount,
        bool reachedLimit = false)
    {
        Content = content;
        FileName = fileName;
        PhotoCount = photoCount;
        SkippedCount = skippedCount;
        ReachedLimit = reachedLimit;
    }

    /// <summary>
    /// 压缩包字节。**只有 <c>ExportAsync</c> 会填充它**；
    /// 走流式导出（<c>ExportToAsync</c>）时为 <c>null</c>。
    ///
    /// 【为什么要有"没有内容"这种状态 —— 债 22】
    /// 打包必须能**直接写进目标流**，而不是先在内存里拼出一个完整 zip
    /// （原实现是 `MemoryStream` + `ToArray()`，峰值内存约等于压缩包的 2 倍；
    /// 上千张照片的批次会实打实地吃内存）。
    /// 后台导出任务因此走 `ExportToAsync(stream)`，它只需要计数与文件名，
    /// 不碰这个属性。写在这里是为了让"什么时候它是 null"有据可查，
    /// 而不是让调用方读到一个空数组还以为包是空的。
    /// </summary>
    public byte[]? Content { get; }

    public string FileName { get; }

    /// <summary>成功放进压缩包的照片数。</summary>
    public int PhotoCount { get; }

    /// <summary>台账里有记录、但存储里读不到的（会写进清单里，而不是悄悄少一张）。</summary>
    public int SkippedCount { get; }

    /// <summary>
    /// 是否因为达到请求的 <see cref="PhotoExportQueryDto.MaxCount"/> 上限而停止。
    ///
    /// 为 true 时**不能保证**已包含全部可导出照片，调用方必须把这件事说出来 ——
    /// 否则用户看到"打包了 500 张"会以为导完了（债 24b 的另一半就是这个"不吭声"）。
    ///
    /// 它与 <see cref="SkippedCount"/> 是两回事，**不能合并**：
    /// 那个是"台账有记录但存储里读不到"（存储问题），这个是"被上限砍掉"（还可能拿得到）。
    /// 混为一谈会让用户以为"再导一次就能拿到更多"。
    /// </summary>
    public bool ReachedLimit { get; }
}

/// <summary>
/// 照片打包导出（zip）。
///
/// 与打印、取图同样是"产出二进制"的能力，因此不实现 <c>IApplicationService</c>：
/// 由控制器设置响应头，而不是把几 MB 的 zip base64 塞进 JSON。
/// </summary>
public interface IPhotoExportService
{
    /// <summary>按条件打包导出；压缩包内附清单 CSV。结果里带 <see cref="PhotoExportResult.Content"/>。</summary>
    Task<PhotoExportResult> ExportAsync(
        PhotoExportQueryDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按条件打包导出，**直接把压缩包写进 <paramref name="output"/>**（债 22）。
    ///
    /// 后台导出任务用这个重载：它拿到的产物可能很大，没有理由先在内存里
    /// 拼一份完整的 zip 再复制到文件。结果里的 <see cref="PhotoExportResult.Content"/>
    /// 为 <c>null</c>（内容已经写进 <paramref name="output"/> 了），计数与文件名照常返回。
    /// </summary>
    Task<PhotoExportResult> ExportToAsync(
        PhotoExportQueryDto input,
        Stream output,
        CancellationToken cancellationToken = default);
}
