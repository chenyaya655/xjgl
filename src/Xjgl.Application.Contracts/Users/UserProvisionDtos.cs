using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Dtos;

namespace Xjgl.Users;

/// <summary>预置用户 DTO（不返回身份证号明文）。</summary>
public class PredefineUserDto : EntityDto<Guid>
{
    public string RealName { get; set; } = default!;

    /// <summary>手机号（列表页按权限可脱敏展示）。</summary>
    public string PhoneNumber { get; set; } = default!;

    public Guid OrgId { get; set; }
    public string? OrgPathCode { get; set; }
    public string? RoleNames { get; set; }
    public string? Position { get; set; }
    public string Status { get; set; } = default!;
    public string StatusName { get; set; } = default!;
    public string ActivationCode { get; set; } = default!;
    public DateTime ActivationExpiresAt { get; set; }
    public Guid? ActivatedUserId { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public string? BatchNo { get; set; }
    public string? Remark { get; set; }
    public DateTime CreationTime { get; set; }
}

/// <summary>新增一条预置用户。</summary>
public class CreatePredefineUserDto
{
    [Required(ErrorMessage = "姓名不能为空")]
    [StringLength(60)]
    public string RealName { get; set; } = default!;

    [Required(ErrorMessage = "手机号不能为空")]
    [StringLength(20)]
    public string PhoneNumber { get; set; } = default!;

    /// <summary>身份证号（可选；提供后激活时必须匹配）。</summary>
    [StringLength(18)]
    public string? IdCardNumber { get; set; }

    [Required]
    public Guid OrgId { get; set; }

    /// <summary>拟授予角色，多个用英文逗号分隔。</summary>
    [StringLength(512)]
    public string? RoleNames { get; set; }

    [StringLength(60)]
    public string? Position { get; set; }

    [StringLength(1024)]
    public string? Remark { get; set; }

    /// <summary>激活码有效天数，默认 30 天。</summary>
    public int ActivationValidDays { get; set; } = 30;
}

/// <summary>批量导入预置用户。</summary>
public class ImportPredefineUsersDto
{
    [Required]
    [MinLength(1, ErrorMessage = "至少要有一条预置记录")]
    public List<CreatePredefineUserDto> Users { get; set; } = new();

    /// <summary>批次号；不传则自动生成。</summary>
    [StringLength(64)]
    public string? BatchNo { get; set; }
}

/// <summary>预置用户查询条件。</summary>
public class PredefineUserQueryDto : PagedAndSortedResultRequestDto
{
    /// <summary>组织 Id；配合 <see cref="IncludeDescendantOrgs"/> 可查整棵子树。</summary>
    public Guid? OrgId { get; set; }

    /// <summary>是否包含下级组织的预置记录。</summary>
    public bool IncludeDescendantOrgs { get; set; }

    /// <summary>姓名或手机号关键字。</summary>
    public string? Keyword { get; set; }

    public string? Status { get; set; }

    public string? BatchNo { get; set; }
}

/// <summary>
/// 本人激活账号。
///
/// 安全约束（保证外人无法注册）：
///   必须同时提供「手机号 + 姓名」，且二者与预置记录完全一致；
///   若预置记录里有身份证号，还要求身份证号匹配；
///   激活码必须有效且未过期；同一条预置记录只能激活一次。
/// </summary>
public class ActivateUserDto
{
    [Required(ErrorMessage = "手机号不能为空")]
    [StringLength(20)]
    public string PhoneNumber { get; set; } = default!;

    [Required(ErrorMessage = "姓名不能为空")]
    [StringLength(60)]
    public string RealName { get; set; } = default!;

    [StringLength(18)]
    public string? IdCardNumber { get; set; }

    [Required(ErrorMessage = "激活码不能为空")]
    [StringLength(64)]
    public string ActivationCode { get; set; } = default!;

    [Required(ErrorMessage = "密码不能为空")]
    [StringLength(128, MinimumLength = 6)]
    public string Password { get; set; } = default!;
}

/// <summary>激活成功后的返回。</summary>
public class ActivateUserResultDto
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = default!;
    public string RealName { get; set; } = default!;
    public Guid OrgId { get; set; }
    public List<string> Roles { get; set; } = new();
}

/// <summary>导入结果。</summary>
public class ImportPredefineResultDto
{
    public string BatchNo { get; set; } = default!;
    public int Total { get; set; }
    public int Succeeded { get; set; }

    /// <summary>失败明细（行号从 1 开始）。</summary>
    public List<ImportPredefineErrorDto> Errors { get; set; } = new();
}

public class ImportPredefineErrorDto
{
    public int RowIndex { get; set; }
    public string? RealName { get; set; }
    public string? PhoneNumber { get; set; }
    public string Message { get; set; } = default!;
}
