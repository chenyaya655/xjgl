using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;
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
    /// <summary>
    /// 每页取数上限，可配置。
    ///
    /// 【为什么做成配置】默认值与 ABP 对 `MaxResultCount` 的硬上限（1000）一致，
    /// 生产上不需要改。可配置的理由是**可测试**：这条取数逻辑的价值全在
    /// "第一页取不到足够可导出的照片时要继续翻页"，而要触发翻页就得
    /// 让结果集超过一页 —— 只能把页大小调小，不能靠插 1001 条数据
    /// （那样既慢又让测试库膨胀）。集成测试把它设成 2 来覆盖那条路径。
    /// </summary>
    private const int DefaultPageSize = 1000;

    private const int MinPageSize = 1;

    private readonly IPhotoAppService _photos;
    private readonly IPhotoContentService _content;
    private readonly ILogger<PhotoExportService> _logger;
    private readonly int _pageSize;

    public PhotoExportService(
        IPhotoAppService photos,
        IPhotoContentService content,
        IConfiguration configuration,
        ILogger<PhotoExportService> logger)
    {
        _photos = photos;
        _content = content;
        _logger = logger;

        var configured = configuration.GetValue<int?>("Xjgl:Photo:ExportPageSize") ?? DefaultPageSize;

        // 兜底并钳到 [1, 1000]：配成 0 或负数会让 while 循环直接空转，
        // 而配成 > 1000 会被 ABP 拒绝（MaxResultCount 有硬上限），
        // 两种错法都表现为"导出莫名其妙是空的"，所以在这里一次性收口。
        _pageSize = Math.Clamp(configured < MinPageSize ? DefaultPageSize : configured, MinPageSize, 1000);
    }

    /// <inheritdoc />
    public async Task<PhotoExportResult> ExportAsync(
        PhotoExportQueryDto input,
        CancellationToken cancellationToken = default)
    {
        // 内存版：给"取字节"的调用方（控制器/测试）用。
        // 后台任务走 ExportToAsync，不经过这个 MemoryStream（债 22）。
        using var buffer = new MemoryStream();

        var result = await ExportToAsync(input, buffer, cancellationToken);

        return new PhotoExportResult(
            buffer.ToArray(),
            result.FileName,
            result.PhotoCount,
            result.SkippedCount,
            result.ReachedLimit);
    }

    /// <inheritdoc />
    public async Task<PhotoExportResult> ExportToAsync(
        PhotoExportQueryDto input,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        var (rows, reachedLimit) = await LoadRowsAsync(input, cancellationToken);

        var manifest = new List<IEnumerable<string?>>
        {
            new[] { "学籍辅号", "姓名", "状态", "文件大小(KB)", "宽", "高", "上传时间", "文件名", "说明" }
        };

        var photoCount = 0;
        var skippedCount = 0;

        // leaveOpen: true —— 流的生命周期归调用方（它可能是临时文件或存储写入流），
        // 这里只负责把 zip 写完，不去关别人的流。
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
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
            "[照片导出] 条件 {Query} → 打包 {Count} 张（缺失 {Skipped}、触顶 {ReachedLimit}）。",
            Describe(input), photoCount, skippedCount, reachedLimit);

        // Content 刻意留 null：内容已经写进 output 了（见 PhotoExportResult.Content 的说明）
        return new PhotoExportResult(null, fileName, photoCount, skippedCount, reachedLimit);
    }

    // ------------------------------------------------------------------
    // 取数
    // ------------------------------------------------------------------

    private async Task<(List<PhotoDto> Rows, bool ReachedLimit)> LoadRowsAsync(
        PhotoExportQueryDto input,
        CancellationToken cancellationToken)
    {
        var rows = new List<PhotoDto>();
        var skip = 0;

        // ★ 债 24b：状态过滤**不能**在取完一页之后再做。
        //
        // 原先是"每页取 PageSize 条 → 丢掉非 Normal/FallbackPending → 下一页"，
        // 于是每页里被丢掉的那些会**同时**吃掉翻页窗口与 MaxCount 配额：
        // 台账里混着待处理/失败的记录时，明明有 800 张可导出、上限 500，
        // 却可能只打包出 300 张，而且返回结果里没有任何提示 ——
        // 用户只会觉得"怎么少了这么多照片"。
        //
        // 现在持续翻页直到**取够 MaxCount 条可导出的**或数据耗尽，
        // 于是"上限"真正等于"最多导出多少张"。
        //
        // ⚠ 另有一种情况必须处理：调用方**显式**按状态筛了一个"没有可导出文件"的状态
        // （例如「只看处理失败的照片」）。此时下面的 skip 会把每一行都跳过，
        // 循环会一路翻到数据耗尽 —— 结果是一个只有清单的包，
        // 而 `ReachedLimit` 仍是 false（因为没取够 MaxCount），
        // 使用者会以为"筛选成功但照片是空的"。这里直接短路，让语义明确。
        if (ExplicitlyNonExportable(input.Status))
        {
            return (rows, false);
        }

        while (rows.Count < input.MaxCount)
        {
            var page = await _photos.GetListAsync(new PhotoQueryDto
            {
                SchoolOrgId = input.SchoolOrgId,
                BatchId = input.BatchId,
                Status = input.Status,
                Keyword = input.Keyword,
                // ⚠ 这里**不能**按 `StudentNo ASC` 排。
                //
                // 新建照片行的 `student_no` 一开始是 **null**（上传服务在同一事务里稍后回填），
                // 而 PostgreSQL 的 `ORDER BY ... ASC` 默认把 NULL 排在最前面 ——
                // 于是"还没有学生号的中间态记录"会把可导出的照片挤到后面，
                // 与"优先导出能用的照片"正好相反。实测就是这个顺序：
                // 页大小设成 2 时第一页是 [Normal(有辅号), Pending(null)]，
                // 而全部是 Pending 的那种批次会把第一页占满。
                //
                // 改成 `UploadedTime ASC`：它是**非空**的时间戳，
                // 排序稳定、不依赖任何可空列，语义也正好是"先来先导"。
                Sorting = "UploadedTime ASC",
                SkipCount = skip,
                MaxResultCount = _pageSize
            });

            if (page.Items.Count == 0)
            {
                break;
            }

            foreach (var row in page.Items)
            {
                if (row.Status is not (PhotoStatuses.Normal or PhotoStatuses.FallbackPending))
                {
                    // 待处理/失败的不进压缩包，但它们仍会出现在包内的台账清单里
                    continue;
                }

                rows.Add(row);

                if (rows.Count >= input.MaxCount)
                {
                    break;
                }
            }

            skip += page.Items.Count;

            if (skip >= page.TotalCount)
            {
                break;
            }
        }

        // 触顶只看"是否用满了上限"：用满就**不能**声称"已全部导出"，
        // 因为再往后还有没有可导出的照片，本轮没去数（要数它得全表扫一遍）。
        // 宁可保守地说"已达上限"，也不要谎报"就这些"。
        return (rows, rows.Count >= input.MaxCount);
    }

    /// <summary>
    /// 调用方是否显式筛了一个"必然没有可导出文件"的状态。
    ///
    /// 可导出的只有 <c>Normal</c> 与 <c>FallbackPending</c>（有原图或降级副本）；
    /// <c>Pending</c>（还没落盘）与 <c>Failed</c>（处理失败）没有可打包的文件。
    /// 空状态表示"不限"，由上面那条"跳过不可导出行"的逻辑处理。
    /// </summary>
    private static bool ExplicitlyNonExportable(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && status is not (PhotoStatuses.Normal or PhotoStatuses.FallbackPending);

    private async Task<string> LoadMissingListAsync(
        PhotoExportQueryDto input,
        CancellationToken cancellationToken)
    {
        var rows = new List<IEnumerable<string?>>
        {
            new[] { "学籍辅号", "姓名", "学校", "年级", "班级", "学籍状态" }
        };

        for (var skip = 0; skip < input.MaxCount; skip += _pageSize)
        {
            var page = await _photos.GetMissingPhotoListAsync(new MissingPhotoQueryDto
            {
                SchoolOrgId = input.SchoolOrgId,
                GradeId = input.MissingGradeId,
                ClassId = input.MissingClassId,
                Keyword = input.Keyword,
                SkipCount = skip,
                MaxResultCount = Math.Min(_pageSize, input.MaxCount - skip)
            });

            rows.AddRange(page.Items.Select(s => new[]
            {
                s.StudentNo, s.Name, s.SchoolName, s.GradeName, s.ClassName, s.StatusName ?? s.Status
            }));

            if (page.Items.Count < _pageSize || rows.Count - 1 >= page.TotalCount)
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
