using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Xjgl.Photos.Dtos;
using Xjgl.Text;

namespace Xjgl.Photos;

/// <summary>
/// 照片打包导出实现。
///
/// 三条设计取舍：
///   1. **导出的是"当前筛选条件下你看到的这批"**：照片列表查询本身就带数据范围约束，
///      导出复用它 —— 否则会出现"界面看不到、导出却拿得到"的越权（导入导出模块踩过同一个坑）。
///   2. **读不到的写进清单，而不是悄悄少一张**：台账里有记录但存储里没有文件（NAS 被清理、
///      降级副本丢失）时，压缩包里那份名单会明确标出"文件缺失"。
///      少一张照片而不出声，教师会以为"全都齐了"。
///   3. **文件名叫「学籍辅号_姓名」**：给老师一份 `3a23be21....jpg` 是没有用的，
///      而文件名里带上辅号才能和花名册对得上。
/// </summary>
public class PhotoExportService : IPhotoExportService, ITransientDependency
{
    /// <summary>单次取数上限（ABP 对 MaxResultCount 有 1000 的硬上限）。</summary>
    private const int PageSize = 1000;

    private readonly IPhotoAppService _photos;
    private readonly IPhotoContentService _content;
    private readonly ILogger<PhotoExportService> _logger;

    public PhotoExportService(
        IPhotoAppService photos,
        IPhotoContentService content,
        ILogger<PhotoExportService> logger)
    {
        _photos = photos;
        _content = content;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PhotoExportResult> ExportAsync(
        PhotoExportQueryDto input,
        CancellationToken cancellationToken = default)
    {
        var rows = await LoadRowsAsync(input, cancellationToken);

        var manifest = new List<IEnumerable<string?>>
        {
            new[] { "学籍辅号", "姓名", "状态", "文件大小(KB)", "宽", "高", "上传时间", "文件名", "说明" }
        };

        using var buffer = new MemoryStream();

        var photoCount = 0;
        var skippedCount = 0;

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                var content = await ReadPhotoAsync(row, input.UseThumbnail, cancellationToken);

                if (content == null)
                {
                    // 台账有、文件没有：写进清单，并在压缩包里留一行说明
                    skippedCount++;
                    manifest.Add(ManifestRow(row, null, "文件缺失（台账有记录但存储里读不到）"));

                    continue;
                }

                var entryName = BuildEntryName(row, usedNames, input.UseThumbnail);

                var entry = zip.CreateEntry($"照片/{entryName}", CompressionLevel.Optimal);

                await using (var entryStream = entry.Open())
                {
                    await entryStream.WriteAsync(content, cancellationToken);
                }

                photoCount++;

                manifest.Add(ManifestRow(row, entryName, null));
            }

            await WriteTextEntryAsync(zip, "照片清单.csv", Csv.WriteForExcel(manifest), cancellationToken);

            if (input.IncludeMissingList)
            {
                var missing = await LoadMissingListAsync(input, cancellationToken);

                await WriteTextEntryAsync(zip, "缺照片名单.csv", missing, cancellationToken);
            }
        }

        var fileName = BuildZipName(input, photoCount);

        _logger.LogInformation(
            "[照片导出] 条件 {Query} → 打包 {Count} 张（缺失 {Skipped}），{Bytes} 字节。",
            Describe(input), photoCount, skippedCount, buffer.Length);

