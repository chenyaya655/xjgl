using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Organizations;

/// <summary>
/// 班级（学年维度）。
///
/// 表名：sys_class
/// 学生最终挂在班级上，因此这里是「学籍归属」的最细粒度。
/// </summary>
public class SysClass : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>所属学校。</summary>
    public Guid SchoolOrgId { get; set; }

    /// <summary>学年，格式 2025-2026（冗余自年级，便于按学年直接查询班级）。</summary>
    public string AcademicYear { get; set; } = default!;

    /// <summary>所属年级。</summary>
    public Guid GradeId { get; set; }

    /// <summary>班号，如 01。</summary>
    public string ClassNo { get; set; } = default!;

    /// <summary>班级名称，如「一年级1班」。</summary>
    public string ClassName { get; set; } = default!;

    /// <summary>班主任（用户 Id）。</summary>
    public Guid? HeadTeacherUserId { get; set; }

    /// <summary>教室。</summary>
    public string? Classroom { get; set; }

    /// <summary>排序号。</summary>
    public int Sort { get; set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public SysClass()
    {
    }

    public SysClass(
        Guid id,
        Guid schoolOrgId,
        string academicYear,
        Guid gradeId,
        string classNo,
        string className,
        int sort = 0) : base(id)
    {
        SchoolOrgId = schoolOrgId;
        AcademicYear = academicYear;
        GradeId = gradeId;
        ClassNo = classNo;
        ClassName = className;
        Sort = sort;
    }
}
