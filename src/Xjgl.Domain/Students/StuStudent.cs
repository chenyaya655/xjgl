using System.Text;
using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Students;

/// <summary>学籍状态码常量（与微代码 STUDENT_STATUS 的码值对应）。</summary>
public static class StudentStatuses
{
    public const string Studying = "01";
    public const string Suspended = "02";
    public const string Resumed = "03";
    public const string TransferredOut = "04";
    public const string TransferredIn = "05";
    public const string Graduated = "06";
    public const string Completed = "07";
    public const string DroppedOut = "08";
    public const string Suspended2 = "09";
    public const string Expelled = "10";
    public const string Deceased = "11";
    public const string Other = "12";

    /// <summary>是否属于「在校」状态（统计在校生数时使用）。</summary>
    public static bool IsInSchool(string status)
    {
        return status is Studying or Resumed or TransferredIn;
    }
}

/// <summary>
/// 学生学籍主档。
///
/// 字段按《教育管理信息 普通中小学校管理信息》的学生基本数据集组织，
/// 码值类字段统一引用微代码（性别码、民族码、学籍状态码…），不写死枚举。
///
/// ★ 身份证号双字段：IdCardEncrypted（AES 密文）+ IdCardHash（HMAC-SHA256），
///   后者用于唯一索引与跨校查重，前者受权限控制才能查看。
///
/// 表名：stu_student
/// </summary>
public class StuStudent : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    // ---- 标识 ----

    /// <summary>全国学籍号（G + 身份证号，19 位）。由全国系统分配，本系统只存储不生成。</summary>
    public string? NationalStudentNo { get; set; }

    /// <summary>学籍辅号（省级/校级），由本系统按规则生成，如 学校标识码(10)+流水号(6)。</summary>
    public string StudentNo { get; set; } = default!;

    /// <summary>班内学号。</summary>
    public string? ClassStudentNo { get; set; }

    /// <summary>姓名（支持生僻字，长度校验必须按 Unicode 码点）。</summary>
    public string Name { get; set; } = default!;

    /// <summary>曾用名。</summary>
    public string? FormerName { get; set; }

    /// <summary>姓名拼音（生僻字可能为空，检索时姓名按原样匹配）。</summary>
    public string? Pinyin { get; set; }

    // ---- 身份 ----

    /// <summary>性别码（微代码 SEX）。</summary>
    public string GenderCode { get; set; } = default!;

    /// <summary>出生日期。</summary>
    public DateTime BirthDate { get; set; }

    /// <summary>身份证件类型码（微代码 ID_TYPE）。</summary>
    public string IdCardTypeCode { get; set; } = default!;

    /// <summary>身份证件号码密文（AES）。</summary>
    public string? IdCardEncrypted { get; set; }

    /// <summary>身份证件号码摘要（HMAC-SHA256），用于唯一索引与查重。</summary>
    public string? IdCardHash { get; set; }

    /// <summary>民族码（微代码 NATION）。</summary>
    public string? NationCode { get; set; }

    /// <summary>国籍/地区码（微代码 COUNTRY）。</summary>
    public string? CountryCode { get; set; }

    /// <summary>政治面貌码（微代码 POLITICAL）。</summary>
    public string? PoliticalCode { get; set; }

    /// <summary>籍贯。</summary>
    public string? NativePlace { get; set; }

    /// <summary>出生地。</summary>
    public string? BirthPlace { get; set; }

    /// <summary>户口性质码（微代码 HOUSEHOLD）。</summary>
    public string? HouseholdNatureCode { get; set; }

    /// <summary>户口所在地。</summary>
    public string? HouseholdAddress { get; set; }

    // ---- 联系 ----

    /// <summary>家庭住址。</summary>
    public string? HomeAddress { get; set; }

    /// <summary>现居住地。</summary>
    public string? CurrentAddress { get; set; }

    /// <summary>联系电话。</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>邮政编码。</summary>
    public string? PostalCode { get; set; }

    /// <summary>电子邮箱。</summary>
    public string? Email { get; set; }

    // ---- 就学 ----

    /// <summary>所属学校（SysOrg 中 OrgType=School）。</summary>
    public Guid SchoolOrgId { get; set; }

    /// <summary>学校物化路径（冗余，便于按数据范围前缀过滤）。</summary>
    public string? SchoolPathCode { get; set; }

    /// <summary>学年，格式 2025-2026。</summary>
    public string AcademicYear { get; set; } = default!;

    /// <summary>年级（SysGrade）。</summary>
    public Guid? GradeId { get; set; }

    /// <summary>班级（SysClass）。</summary>
    public Guid? ClassId { get; set; }

    /// <summary>入学年月。</summary>
    public DateTime? EnrollDate { get; set; }

    /// <summary>入学方式码（微代码 ENROLL_TYPE）。</summary>
    public string? EnrollTypeCode { get; set; }

    /// <summary>就读方式码（微代码 STUDY_MODE，走读/住校）。</summary>
    public string? StudyModeCode { get; set; }

    /// <summary>学制码（微代码 SCHOOL_LENGTH）。</summary>
    public string? SchoolLengthCode { get; set; }

    /// <summary>学籍状态码（微代码 STUDENT_STATUS）。</summary>
    public string StudentStatusCode { get; set; } = StudentStatuses.Studying;

    // ---- 学生属性 ----

    /// <summary>是否随迁子女。</summary>
    public bool IsMigrantChild { get; set; }

    /// <summary>随迁子女类型码（微代码 MIGRANT）。</summary>
    public string? MigrantTypeCode { get; set; }

    /// <summary>是否留守儿童。</summary>
    public bool IsLeftBehindChild { get; set; }

    /// <summary>留守儿童类型码（微代码 LEFT_BEHIND）。</summary>
    public string? LeftBehindTypeCode { get; set; }

    /// <summary>是否家庭经济困难。</summary>
    public bool IsPoor { get; set; }

    /// <summary>是否残疾。</summary>
    public bool IsDisabled { get; set; }

    /// <summary>残疾类别码（微代码 DISABILITY）。</summary>
    public string? DisabilityCode { get; set; }

    /// <summary>健康状况码（微代码 HEALTH）。</summary>
    public string? HealthCode { get; set; }

    // ---- 其它 ----

    /// <summary>照片相对路径（存 NAS 上的路径，不存二进制）。</summary>
    public string? PhotoPath { get; set; }

    /// <summary>来源系统（对接外部系统时使用）。</summary>
    public string? SourceSystem { get; set; }

    /// <summary>外部主键（对接全国学籍系统时使用）。</summary>
    public string? ExternalKey { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public StuStudent()
    {
    }

    public StuStudent(
        Guid id,
        string studentNo,
        string name,
        string genderCode,
        DateTime birthDate,
        Guid schoolOrgId,
        string academicYear) : base(id)
    {
        StudentNo = studentNo;
        Name = name;
        GenderCode = genderCode;
        BirthDate = birthDate;
        SchoolOrgId = schoolOrgId;
        AcademicYear = academicYear;
        IdCardTypeCode = "1";
        StudentStatusCode = StudentStatuses.Studying;
    }

    /// <summary>获取脱敏后的姓名（保留首字，其余用 * 代替；生僻字按码点处理）。</summary>
    public string GetMaskedName()
    {
        var runes = Name.EnumerateRunes().ToList();

        if (runes.Count <= 1)
        {
            return Name;
        }

        return string.Concat(runes[0].ToString(), new string('*', runes.Count - 1));
    }
}
