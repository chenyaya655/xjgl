using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Services;

namespace Xjgl.Permissions;

/// <summary>一条权限委派（台账行）。</summary>
public class PermissionDelegationDto
{
    public Guid Id { get; set; }

    public Guid DelegatorUserId { get; set; }
    public string? DelegatorName { get; set; }

    public Guid DelegateeUserId { get; set; }
    public string? DelegateeName { get; set; }

    public string PermissionName { get; set; } = default!;

    /// <summary>权限的中文名（由元数据里的操作名翻译，查不到时回落为权限码）。</summary>
    public string? PermissionDisplayName { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public bool IsRevoked { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }

    /// <summary>是否仍然生效（未撤销、未到期）。前端据此显示"生效中 / 已失效"。</summary>
    public bool IsActive { get; set; }

    /// <summary>是不是"我借出去的"（前端要能区分两个方向）。</summary>
    public bool IsMine { get; set; }

    public string? Remark { get; set; }

    public DateTime CreationTime { get; set; }
}

/// <summary>新建一条委派。</summary>
public class CreatePermissionDelegationDto
{
    /// <summary>被委派人（用户的 Id）。</summary>
    [Required]
    public Guid DelegateeUserId { get; set; }

    /// <summary>要委派的权限码；必须是**自己拥有**且**允许委派**的。</summary>
    [Required]
    [StringLength(128)]
    public string PermissionName { get; set; } = default!;

    /// <summary>到期时间；留空表示长期有效（需要显式撤销）。</summary>
    public DateTime? ExpiresAt { get; set; }

    [StringLength(256)]
    public string? Remark { get; set; }
}

/// <summary>可委派的权限项（给下拉框用：只列"我拥有的、且允许委派的"）。</summary>
public class DelegatablePermissionDto
{
    public string PermissionName { get; set; } = default!;
    public string? DisplayName { get; set; }
}

/// <summary>
/// 权限委派（ROADMAP 第 19 条）。
///
/// 【为什么不额外定义一个权限码来管它】
/// 委派是"用我自己的权限"，谁能委派什么由**子集约束**决定（只能借出自己有的），
/// 再叠一个 `Sys.Delegation.Manage` 只会多出一处"勾了到底管不管用"的疑问。
/// 撤销则由**归属**约束：只有委派人本人（或管理员）能撤销自己借出的那条。
/// </summary>
public interface IPermissionDelegationAppService : IApplicationService
{
    /// <summary>与我相关的委派：我借出去的 + 别人借给我的。</summary>
    Task<List<PermissionDelegationDto>> GetListAsync();

    /// <summary>我可以委派出去的权限（我拥有的 ∩ 允许委派的）。</summary>
    Task<List<DelegatablePermissionDto>> GetDelegatablePermissionsAsync();

    /// <summary>新建一条委派（立即生效）。</summary>
    Task<PermissionDelegationDto> CreateAsync(CreatePermissionDelegationDto input);

    /// <summary>撤销一条委派（只有委派人本人或管理员可以）。</summary>
    Task<PermissionDelegationDto> RevokeAsync(Guid id);
}
