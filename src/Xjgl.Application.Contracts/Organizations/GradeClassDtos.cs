using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Dtos;

namespace Xjgl.Organizations;

/// <summary>年级 DTO。</summary>
public class GradeDto : EntityDto<Guid>
{
    public Guid SchoolOrgId { get; set; }
    public string AcademicYear { get; set; } = default!;
    public string GradeCode { get; set; } = default!;
    public string GradeName { get; set; } = default!;
    public Guid? HeadTeacherUserId { get; set; }
    public int Sort { get; set; }
    public bool IsEnabled { get; set; }
    public string? Remark { get; set; }

    /// <summary>该年级下的班级数。</summary>
    public int ClassCount { get; set; }
}

/// <summary>班级 DTO。</summary>
public class ClassDto : EntityDto<Guid>
{
    public Guid SchoolOrgId { get; set; }
    public string AcademicYear { get; set; } = default!;
    public Guid GradeId { get; set; }
    public string GradeName { get; set; } = default!;
    public string ClassNo { get; set; } = default!;
    public string ClassName { get; set; } = default!;
    public Guid? HeadTeacherUserId { get; set; }
    public string? Classroom { get; set; }
    public int Sort { get; set; }
    public bool IsEnabled { get; set; }
}

/// <summary>新增 / 修改年级。</summary>
public class CreateUpdateGradeDto
{
    [Required]
    public Guid SchoolOrgId { get; set; }

    [Required(ErrorMessage = "学年不能为空")]
    [RegularExpression(@"^(\d{4}|\d{4}-\d{4})$", ErrorMessage = "学年格式应为 2025-2026（也可只填起始年 2025）")]
    public string AcademicYear { get; set; } = default!;

    [Required(ErrorMessage = "年级码不能为空")]
    [StringLength(8)]
    public string GradeCode { get; set; } = default!;

    [Required(ErrorMessage = "年级名称不能为空")]
    [StringLength(32)]
    public string GradeName { get; set; } = default!;

    public Guid? HeadTeacherUserId { get; set; }

    public int Sort { get; set; }

    public bool IsEnabled { get; set; } = true;

    [StringLength(512)]
    public string? Remark { get; set; }
}

/// <summary>新增 / 修改班级。</summary>
public class CreateUpdateClassDto
{
    [Required]
    public Guid GradeId { get; set; }

    [Required(ErrorMessage = "班号不能为空")]
    [StringLength(8)]
    public string ClassNo { get; set; } = default!;

    [Required(ErrorMessage = "班级名称不能为空")]
    [StringLength(64)]
    public string ClassName { get; set; } = default!;

    public Guid? HeadTeacherUserId { get; set; }

    [StringLength(64)]
    public string? Classroom { get; set; }

    public int Sort { get; set; }

    public bool IsEnabled { get; set; } = true;
}

/// <summary>按学年批量生成年级。</summary>
public class BatchGenerateGradesDto
{
    [Required]
    public Guid SchoolOrgId { get; set; }

    [Required]
    [RegularExpression(@"^(\d{4}|\d{4}-\d{4})$", ErrorMessage = "学年格式应为 2025-2026（也可只填起始年 2025）")]
    public string AcademicYear { get; set; } = default!;

    /// <summary>要生成的年级码（对应微代码 GRADE 的码值）。为空则生成该学段全部年级。</summary>
    public List<string>? GradeCodes { get; set; }

    /// <summary>学校类型码（小学/初中/普通高中…），用于推断默认年级范围。</summary>
    [StringLength(8)]
    public string? SchoolTypeCode { get; set; }
}

/// <summary>批量生成班级。</summary>
public class BatchGenerateClassesDto
{
    [Required]
    public Guid GradeId { get; set; }

    [Range(1, 100, ErrorMessage = "班级数必须在 1~100 之间")]
    public int ClassCount { get; set; }

    /// <summary>起始班号，默认 1。</summary>
    [Range(1, 99)]
    public int StartNo { get; set; } = 1;

    /// <summary>班级名称模板，支持 {grade} 与 {no} 占位符，默认 {grade}{no}班。</summary>
    [StringLength(64)]
    public string? NamePattern { get; set; }
}

/// <summary>批量生成结果。</summary>
public class BatchGenerateResultDto<T>
{
    public int Created { get; set; }
    public int Skipped { get; set; }

    public List<T> Items { get; set; } = new();
}
