using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Students;

/// <summary>
/// 学习简历（就读经历）：某段时间在某个学校（幼儿园/小学/初中…）就读，可含担任职务与证明人。
///
/// 表名：stu_resume
/// </summary>
public class StuResume : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>所属学生（stu_student.Id）。</summary>
    public Guid StudentId { get; set; }

    /// <summary>显示顺序（按时间倒序展示时用）。</summary>
    public int Seq { get; set; }

    /// <summary>起始日期。</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>结束日期（在读填 null）。</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>学校（园）名称。历史经历可能是外校，因此存名称而不是组织 Id。</summary>
    public string SchoolName { get; set; } = default!;

    /// <summary>年级名称（自由文本，如"三年级"）。</summary>
    public string? GradeName { get; set; }

    /// <summary>班级名称（自由文本，如"二班"）。</summary>
    public string? ClassName { get; set; }

    /// <summary>担任职务。</summary>
    public string? Post { get; set; }

    /// <summary>证明人。</summary>
    public string? Referee { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public StuResume()
    {
    }

    public StuResume(Guid id, Guid studentId, int seq, string schoolName) : base(id)
    {
        StudentId = studentId;
        Seq = seq;
        SchoolName = schoolName;
    }
}
