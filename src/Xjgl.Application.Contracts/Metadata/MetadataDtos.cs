using Volo.Abp.Application.Dtos;

namespace Xjgl.Metadata;

/// <summary>模块元数据 DTO（含操作）。</summary>
public class ModuleDto : EntityDto<Guid>
{
    public Guid SubsystemId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Path { get; set; }
    public string? Component { get; set; }
    public string? Icon { get; set; }
    public int Sort { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsHidden { get; set; }
    public bool IsExternal { get; set; }
    public string PermissionName { get; set; } = default!;
    public string? OrgTypes { get; set; }
    public string? Remark { get; set; }

    public List<ModuleActionDto> Actions { get; set; } = new();
}

/// <summary>模块操作 DTO。</summary>
public class ModuleActionDto : EntityDto<Guid>
{
    public Guid ModuleId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string ActionType { get; set; } = default!;
    public string PermissionName { get; set; } = default!;
    public int Sort { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsDefaultGranted { get; set; }
}

/// <summary>子系统 DTO（含模块）。</summary>
public class SubsystemDto : EntityDto<Guid>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Icon { get; set; }
    public int Sort { get; set; }
    public bool IsEnabled { get; set; }
    public string? Remark { get; set; }

    public List<ModuleDto> Modules { get; set; } = new();
}

/// <summary>新增 / 修改子系统。</summary>
public class CreateUpdateSubsystemDto
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Icon { get; set; }
    public int Sort { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? Remark { get; set; }
}

/// <summary>新增 / 修改模块。</summary>
public class CreateUpdateModuleDto
{
    public Guid SubsystemId { get; set; }

    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Path { get; set; }
    public string? Component { get; set; }
    public string? Icon { get; set; }
    public int Sort { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsHidden { get; set; }
    public bool IsExternal { get; set; }
    public string? OrgTypes { get; set; }
    public string? Remark { get; set; }
}

/// <summary>新增 / 修改模块操作。</summary>
public class CreateUpdateModuleActionDto
{
    public Guid ModuleId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string ActionType { get; set; } = default!;
    public int Sort { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsDefaultGranted { get; set; }
}

/// <summary>前端菜单项。</summary>
public class MenuItemDto
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Path { get; set; }
    public string? Component { get; set; }
    public string? Icon { get; set; }
    public bool IsExternal { get; set; }

    /// <summary>该模块下当前用户拥有的操作权限码。</summary>
    public List<string> Actions { get; set; } = new();
}

/// <summary>前端一级菜单（对应子系统）。</summary>
public class MenuGroupDto
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Icon { get; set; }

    public List<MenuItemDto> Children { get; set; } = new();
}

/// <summary>当前登录用户的完整上下文，前端登录后一次拿全。</summary>
public class MyProfileDto
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = default!;
    public string? RealName { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }

    public Guid? OrgId { get; set; }
    public string? OrgName { get; set; }
    public string? OrgType { get; set; }
    public string? OrgPathCode { get; set; }

    public List<string> Roles { get; set; } = new();

    /// <summary>当前用户拥有的全部权限码（含模块与操作）。</summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>可见菜单树。</summary>
    public List<MenuGroupDto> Menus { get; set; } = new();
}