        return new PhotoExportResult(buffer.ToArray(), fileName, photoCount, skippedCount);
    }

    // ------------------------------------------------------------------
    // 取数
    // ------------------------------------------------------------------

    private async Task<List<PhotoDto>> LoadRowsAsync(
        PhotoExportQueryDto input,
        CancellationToken cancellationToken)
    {
        var rows = new List<PhotoDto>();

        for (var skip = 0; skip < input.MaxCount; skip += PageSize)
        {
            var page = await _photos.GetListAsync(new PhotoQueryDto
            {
                SchoolOrgId = input.SchoolOrgId,
                BatchId = input.BatchId,
                Status = input.Status,
                Keyword = input.Keyword,
                Sorting = "StudentNo ASC",
                SkipCount = skip,
                MaxResultCount = Math.Min(PageSize, input.MaxCount - skip)
            });

            rows.AddRange(page.Items);

            if (page.Items.Count < PageSize || rows.Count >= page.TotalCount)
            {
                break;
            }
        }

        // 只导出"有图可导"的状态：待处理/失败的记录仍然会出现在清单里（通过台账也能看到）
        return rows
            .Where(r => r.Status is PhotoStatuses.Normal or PhotoStatuses.FallbackPending)
            .ToList();
    }

    private async Task<string> LoadMissingListAsync(
        PhotoExportQueryDto input,
        CancellationToken cancellationToken)
    {
        var rows = new List<IEnumerable<string?>>
        {
            new[] { "学籍辅号", "姓名", "学校", "年级", "班级", "学籍状态" }
        };

        for (var skip = 0; skip < input.MaxCount; skip += PageSize)
        {
            var page = await _photos.GetMissingPhotoListAsync(new MissingPhotoQueryDto
            {
                SchoolOrgId = input.SchoolOrgId,
                GradeId = input.MissingGradeId,
                ClassId = input.MissingClassId,
                Keyword = input.Keyword,
                SkipCount = skip,
                MaxResultCount = Math.Min(PageSize, input.MaxCount - skip)
            });

            rows.AddRange(page.Items.Select(s => new[]
            {
                s.StudentNo, s.Name, s.SchoolName, s.GradeName, s.ClassName, s.StatusName ?? s.Status
            }));

            if (page.Items.Count < PageSize || rows.Count - 1 >= page.TotalCount)
            {
                break;
            }
        }

        return Csv.WriteForExcel(rows);
    }

    private async Task<byte[]?> ReadPhotoAsync(
        PhotoDto row,
        bool useThumbnail,
        CancellationToken cancellationToken)
    {
        try
        {
            // 复用取图服务：它已经处理好"主存储 → 本地降级区"的回落与数据范围校验
            var content = await _content.GetAsync(row.StudentId, useThumbnail, cancellationToken);

            return content?.Content;
        }
        catch (Exception ex)
        {
            // 单张读失败（存储瞬时不可用等）不该让整批导出失败
            _logger.LogWarning(ex, "[照片导出] 读取失败：{Path}", row.RelativePath);

            return null;
        }
    }

    // ------------------------------------------------------------------
    // 组装
    // ------------------------------------------------------------------

    private static IEnumerable<string?> ManifestRow(PhotoDto row, string? entryName, string? note)
    {
        return new[]
        {
            row.StudentNo,
            row.StudentName,
            row.StatusText,
            (row.FileSize / 1024.0).ToString("F1"),
            row.Width.ToString(),
            row.Height.ToString(),
            row.UploadedTime.ToString("yyyy-MM-dd HH:mm"),
            entryName,
            note
        };
    }

    /// <summary>压缩包内的文件名：学籍辅号_姓名.扩展名（重名时补序号）。</summary>
    private static string BuildEntryName(PhotoDto row, HashSet<string> used, bool useThumbnail)
    {
        var baseName = Sanitize($"{row.StudentNo}_{row.StudentName}");
        var extension = useThumbnail ? ".jpg" : PhotoPathRule.NormalizeExtension(row.RelativePath);

        var name = baseName + extension;
        var index = 2;

        while (!used.Add(name))
        {
            name = $"{baseName}({index++}){extension}";
        }

        return name;
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive zip,
        string entryName,
        string text,
        CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);

        await using var stream = entry.Open();

        // 与导入模板保持一致：带 UTF-8 BOM，Excel 打开中文不乱码
        await stream.WriteAsync(Encoding.UTF8.GetPreamble(), cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    private static string BuildZipName(PhotoExportQueryDto input, int photoCount)
    {
        var parts = new List<string> { "学生照片" };

        if (input.BatchId.HasValue)
        {
            parts.Add(input.BatchId.Value.ToString("N")[..8]);
        }

        if (!input.Keyword.IsNullOrWhiteSpace())
        {
            parts.Add(input.Keyword!);
        }

        parts.Add($"{photoCount}张");
        parts.Add(DateTime.Now.ToString("yyyyMMdd"));

        return $"{string.Join("_", parts.Select(Sanitize))}.zip";
    }

    private static string Describe(PhotoExportQueryDto input)
    {
        var parts = new List<string>();

        if (input.SchoolOrgId.HasValue) parts.Add($"学校={input.SchoolOrgId}");
        if (input.BatchId.HasValue) parts.Add($"批次={input.BatchId}");
        if (!input.Status.IsNullOrWhiteSpace()) parts.Add($"状态={input.Status}");
        if (!input.Keyword.IsNullOrWhiteSpace()) parts.Add($"关键字={input.Keyword}");
        if (input.UseThumbnail) parts.Add("缩略图");

        return parts.Count == 0 ? "全部（受数据范围约束）" : string.Join("、", parts);
    }

    /// <summary>去掉文件名里不能用的字符（姓名与关键字都是用户输入）。</summary>
    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();

        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        return cleaned.Length == 0 ? "未命名" : cleaned;
    }
}
