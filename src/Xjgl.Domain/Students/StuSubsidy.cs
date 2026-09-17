using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Students;

/// <summary>
/// 学生资助记录（免学费、助学金、生活补助、社会资助…）。
///
/// 金额用 decimal(18,2) 存，不用 float：资助金额要参与汇总核算，
/// 浮点误差在"分"这个量级上会直接变成对不上账。
///
/// 表名：stu_subsidy
/// </summary>
public class StuSubsidy : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>所属学生（stu_student.Id）。</summary>
    public Guid StudentId { get; set; }

    /// <summary>显示顺序。</summary>
    public int Seq { get; set; }

    /// <summary>学年，格式 2025-2026。</summary>
    public string AcademicYear { get; set; } = default!;

    /// <summary>资助类型码（微代码 SUBSIDY_TYPE）。</summary>
    public string? SubsidyTypeCode { get; set; }

    /// <summary>资助项目名称。</summary>
    public string SubsidyName { get; set; } = default!;

    /// <summary>资助金额（元）。</summary>
    public decimal? Amount { get; set; }

    /// <summary>发放日期。</summary>
    public DateTime? SubsidyDate { get; set; }

    /// <summary>资助方（政府部门 / 学校 / 社会机构）。</summary>
    public string? FundingOrg { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public StuSubsidy()
    {
    }

    public StuSubsidy(Guid id, Guid studentId, int seq, string academicYear, string subsidyName) : base(id)
    {
        StudentId = studentId;
        Seq = seq;
        AcademicYear = academicYear;
        SubsidyName = subsidyName;
    }
}
