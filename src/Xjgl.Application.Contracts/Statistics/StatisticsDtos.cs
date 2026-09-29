using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Xjgl.Statistics;

/// <summary>统计结果的单个分组。</summary>
public class StatisticItemDto
{
    /// <summary>分组键（Id 或码值）。</summary>
    public string Key { get; set; } = default!;

    /// <summary>可直接展示的名称；**可能为 null**（码值维度由前端用码表翻译）。</summary>
    public string? Name { get; set; }

    public long Count { get; set; }
}

/// <summary>统计结果。</summary>
public class StatisticResultDto
{
    public string Dimension { get; set; } = default!;

    /// <summary>各分组计数之和。它**不是**"库里的总数" —— 分组被上限截断时它也只覆盖返回的分组。</summary>
    public long Total { get; set; }

    /// <summary>
    /// 是否因为分组数达到上限而被截断。
    ///
    /// 必须暴露出来：按学校统计时"只显示前 200 所"与"一共就 200 所"
    /// 对使用者是完全不同的两件事，而两者在界面上长得一模一样。
    /// </summary>
    public bool IsTruncated { get; set; }

    /// <summary>本系统在本次统计里用的数据范围（人类可读，便于排查"为什么数字比别人小"）。</summary>
    public string ScopeDescription { get; set; } = default!;

    public List<StatisticItemDto> Items { get; set; } = new();
}

/// <summary>学生统计的请求。</summary>
public class StudentStatisticsQueryDto
{
    /// <summary>统计维度，取值见 <see cref="StudentStatDimensions"/>。</summary>
    [Required]
    [StringLength(32)]
    public string Dimension { get; set; } = StudentStatDimensions.School;

    public Guid? SchoolOrgId { get; set; }

    public bool IncludeDescendantOrgs { get; set; }

    public string? AcademicYear { get; set; }

    public Guid? GradeId { get; set; }

    public Guid? ClassId { get; set; }

    public string? StudentStatusCode { get; set; }

    public string? GenderCode { get; set; }
}

/// <summary>异动统计的请求。</summary>
public class ChangeStatisticsQueryDto
{
    /// <summary>统计维度，取值见 <see cref="ChangeStatDimensions"/>。</summary>
    [Required]
    [StringLength(32)]
    public string Dimension { get; set; } = ChangeStatDimensions.ChangeType;

    public Guid? SchoolOrgId { get; set; }

    public bool IncludeDescendantOrgs { get; set; }

    public Guid? ChangeTypeId { get; set; }

    public string? Status { get; set; }

    /// <summary>申请日期下界（含）。</summary>
    public DateTime? From { get; set; }

    /// <summary>申请日期上界（含当天）。</summary>
    public DateTime? To { get; set; }
}

/// <summary>
/// 统计报表。
///
/// 与列表接口是**两套权限**（<c>Rpt.*</c>）：能看学生名单不等于能看全省汇总 ——
/// 汇总数字本身就是敏感信息（一个县的学生总数、异动趋势）。
/// 但两者共用同一套**数据范围**：统计绝不能比列表看到更多。
/// </summary>
public interface IStatisticsAppService : IApplicationService
{
    /// <summary>学生统计（按学校 / 年级 / 班级 / 学籍状态 / 性别 / 学年 / 有无照片）。</summary>
    Task<StatisticResultDto> GetStudentStatisticsAsync(StudentStatisticsQueryDto input);

    /// <summary>异动统计（按异动类型 / 审批状态 / 月份 / 转出校 / 转入校）。</summary>
    Task<StatisticResultDto> GetChangeStatisticsAsync(ChangeStatisticsQueryDto input);
}
