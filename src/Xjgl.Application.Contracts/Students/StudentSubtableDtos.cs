using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Dtos;

namespace Xjgl.Students;

// ---------------------------------------------------------------------------
// 学生详情子表（监护人 / 学习简历 / 奖励 / 处分 / 资助 / 体检）
//
// 命名约定：
//   - 读取 DTO 带显示用的名称字段（RelationName / RewardTypeName …），
//     由后端把码值翻译成中文，前端不做码表映射（与列表页一致）；
//   - 写入 DTO 只收码值 + 业务字段，**身份证明文只在写入方向出现**，
//     读取方向只给掩码（`MaskedIdCard`）—— 这样即使前端出错也导不出明文。
// ---------------------------------------------------------------------------

/// <summary>监护人。</summary>
public class GuardianDto : EntityDto<Guid>
{
    public Guid StudentId { get; set; }
    public int Seq { get; set; }
    public string Name { get; set; } = default!;
    public string? RelationCode { get; set; }
    public string? RelationName { get; set; }

    /// <summary>脱敏后的身份证号（保留前 6 后 4），明文不出接口。</summary>
    public string? MaskedIdCard { get; set; }

    public bool HasIdCard { get; set; }
    public string? PhoneNumber { get; set; }
    public string? WorkUnit { get; set; }
    public string? Address { get; set; }
    public bool IsPrimary { get; set; }
    public string? Remark { get; set; }
}

/// <summary>新增 / 修改监护人。</summary>
public class CreateUpdateGuardianDto
{
    [Required(ErrorMessage = "监护人姓名不能为空")]
    [StringLength(60)]
    public string Name { get; set; } = default!;

    [StringLength(8)]
    public string? RelationCode { get; set; }

    /// <summary>
    /// 身份证号明文。**留空表示不修改**（更新时用）：界面上只显示掩码，
    /// 用户不改就不该回传，否则会把掩码当成新号码存进去。
    /// </summary>
    [StringLength(18)]
    public string? IdCardNumber { get; set; }

    [StringLength(20)]
    public string? PhoneNumber { get; set; }

    [StringLength(120)]
    public string? WorkUnit { get; set; }

    [StringLength(200)]
    public string? Address { get; set; }

    public bool IsPrimary { get; set; }

    [StringLength(500)]
    public string? Remark { get; set; }
}

/// <summary>学习简历。</summary>
public class ResumeDto : EntityDto<Guid>
{
    public Guid StudentId { get; set; }
    public int Seq { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string SchoolName { get; set; } = default!;
    public string? GradeName { get; set; }
    public string? ClassName { get; set; }
    public string? Post { get; set; }
    public string? Referee { get; set; }
    public string? Remark { get; set; }
}

/// <summary>新增 / 修改学习简历。</summary>
public class CreateUpdateResumeDto
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    [Required(ErrorMessage = "学校名称不能为空")]
    [StringLength(120)]
    public string SchoolName { get; set; } = default!;

    [StringLength(40)]
    public string? GradeName { get; set; }

    [StringLength(40)]
    public string? ClassName { get; set; }

    [StringLength(60)]
    public string? Post { get; set; }

    [StringLength(60)]
    public string? Referee { get; set; }

    [StringLength(500)]
    public string? Remark { get; set; }
}

/// <summary>奖励。</summary>
public class RewardDto : EntityDto<Guid>
{
    public Guid StudentId { get; set; }
    public int Seq { get; set; }
    public DateTime RewardDate { get; set; }
    public string RewardName { get; set; } = default!;
    public string? RewardTypeCode { get; set; }
    public string? RewardTypeName { get; set; }
    public string? RewardLevelCode { get; set; }
    public string? RewardLevelName { get; set; }
    public string? AwardOrg { get; set; }
    public string? Remark { get; set; }
}

/// <summary>新增 / 修改奖励。</summary>
public class CreateUpdateRewardDto
{
    [Required(ErrorMessage = "获奖日期不能为空")]
    public DateTime RewardDate { get; set; }

    [Required(ErrorMessage = "奖励名称不能为空")]
    [StringLength(120)]
    public string RewardName { get; set; } = default!;

    [StringLength(8)]
    public string? RewardTypeCode { get; set; }

    [StringLength(8)]
    public string? RewardLevelCode { get; set; }

    [StringLength(120)]
    public string? AwardOrg { get; set; }

    [StringLength(500)]
    public string? Remark { get; set; }
}

