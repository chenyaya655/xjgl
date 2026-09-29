using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Services;

namespace Xjgl.Organizations;

/// <summary>某个角色当前的数据范围（ROADMAP 第 9 条）。</summary>
public class RoleDataScopeDto
{
    public Guid RoleId { get; set; }

    public string RoleName { get; set; } = default!;

    /// <summary>
    /// 配置的范围码值；**为 null 表示没有配置** ——
    /// 此时该角色按组织层级推导（省厅=全省、市/县/校=本级及下级），
    /// 也就是"与加这个功能之前完全一致"。
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>范围的中文名（未配置时为 null）。</summary>
    public string? ScopeName { get; set; }

    /// <summary>是否已显式配置（前端据此区分"默认"与"配置成一样"）。</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Scope);

    public string? Remark { get; set; }

    /// <summary>可配置的范围选项（给下拉框用，避免前端手写码值）。</summary>
    public static List<CodeOption> Options()
        => DataScopes.Configurable
            .Select(s => new CodeOption
            {
                Value = s,
                Label = DataScopes.GetDisplayName(s),
                // 解释文案也由后端下发：前端自己写一份"码值 → 文案"的映射
                // 就是第二份码值，码值改名/新增时它只会静默少一句说明（见 GetDescription 的说明）
                Description = DataScopes.GetDescription(s)
            })
            .ToList();
}

/// <summary>选项（值 / 显示名 / 可选的一句话解释）。</summary>
public class CodeOption
{
    public string Value { get; set; } = default!;

    public string Label { get; set; } = default!;

    /// <summary>一句话解释"选它意味着什么"；没有解释时为空串。</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>设置某个角色的数据范围。</summary>
public class SetRoleDataScopeDto
{
    [Required]
    public Guid RoleId { get; set; }

    /// <summary>
    /// 范围码值。传 null / 空串表示**清除配置**（回到按组织层级推导）。
    /// </summary>
    [StringLength(32)]
    public string? Scope { get; set; }

    /// <summary>备注：为什么给这个角色配这个范围。半年后没人记得。</summary>
    [StringLength(256)]
    public string? Remark { get; set; }
}

/// <summary>
/// 角色级数据范围配置。
///
/// 权限沿用**角色管理**的权限（`Sys.Role.*`）：配数据范围本来就是角色管理的一部分，
/// 没必要为它再定义一个权限码 —— 多一个码就多一处"勾了到底管不管用"的疑问。
/// </summary>
public interface IRoleDataScopeAppService : IApplicationService
{
    /// <summary>全部角色及其数据范围（未配置的也返回，Scope 为 null）。</summary>
    Task<List<RoleDataScopeDto>> GetListAsync();

    /// <summary>可配置的范围选项（下拉框数据源）。</summary>
    Task<List<CodeOption>> GetOptionsAsync();

    /// <summary>设置或清除某个角色的数据范围。</summary>
    Task<RoleDataScopeDto> SetAsync(SetRoleDataScopeDto input);
}
