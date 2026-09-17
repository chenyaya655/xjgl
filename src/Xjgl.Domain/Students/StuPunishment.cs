using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Students;

/// <summary>
/// 学生处分记录。
///
/// ★ 纪律处分是**可以撤销**的（申诉成功、表现良好提前解除），
///   所以采用「撤销标记 + 撤销日期 + 撤销原因」而不是直接删行：
///   直接删除会丢掉"曾经有过处分"这段事实，而这段事实在学籍档案里必须留痕。
///
/// 表名：stu_punishment
/// </summary>
public class StuPunishment : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>所属学生（stu_student.Id）。</summary>
    public Guid StudentId { get; set; }

    /// <summary>显示顺序。</summary>
    public int Seq { get; set; }

    /// <summary>处分日期。</summary>
    public DateTime PunishDate { get; set; }

    /// <summary>处分名称（如"警告"）。</summary>
    public string PunishName { get; set; } = default!;

    /// <summary>处分类型码（微代码 PUNISH_TYPE）。</summary>
    public string? PunishTypeCode { get; set; }

    /// <summary>处分决定单位。</summary>
    public string? PunishOrg { get; set; }

    /// <summary>是否已撤销。</summary>
    public bool IsRevoked { get; set; }

    /// <summary>撤销日期。</summary>
    public DateTime? RevokeDate { get; set; }

    /// <summary>撤销原因。</summary>
    public string? RevokeReason { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public StuPunishment()
    {
    }

    public StuPunishment(Guid id, Guid studentId, int seq, DateTime punishDate, string punishName) : base(id)
    {
        StudentId = studentId;
        Seq = seq;
        PunishDate = punishDate;
        PunishName = punishName;
    }
}
