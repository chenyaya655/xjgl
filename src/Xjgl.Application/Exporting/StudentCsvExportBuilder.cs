using System.Globalization;
using Microsoft.Extensions.Configuration;
using Volo.Abp.DependencyInjection;
using Xjgl.Students;
using Xjgl.Text;

namespace Xjgl.Exporting;

/// <summary>学生名单导出的结果（含"有没有被截断"的如实交代）。</summary>
/// <param name="Csv">CSV 文本。</param>
/// <param name="Total">符合条件的学生总数。</param>
/// <param name="RowCount">实际写出的行数。</param>
/// <param name="IsTruncated">是否因上限被截断。</param>
public sealed record StudentCsvExportResult(string Csv, long Total, int RowCount, bool IsTruncated);

/// <summary>
/// 学生名单 CSV 的组装。
///
/// 【为什么从 StudentImportExportAppService 挪出来】
///   那个方法原本是"带查询串直接下载"（<c>GET .../export</c>）。改成任务式之后，
///   这个 HTTP 出口必须消失（否则新旧两条路并存，而新的那条没人走），
///   但**查询逻辑不能重写** —— 它带了数据范围约束与码值翻译，
///   另写一套极易出现"界面看不到、导出却拿得到"的越权。
///   所以是整体搬移 + 改上限与截断交代，查询本身一行没动。
///
/// 【债务 24：这里的上限以前是硬编码 5000 且超限静默截断】
///   原来超了 5000 行就只取前 5000 行，产出物里**没有任何痕迹** ——
///   用户拿到一份少了 8000 人的名单，还以为"全校就这么多"。
///   对学籍系统来说，"少人"是最不能接受的失败方式（这一条在照片导出里
///   已经踩过一次，见 PhotoExportService 的注释）。
///   现在：上限可配置，且**无论截断与否都把 Total 与实际行数交出去**，
///   由调用方写进任务的 Message 里让用户看见。
/// </summary>
public interface IStudentCsvExportBuilder
{
    Task<StudentCsvExportResult> BuildAsync(
        StudentQueryDto input,
        CancellationToken cancellationToken = default);
}

public class StudentCsvExportBuilder : IStudentCsvExportBuilder, ITransientDependency
{
    /// <summary>
    /// 导出上限的配置键。默认 10 万行。
    ///
    /// 默认值不再取 5000：任务式之后没有请求超时压力，限制的意义从
    /// "别把请求拖死"变成了"别把内存打爆"。10 万行 CSV 约 15MB，
    /// 而"一次导出全校/全县"在教学管理里是常规操作。
    /// </summary>
    private const string MaxRowsConfigKey = "Xjgl:Export:MaxRows";

    private const int DefaultMaxRows = 100_000;

    /// <summary>
    /// 单页取数上限，可配置。
    ///
    /// **ABP 对 `MaxResultCount` 有 1000 的硬上限**（默认值），传更大值会被参数校验
    /// 直接拒掉（AbpValidationException），因此只能分页累积，不能"一次要 10 万条"。
    /// 默认值就是那个硬上限，生产上不需要改。
    ///
    /// 【为什么做成可配置】为了**可测试**。这里唯一的复杂度就是"每页之后上报一次进度"
    /// （债 23），而要跨页就得让结果集超过一页 —— 用 1000 的页大小意味着集成测试
    /// 必须往共享测试库里插 1000+ 名学生（实测 30 秒上下，还每跑一次多留一批垃圾），
    /// 久了就没人愿意跑全量测试了。把页大小调小，7 条数据就能走完同一个循环体。
    /// 这与 `PhotoExportService` 的 `Xjgl:Photo:ExportPageSize` 是同一条取舍，
    /// 刻意保持一致：**两个分页导出都靠"把页大小调小"来覆盖翻页路径**。
    /// </summary>
    private const string PageSizeConfigKey = "Xjgl:Export:StudentCsvPageSize";

    private const int DefaultPageSize = 1000;

    private readonly IStudentAppService _students;
    private readonly IConfiguration _configuration;

