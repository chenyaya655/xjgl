using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Students;

/// <summary>
/// 学生监护人。一个学生最多可以有多个监护人（父母、祖父母…），用 <see cref="Seq"/> 排序。
///
/// ★ 监护人的身份证号同样按「密文 + 掩码」处理：家长的身份信息与学生一样属于个人敏感信息，
///   不能因为"它在子表里"就明文落库。
///
/// 表名：stu_guardian
/// </summary>
public class StuGuardian : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>所属学生（stu_student.Id）。</summary>
    public Guid StudentId { get; set; }

    /// <summary>显示顺序（第一监护人为 1）。</summary>
    public int Seq { get; set; }

    /// <summary>姓名。</summary>
    public string Name { get; set; } = default!;

    /// <summary>与学生关系码（微代码 GUARDIAN_RELATION）。</summary>
    public string? RelationCode { get; set; }

    /// <summary>身份证件号码密文（AES）。</summary>
    public string? IdCardEncrypted { get; set; }

    /// <summary>联系电话。</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>工作单位。</summary>
    public string? WorkUnit { get; set; }

    /// <summary>现住址。</summary>
    public string? Address { get; set; }

    /// <summary>是否第一监护人（联系学生时的首选联系人）。</summary>
    public bool IsPrimary { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public StuGuardian()
    {
    }

    public StuGuardian(Guid id, Guid studentId, int seq, string name) : base(id)
    {
        StudentId = studentId;
        Seq = seq;
        Name = name;
    }
}
