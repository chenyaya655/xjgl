using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Dtos;

namespace Xjgl.Students;

/// <summary>学生列表项（轻量，用于列表页）。</summary>
public class StudentListItemDto : EntityDto<Guid>
{
    public string StudentNo { get; set; } = default!;
    public string? NationalStudentNo { get; set; }
    public string Name { get; set; } = default!;

    /// <summary>
    /// 是否含生僻字（前端据此启用备用字体）。
    ///
    /// ⚠ **不要**用"生成生僻字图片"兜底（已明确否决）：那等于把文字变成位图，
    ///   从此不可检索、不可复制、不可比对，而且每遇到一个陌生字都要先有一张图。
    ///   方向是在**编码层**解决（按 Unicode 码点处理 + 用字体覆盖码点），
    ///   见 <c>RareCharacterText</c> 的说明。
    /// </summary>
    public bool HasRareCharacter { get; set; }

    public string GenderCode { get; set; } = default!;
    public string? GenderName { get; set; }
    public DateTime BirthDate { get; set; }
    public int Age { get; set; }
    public Guid SchoolOrgId { get; set; }
    public string? SchoolName { get; set; }
    public string AcademicYear { get; set; } = default!;
    public Guid? GradeId { get; set; }
    public string? GradeName { get; set; }
    public Guid? ClassId { get; set; }
    public string? ClassName { get; set; }
    public string StudentStatusCode { get; set; } = default!;
    public string? StudentStatusName { get; set; }

    /// <summary>脱敏后的身份证号（保留前 6 后 4）。</summary>
    public string? MaskedIdCard { get; set; }

    public bool HasPhoto { get; set; }
}

/// <summary>学生详情。</summary>
public class StudentDto : StudentListItemDto
{
    public string? FormerName { get; set; }
    public string? Pinyin { get; set; }
    public string IdCardTypeCode { get; set; } = default!;
    public string? NationCode { get; set; }
    public string? CountryCode { get; set; }
    public string? PoliticalCode { get; set; }
    public string? NativePlace { get; set; }
    public string? BirthPlace { get; set; }
    public string? HouseholdNatureCode { get; set; }
    public string? HouseholdAddress { get; set; }
    public string? HomeAddress { get; set; }
    public string? CurrentAddress { get; set; }
    public string? PhoneNumber { get; set; }
    public string? PostalCode { get; set; }
    public string? Email { get; set; }
    public DateTime? EnrollDate { get; set; }
    public string? EnrollTypeCode { get; set; }
    public string? StudyModeCode { get; set; }
    public string? SchoolLengthCode { get; set; }
    public bool IsMigrantChild { get; set; }
    public string? MigrantTypeCode { get; set; }
    public bool IsLeftBehindChild { get; set; }
    public string? LeftBehindTypeCode { get; set; }
    public bool IsPoor { get; set; }
    public bool IsDisabled { get; set; }
    public string? DisabilityCode { get; set; }
    public string? HealthCode { get; set; }
    public string? PhotoPath { get; set; }
    public string? Remark { get; set; }
}

/// <summary>新增 / 修改学生。</summary>
public class CreateUpdateStudentDto
{
    [Required(ErrorMessage = "姓名不能为空")]
    [StringLength(60)]
    public string Name { get; set; } = default!;

    [StringLength(60)]
    public string? FormerName { get; set; }

    [Required(ErrorMessage = "性别不能为空")]
    [StringLength(4)]
    public string GenderCode { get; set; } = default!;

    [Required(ErrorMessage = "出生日期不能为空")]
    public DateTime BirthDate { get; set; }

    [Required]
    [StringLength(4)]
    public string IdCardTypeCode { get; set; } = "1";

    [Required(ErrorMessage = "身份证件号码不能为空")]
    [StringLength(18)]
    public string IdCardNumber { get; set; } = default!;

    [Required]
    public Guid SchoolOrgId { get; set; }

    [Required]
    [RegularExpression(@"^(\d{4}|\d{4}-\d{4})$", ErrorMessage = "学年格式应为 2025-2026")]
    public string AcademicYear { get; set; } = default!;

