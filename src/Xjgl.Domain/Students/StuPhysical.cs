using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Students;

/// <summary>
/// 学生体质健康（体检）记录。**按学年**记录，一个学年一条。
///
/// 身高/体重/视力用 decimal 而不是 double：这些值要按学段做均值统计，
/// 也要求"两次记录之间涨了多少"能精确比较。
///
/// 表名：stu_physical
/// </summary>
public class StuPhysical : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>所属学生（stu_student.Id）。</summary>
    public Guid StudentId { get; set; }

    /// <summary>学年，格式 2025-2026（同一学生同一学年应只有一条）。</summary>
    public string AcademicYear { get; set; } = default!;

    /// <summary>体检日期。</summary>
    public DateTime? CheckDate { get; set; }

    /// <summary>身高（厘米）。</summary>
    public decimal? Height { get; set; }

    /// <summary>体重（千克）。</summary>
    public decimal? Weight { get; set; }

    /// <summary>左眼裸眼视力。</summary>
    public decimal? LeftVision { get; set; }

    /// <summary>右眼裸眼视力。</summary>
    public decimal? RightVision { get; set; }

    /// <summary>健康状况码（微代码 HEALTH）。</summary>
    public string? HealthCode { get; set; }

    /// <summary>医生建议。</summary>
    public string? DoctorAdvice { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public StuPhysical()
    {
    }

    public StuPhysical(Guid id, Guid studentId, string academicYear) : base(id)
    {
        StudentId = studentId;
        AcademicYear = academicYear;
    }
}
