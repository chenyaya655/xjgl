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
        dto.Menus = BuildMenus(snapshot, permissionSet, dto.OrgType);

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
    /// 组装可见菜单：模块（或它的任一操作）被授予就出现；模块下的操作权限一并返回，供前端控制按钮。
    ///
    /// 【为什么"任一操作被授予"也算模块可见 —— 这是一个真实踩过的坑】
    ///   权限体系是两级：模块权限 <c>Stu.Student</c> + 操作子权限 <c>Stu.Student.Query</c>。
    ///   最初的实现只认 `permissionSet.Contains(m.PermissionName)`，即**必须显式授予模块权限**。
    ///   它之所以长期没暴露，是因为 admin 角色被种子授予了「当时定义的全部权限」——
    ///   模块权限与操作权限**两者都有**；而按最小权限原则建的角色（例如演示账号使用的
    ///   DemoOperator，只授 13 条叶子权限）**没有任何模块权限**。
    ///   结果：该角色 <c>/api/app/my-profile</c> 返回 permissions 13 条、menus **空数组**，
    ///   前端左侧只剩写死的「工作台」，每个业务页面都进不去。
    ///
    ///   注意这**不是**放宽鉴权：模块可见只是"显示入口"，能不能看数据仍由各接口上的
    ///   <c>[Authorize]</c> 按叶子权限判定。反过来更荒谬 —— 一个能查学生的账号
    ///   看不到「学生管理」菜单，等于把功能藏起来了。
    /// </summary>
    /// <param name="orgType">
    /// 当前用户所属组织的层级（省/市/县/校），用于模块的「组织层级」限制。
    /// 为 null（用户没有关联组织）时**不做限制** —— 见 <see cref="IsOrgTypeAllowed"/>。
    /// </param>
    private static List<MenuGroupDto> BuildMenus(
        ModuleMetadataSnapshot snapshot,
        HashSet<string> permissionSet,
        string? orgType)
    {
        var groups = new List<MenuGroupDto>();

        foreach (var subsystem in snapshot.Subsystems)
        {
            var visibleModules = subsystem.Modules
                .Where(m => IsModuleVisible(m, permissionSet) && IsOrgTypeAllowed(m, orgType))
                .Select(m => new MenuItemDto
                {
                    Code = m.Code,
                    Name = m.Name,
                    Path = m.Path,
                    Component = m.Component,
                    Icon = m.Icon,
                    IsExternal = m.IsExternal,
                    // 隐藏模块**照样下发**：前端注册动态路由用的就是这份列表，
                    // 删掉它等于把"不进菜单"变成"打不开"（详见 MenuItemDto.IsHidden）
                    IsHidden = m.IsHidden,
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

    /// <summary>
    /// 模块是否对当前用户可见：模块权限本身被授予，或它的**任一**操作权限被授予。
    ///
    /// 只判一层（模块 → 操作），不做递归：元数据权限树固定是两级，多写一层递归
    /// 反而会让人以为还有第三级。
    /// </summary>
    private static bool IsModuleVisible(ModuleMetadata module, HashSet<string> permissionSet)
    {
        if (permissionSet.Contains(module.PermissionName))
        {
            return true;
        }

        return module.Actions.Any(a => permissionSet.Contains(a.PermissionName));
    }

    /// <summary>
    /// 模块的「组织层级」限制是否放行当前用户。
    ///
    /// 元数据里的 `OrgTypes` 是逗号分隔的组织层级（省/市/县/校），留空 = 不限。
    /// 典型用途：只给县级及以上用的汇总模块，不该出现在学校账号的菜单里。
    ///
    /// 【三个刻意的决定】
    ///   1. **留空即不限**：绝大多数模块不填，行为与加这个功能之前完全一致；
    ///   2. **用户没有关联组织时不限制**：组织层级无从判断，
    ///      此时"藏起来"会让功能静默消失（与"数据范围：未关联组织什么都看不到"不同 ——
    ///      那是**数据**的可见性，这里是**菜单入口**的适用性，方向相反才安全）；
    ///   3. **它只管菜单**：能不能调用接口仍由各接口的 `[Authorize]` 判定。
    ///      换句话说，它是"这个模块跟这个层级的人有没有关系"，不是一道鉴权闸门 ——
    ///      所以**不能**指望靠它挡越权（挡越权的是权限 + 数据范围）。
    /// </summary>
    private static bool IsOrgTypeAllowed(ModuleMetadata module, string? orgType)
    {
        if (module.AllowedOrgTypes.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(orgType))
        {
            return true;
        }

        return module.AllowedOrgTypes.Contains(orgType, StringComparer.OrdinalIgnoreCase);
    }
}
