using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Organizations;

/// <summary>
/// 年级（学年维度）。
///
/// ★ 为什么年级不在组织树里：
///   组织树（SysOrg）是权限与数据范围的锚点，必须稳定；
///   而年级每年升级、班级每年重分。若把年级班级放进组织树，
///   每年都要重构组织树与历史权限，权限树也会膨胀到几十万个节点。
///   因此年级班级单独建模，挂在「学校 + 学年」下。
///
/// 表名：sys_grade
/// </summary>
public class SysGrade : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>所属学校（SysOrg 中 OrgType=School 的节点）。</summary>
    public Guid SchoolOrgId { get; set; }

    /// <summary>学年，格式 2025-2026。</summary>
    public string AcademicYear { get; set; } = default!;

    /// <summary>年级码，如 01 / 07 / 10。</summary>
    public string GradeCode { get; set; } = default!;

    /// <summary>年级名称，如「一年级」「高一」。</summary>
    public string GradeName { get; set; } = default!;

    /// <summary>年级组长（用户 Id）。</summary>
    public Guid? HeadTeacherUserId { get; set; }

    /// <summary>排序号。</summary>
    public int Sort { get; set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public SysGrade()
    {
    }

    public SysGrade(
        Guid id,
        Guid schoolOrgId,
        string academicYear,
        string gradeCode,
        string gradeName,
        int sort = 0) : base(id)
    {
        SchoolOrgId = schoolOrgId;
        AcademicYear = academicYear;
        GradeCode = gradeCode;
        GradeName = gradeName;
        Sort = sort;
    }
}
