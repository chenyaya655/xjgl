using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Dtos;

namespace Xjgl.Students;

/// <summary>花名册的查询条件（与列表页的筛选口径保持一致）。</summary>
public class StudentRosterQueryDto : PagedAndSortedResultRequestDto
{
    /// <summary>学校（不传则按当前用户的数据范围）。</summary>
    public Guid? SchoolOrgId { get; set; }

    /// <summary>是否包含下级组织（默认 false，只看本级学校）。</summary>
    public bool IncludeDescendantOrgs { get; set; }

    public string? AcademicYear { get; set; }
    public Guid? GradeId { get; set; }
    public Guid? ClassId { get; set; }

    /// <summary>学籍状态码（不传=全部；默认由前端传「在读」等具体码值）。</summary>
    [StringLength(8)]
    public string? StudentStatusCode { get; set; }

    [StringLength(64)]
    public string? Keyword { get; set; }

    /// <summary>
    /// 姓名是否脱敏（保留姓氏，其余 *）。
    ///
    /// 对外提供名单（如报送给其它部门）时应当脱敏；校内使用则不脱敏。
    /// 默认 false：校内打印的场景更多。
    /// </summary>
    public bool MaskNames { get; set; }

    /// <summary>最大行数（防止一次打印几万行把服务器拖死）。默认 2000，上限 10000。</summary>
    [Range(1, 10000)]
    public int MaxRows { get; set; } = 2000;
}

/// <summary>打印/导出的文件。</summary>
public sealed class PrintFileResult
{
    public PrintFileResult(byte[] content, string fileName, string contentType = "application/pdf", int? rowCount = null)
    {
        Content = content;
        FileName = fileName;
        ContentType = contentType;
        RowCount = rowCount;
    }

    public byte[] Content { get; }

    public string FileName { get; }

    public string ContentType { get; }

    /// <summary>花名册包含的行数（学籍卡为 null）。前端用它提示"共 N 人"。</summary>
    public int? RowCount { get; }
}

/// <summary>
/// 学籍卡与花名册的打印。
///
/// **不是应用服务（自动 API 控制器）**：它的产物是**文件**而不是 JSON，
/// 因此由 <c>Xjgl.HttpApi</c> 里的控制器显式接管路由与响应头
/// （自动 API 会把 byte[] 序列化成 base64 塞进 JSON，几 MB 的 PDF 变成十几 MB 的字符串，
/// 浏览器也没法走"另存为"）。这也正是它不实现 <c>IApplicationService</c> 的原因。
/// </summary>
public interface IStudentPrintService
{
    /// <summary>渲染一名学生的学籍卡。</summary>
    Task<PrintFileResult> RenderStudentCardAsync(Guid studentId, CancellationToken cancellationToken = default);

    /// <summary>按条件渲染花名册。</summary>
    Task<PrintFileResult> RenderRosterAsync(StudentRosterQueryDto input, CancellationToken cancellationToken = default);
}
