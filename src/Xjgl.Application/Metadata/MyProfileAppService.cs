using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Identity;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Users;

namespace Xjgl.Metadata;

/// <summary>
/// 当前登录用户的上下文。
///
/// 前端登录后调用一次 <c>GET /api/app/my-profile</c>，就能拿到：
/// 身份信息、所属组织、角色、**全部权限码**、**可见菜单树**。
/// 前端据此完成三件事：动态注册路由、渲染菜单、按权限码控制按钮。
/// </summary>
[Authorize]
public class MyProfileAppService : XjglAppService, IMyProfileAppService
{
    private readonly IModuleMetadataCache _metadataCache;
    private readonly IPermissionChecker _permissionChecker;
    private readonly IdentityUserManager _userManager;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IXjglRepository<SysOrg> _orgs;

    public MyProfileAppService(
        IModuleMetadataCache metadataCache,
        IPermissionChecker permissionChecker,
        IdentityUserManager userManager,
        IXjglRepository<SysUserPredefine> predefines,
        IXjglRepository<SysOrg> orgs)
    {
        _metadataCache = metadataCache;
        _permissionChecker = permissionChecker;
        _userManager = userManager;
        _predefines = predefines;
        _orgs = orgs;
    }

    public async Task<MyProfileDto> GetAsync()
    {
        if (!CurrentUser.IsAuthenticated || CurrentUser.Id == null)
        {
            throw new AbpAuthorizationException("当前请求未通过身份认证。");
        }

        var userId = CurrentUser.Id.Value;
        var user = await _userManager.FindByIdAsync(userId.ToString())
                   ?? throw new AbpAuthorizationException("当前用户不存在。");

        var roles = await _userManager.GetRolesAsync(user);

        var dto = new MyProfileDto
        {
            UserId = userId,
            UserName = user.UserName ?? string.Empty,
            RealName = user.Name,
            Email = user.Email,
            PhoneNumber = user.PhoneNumber,
            Roles = roles.ToList()
        };

        await FillOrganizationAsync(dto, userId);

        var snapshot = _metadataCache.GetSnapshot();
        var permissionSet = await ResolvePermissionsAsync(snapshot);

        dto.Permissions = permissionSet.ToList();
        dto.Menus = BuildMenus(snapshot, permissionSet);

        return dto;
    }

    /// <summary>通过预置记录反查用户所属组织（预置记录里保存了 OrgId）。</summary>
    private async Task FillOrganizationAsync(MyProfileDto dto, Guid userId)
    {
        var predefine = await _predefines.FindAsync(p => p.ActivatedUserId == userId);
        if (predefine == null)
        {
            return;
        }

        dto.OrgId = predefine.OrgId;

        var org = await _orgs.FindAsync(predefine.OrgId);
        if (org != null)
        {
            dto.OrgName = org.Name;
            dto.OrgType = org.OrgType;
            dto.OrgPathCode = org.PathCode;
        }
    }

    /// <summary>批量解析当前用户在元数据权限里被授予了哪些。</summary>
    private async Task<HashSet<string>> ResolvePermissionsAsync(ModuleMetadataSnapshot snapshot)
    {
        var names = snapshot.AllPermissionNames.ToArray();

        if (names.Length == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var result = await _permissionChecker.IsGrantedAsync(names);

        return result.Result
            .Where(kv => kv.Value == PermissionGrantResult.Granted)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// 组装可见菜单：模块权限被授予才出现；模块下的操作权限一并返回，供前端控制按钮。
    /// </summary>
    private static List<MenuGroupDto> BuildMenus(ModuleMetadataSnapshot snapshot, HashSet<string> permissionSet)
    {
        var groups = new List<MenuGroupDto>();

        foreach (var subsystem in snapshot.Subsystems)
        {
            var visibleModules = subsystem.Modules
                .Where(m => permissionSet.Contains(m.PermissionName))
                .Select(m => new MenuItemDto
                {
                    Code = m.Code,
                    Name = m.Name,
                    Path = m.Path,
                    Component = m.Component,
                    Icon = m.Icon,
                    IsExternal = m.IsExternal,
                    Actions = m.Actions
                        .Where(a => permissionSet.Contains(a.PermissionName))
                        .Select(a => a.PermissionName)
                        .ToList()
                })
                .ToList();

            if (visibleModules.Count == 0)
            {
                continue;
            }

            groups.Add(new MenuGroupDto
            {
                Code = subsystem.Code,
                Name = subsystem.Name,
                Icon = subsystem.Icon,
                Children = visibleModules
            });
        }

        return groups;
    }
}