    public Guid? GradeId { get; set; }
    public Guid? ClassId { get; set; }

    [StringLength(512)]
    public string? ClassStudentNo { get; set; }

    [StringLength(8)]
    public string? NationCode { get; set; }

    [StringLength(8)]
    public string? CountryCode { get; set; }

    [StringLength(8)]
    public string? PoliticalCode { get; set; }

    [StringLength(128)]
    public string? NativePlace { get; set; }

    [StringLength(128)]
    public string? BirthPlace { get; set; }

    [StringLength(8)]
    public string? HouseholdNatureCode { get; set; }

    [StringLength(512)]
    public string? HouseholdAddress { get; set; }

    [StringLength(512)]
    public string? HomeAddress { get; set; }

    [StringLength(512)]
    public string? CurrentAddress { get; set; }

    [StringLength(20)]
    public string? PhoneNumber { get; set; }

    [StringLength(8)]
    public string? PostalCode { get; set; }

    [StringLength(128)]
    public string? Email { get; set; }

    public DateTime? EnrollDate { get; set; }

    [StringLength(8)]
    public string? EnrollTypeCode { get; set; }

    [StringLength(8)]
    public string? StudyModeCode { get; set; }

    [StringLength(8)]
    public string? SchoolLengthCode { get; set; }

    [StringLength(8)]
    public string? StudentStatusCode { get; set; }

    public bool IsMigrantChild { get; set; }

    /// <summary>
    /// 随迁子女类型码。
    ///
    /// 【为什么它一直写不进来】
    /// 详情 DTO（<see cref="StudentDto"/>）与实体（<c>StuStudent.MigrantTypeCode</c>）
    /// 都有这个字段，但新增/修改 DTO 漏了它 —— 于是"随迁类型"这一栏
    /// **任何接口都写不进去**，只在导出/详情里回读得到（恒为 null）。
    /// 同类问题还有下面的留守儿童类型码。
    /// </summary>
    [StringLength(8)]
    public string? MigrantTypeCode { get; set; }

    public bool IsLeftBehindChild { get; set; }

    /// <summary>留守儿童类型码（同上：此前新增/修改 DTO 缺失，写不进来）。</summary>
    [StringLength(8)]
    public string? LeftBehindTypeCode { get; set; }

    public bool IsPoor { get; set; }
    public bool IsDisabled { get; set; }

    [StringLength(8)]
    public string? DisabilityCode { get; set; }

    [StringLength(8)]
    public string? HealthCode { get; set; }

    [StringLength(512)]
    public string? Remark { get; set; }
}

/// <summary>学生查询条件。</summary>
public class StudentQueryDto : PagedAndSortedResultRequestDto
{
    public Guid? SchoolOrgId { get; set; }

    /// <summary>按组织子树查询（配合 SysOrg 的物化路径）。</summary>
    public bool IncludeDescendantOrgs { get; set; }

    public string? AcademicYear { get; set; }
    public Guid? GradeId { get; set; }
    public Guid? ClassId { get; set; }
    public string? StudentStatusCode { get; set; }
    public string? GenderCode { get; set; }

    /// <summary>姓名 / 学籍号 / 身份证号 关键字。</summary>
    public string? Keyword { get; set; }
}

/// <summary>重复学籍检测请求。</summary>
public class CheckStudentDuplicateDto
{
    [Required]
    public string Name { get; set; } = default!;

    [Required]
    public string IdCardNumber { get; set; } = default!;

    public DateTime? BirthDate { get; set; }

    /// <summary>排除掉的学生 Id（修改时排除自己）。</summary>
    public Guid? ExcludeId { get; set; }
}

/// <summary>重复学籍检测结果。</summary>
public class StudentDuplicateCheckResultDto
{
    /// <summary>是否存在重复。</summary>
    public bool HasDuplicate { get; set; }

    /// <summary>重复原因描述。</summary>
    public List<string> Reasons { get; set; } = new();

    /// <summary>命中的已有学生（可能跨校、跨区）。</summary>
    public List<StudentListItemDto> Matched { get; set; } = new();
}