/// <summary>处分。</summary>
public class PunishmentDto : EntityDto<Guid>
{
    public Guid StudentId { get; set; }
    public int Seq { get; set; }
    public DateTime PunishDate { get; set; }
    public string PunishName { get; set; } = default!;
    public string? PunishTypeCode { get; set; }
    public string? PunishTypeName { get; set; }
    public string? PunishOrg { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime? RevokeDate { get; set; }
    public string? RevokeReason { get; set; }
    public string? Remark { get; set; }
}

/// <summary>新增 / 修改处分。</summary>
public class CreateUpdatePunishmentDto
{
    [Required(ErrorMessage = "处分日期不能为空")]
    public DateTime PunishDate { get; set; }

    [Required(ErrorMessage = "处分名称不能为空")]
    [StringLength(120)]
    public string PunishName { get; set; } = default!;

    [StringLength(8)]
    public string? PunishTypeCode { get; set; }

    [StringLength(120)]
    public string? PunishOrg { get; set; }

    /// <summary>是否已撤销（撤销信息与处分信息一起写，保持"撤销"是处分状态的一部分）。</summary>
    public bool IsRevoked { get; set; }

    public DateTime? RevokeDate { get; set; }

    [StringLength(500)]
    public string? RevokeReason { get; set; }

    [StringLength(500)]
    public string? Remark { get; set; }
}

/// <summary>资助。</summary>
public class SubsidyDto : EntityDto<Guid>
{
    public Guid StudentId { get; set; }
    public int Seq { get; set; }
    public string AcademicYear { get; set; } = default!;
    public string? SubsidyTypeCode { get; set; }
    public string? SubsidyTypeName { get; set; }
    public string SubsidyName { get; set; } = default!;
    public decimal? Amount { get; set; }
    public DateTime? SubsidyDate { get; set; }
    public string? FundingOrg { get; set; }
    public string? Remark { get; set; }
}

/// <summary>新增 / 修改资助。</summary>
public class CreateUpdateSubsidyDto
{
    [Required]
    [RegularExpression(@"^(\d{4}|\d{4}-\d{4})$", ErrorMessage = "学年格式应为 2025-2026")]
    public string AcademicYear { get; set; } = default!;

    [StringLength(8)]
    public string? SubsidyTypeCode { get; set; }

    [Required(ErrorMessage = "资助项目名称不能为空")]
    [StringLength(120)]
    public string SubsidyName { get; set; } = default!;

    /// <summary>资助金额（元）。负数没有意义，因此限定 &gt;= 0。</summary>
    [Range(0, 99999999, ErrorMessage = "资助金额不能为负数")]
    public decimal? Amount { get; set; }

    public DateTime? SubsidyDate { get; set; }

    [StringLength(120)]
    public string? FundingOrg { get; set; }

    [StringLength(500)]
    public string? Remark { get; set; }
}

/// <summary>体质健康（体检）。</summary>
public class PhysicalDto : EntityDto<Guid>
{
    public Guid StudentId { get; set; }
    public string AcademicYear { get; set; } = default!;
    public DateTime? CheckDate { get; set; }
    public decimal? Height { get; set; }
    public decimal? Weight { get; set; }
    public decimal? LeftVision { get; set; }
    public decimal? RightVision { get; set; }
    public string? HealthCode { get; set; }
    public string? HealthName { get; set; }
    public string? DoctorAdvice { get; set; }
    public string? Remark { get; set; }
}

/// <summary>新增 / 修改体质健康记录。</summary>
public class CreateUpdatePhysicalDto
{
    [Required]
    [RegularExpression(@"^(\d{4}|\d{4}-\d{4})$", ErrorMessage = "学年格式应为 2025-2026")]
    public string AcademicYear { get; set; } = default!;

    public DateTime? CheckDate { get; set; }

    [Range(0, 300, ErrorMessage = "身高应在 0~300 厘米之间")]
    public decimal? Height { get; set; }

    [Range(0, 500, ErrorMessage = "体重应在 0~500 千克之间")]
    public decimal? Weight { get; set; }

    [Range(0, 6, ErrorMessage = "视力应在 0~6 之间")]
    public decimal? LeftVision { get; set; }

    [Range(0, 6, ErrorMessage = "视力应在 0~6 之间")]
    public decimal? RightVision { get; set; }

    [StringLength(8)]
    public string? HealthCode { get; set; }

    [StringLength(500)]
    public string? DoctorAdvice { get; set; }

    [StringLength(500)]
    public string? Remark { get; set; }
}