    public StudentCsvExportBuilder(IStudentAppService students, IConfiguration configuration)
    {
        _students = students;
        _configuration = configuration;
    }

    public async Task<StudentCsvExportResult> BuildAsync(
        StudentQueryDto input,
        CancellationToken cancellationToken = default)
    {
        var maxRows = Math.Max(1, _configuration.GetValue(MaxRowsConfigKey, DefaultMaxRows));

        // 兜底并钳到 [1, 1000]：配成 0 或负数会让循环空转（表现为"导出来是空的"），
        // 配成 > 1000 会被 ABP 拒绝（同样的表现）。两种错法都很难往"页大小"上想，
        // 所以在入口一次性收口。与 PhotoExportService 的处理一致。
        var configuredPageSize = _configuration.GetValue<int?>(PageSizeConfigKey) ?? DefaultPageSize;

        var pageSize = Math.Clamp(configuredPageSize < 1 ? DefaultPageSize : configuredPageSize, 1, 1000);

        StudentQueryDto BuildQuery(int skipCount, int take) => new()
        {
            SchoolOrgId = input.SchoolOrgId,
            IncludeDescendantOrgs = input.IncludeDescendantOrgs,
            AcademicYear = input.AcademicYear,
            GradeId = input.GradeId,
            ClassId = input.ClassId,
            StudentStatusCode = input.StudentStatusCode,
            GenderCode = input.GenderCode,
            Keyword = input.Keyword,
            Sorting = input.Sorting ?? "StudentNo ASC",
            SkipCount = skipCount,
            MaxResultCount = take
        };

        // 复用列表查询：它已经带了数据范围约束与码值翻译。
        var students = new List<StudentListItemDto>();
        long total = 0;

        for (var skip = 0; skip < maxRows; skip += pageSize)
        {
            // 最后一页只取到上限为止，避免实际行数超过配置的上限
            var take = Math.Min(pageSize, maxRows - students.Count);

            if (take <= 0)
            {
                break;
            }

            var page = await _students.GetListAsync(BuildQuery(skip, take));

            total = page.TotalCount;
            students.AddRange(page.Items);

            // ★ 债 23：每取完一页就把"已经攒到多少"上报一次，轮询才看得到百分比。
            //
            // 为什么要**单独算一个 progressTotal**而不是直接把 `total`（符合条件的学生总数）
            // 报上去：这一种会被 `MaxRows` 截断，而截断时 total 会**大于**最终产出的行数。
            // 报原值的话，截断场景下"已处理"会永远追不上总数，界面上永远差那么几个百分点，
            // 用户会以为任务卡住了 —— 而它其实已经全部产出完了。
            //
            // 口径：**进度以"本次真的会写出去的行数"为分母**（= min(总数, 上限)）。
            // 于是它天然满足 `Processed <= Total`，跑完时 `Processed == RowCount` 也成立。
            var progressTotal = Math.Min(total, maxRows);

            await ExportProgress.ReportAsync(students.Count, progressTotal, cancellationToken);

            if (page.Items.Count < take || students.Count >= total)
            {
                break;
            }
        }

        var rows = new List<IEnumerable<string?>>
        {
            new[]
            {
                "学籍辅号", "姓名", "性别", "出生日期", "身份证号", "学校",
                "学年", "年级", "班级", "学籍状态", "是否有照片", "含生僻字"
            }
        };

        rows.AddRange(students.Select(s => new[]
        {
            s.StudentNo,
            s.Name,
            s.GenderName ?? s.GenderCode,
            s.BirthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            s.MaskedIdCard,
            s.SchoolName,
            s.AcademicYear,
            s.GradeName,
            s.ClassName,
            s.StudentStatusName ?? s.StudentStatusCode,
            s.HasPhoto ? "是" : "否",
            s.HasRareCharacter ? "是" : "否"
        }));

        return new StudentCsvExportResult(
            Csv.WriteForExcel(rows),
            total,
            students.Count,
            IsTruncated: students.Count < total);
    }
}
