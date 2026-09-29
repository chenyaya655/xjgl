using Microsoft.AspNetCore.Identity;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Identity;
using Volo.Abp.Users;
using Xjgl.Data;
using Xjgl.Users;

namespace Xjgl.Organizations;

/// <summary>当前用户数据范围的解析器。</summary>
public interface IUserDataScopeProvider
{
    /// <summary>解析当前登录用户的数据范围（同一请求内只查一次库）。</summary>
    Task<UserDataScope> GetAsync();
}

/// <summary>
/// 数据范围解析：**角色上配置的范围 → 用户所属组织 → 范围**。
///
/// 【解析顺序（ROADMAP 第 9 条）】
///   1. 内置 admin 角色 → 全部数据（超管旁路，不可配置掉）；
///   2. 用户各角色在 <see cref="SysRoleDataScope"/> 里配置了范围 → 取**最宽**的那个
///      （取最宽的理由见 <see cref="DataScopes.ResolveEffective"/>）；
///   3. 没有任何配置 → 按组织层级推导（与加这个功能之前**完全一致**）：
///      | 用户的组织 | 范围 |
///      |---|---|
///      | 省厅（Province） | 全部数据 |
///      | 地市 / 县区 / 学校 | 本级及下级 |
///      | 未关联组织 + 非 admin | **什么都看不到** |
///
/// 【为什么"没配置就按层级推导"而不是"没配置就看不到"】
/// 前者保证升级后现有角色不受影响（否则上线瞬间所有人失明）；
/// 后者才是"默认安全"，但它只在**用户自己没关联组织**时启用
/// —— 那一步确实必须什么都看不到（见 <see cref="UserDataScope.Empty"/>）。
///
/// 【超管旁路为什么必须有】
///   内置 admin 账号没有关联组织（它不是某一个学校的人）。
///   若不旁路，系统管理员登录后会看不到任何数据、也无法排查问题。
///   同时它也让集成测试可以继续以 admin 身份覆盖全量场景。
///
/// 注册为 Scoped：同一请求内多个查询只解析一次（见下面的 _cached）。
/// </summary>
public class UserDataScopeProvider : IUserDataScopeProvider, IScopedDependency
{
    /// <summary>内置超级管理员角色名（ABP 默认角色）。</summary>
    private const string SuperAdminRole = "admin";

    private readonly ICurrentUser _currentUser;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IXjglRepository<SysRoleDataScope> _roleScopes;
    private readonly IIdentityRoleRepository _roles;
    private readonly ILookupNormalizer _lookupNormalizer;

    private UserDataScope? _cached;

    public UserDataScopeProvider(
        ICurrentUser currentUser,
        IXjglRepository<SysUserPredefine> predefines,
        IXjglRepository<SysOrg> orgs,
        IXjglRepository<SysRoleDataScope> roleScopes,
        IIdentityRoleRepository roles,
        ILookupNormalizer lookupNormalizer)
    {
        _currentUser = currentUser;
        _predefines = predefines;
        _orgs = orgs;
        _roleScopes = roleScopes;
        _roles = roles;
        _lookupNormalizer = lookupNormalizer;
    }

    public async Task<UserDataScope> GetAsync()
    {
        if (_cached != null)
        {
            return _cached;
        }

        _cached = await ResolveAsync();

        return _cached;
    }

    private async Task<UserDataScope> ResolveAsync()
    {
        var userId = _currentUser.Id;

        // 未登录：什么都不给（匿名接口不会走到这里，但防御性处理）
        if (userId == null)
        {
            return UserDataScope.Empty(null);
        }

        // 超管旁路
        if (_currentUser.Roles.Contains(SuperAdminRole, StringComparer.OrdinalIgnoreCase))
        {
            return UserDataScope.ForSuperAdmin(userId);
        }

        // 用户 → 预置记录 → 组织
        // （预置记录里存了 OrgId，这是"用户属于哪个单位"的唯一来源）
        var predefine = await _predefines.FindAsync(p => p.ActivatedUserId == userId);

        if (predefine == null || predefine.OrgId == Guid.Empty)
        {
            // 关联不到组织：**默认什么都看不到**，而不是默认全都能看到。
            // 这是数据范围设计里唯一不能搞错的方向。
            return UserDataScope.Empty(userId);
        }

        var org = await _orgs.FindAsync(predefine.OrgId);

        if (org == null)
        {
            return UserDataScope.Empty(userId);
        }

        // ★ 角色上配置的范围优先（ROADMAP 第 9 条）。
        // 放在"拿到组织之后"：SelfOnly 这类范围仍然需要组织路径来限定子树，
        // 而 Empty（未关联组织）的人不该因为配了角色就突然能看到数据。
        var configured = await ResolveConfiguredScopeAsync();

        return BuildScope(userId, org, configured);
    }

    /// <summary>
    /// 取当前用户各角色上配置的范围里**最宽**的那个；一个都没配则返回 <c>null</c>。
    /// </summary>
    private async Task<string?> ResolveConfiguredScopeAsync()
    {
        var roleNames = _currentUser.Roles;

        // 注意是 `Length` 不是 `Count`：`ICurrentUser.Roles` 是 `string[]`，
        // `.Count` 会解析成 LINQ 的扩展方法（方法组），写成 `== 0` 编译不过。
        if (roleNames == null || roleNames.Length == 0)
        {
            return null;
        }

        var roleIds = new List<Guid>();

        foreach (var roleName in roleNames)
        {
            if (roleName.IsNullOrWhiteSpace())
            {
                continue;
            }

            // 按规范化名查（ABP 的 AbpRoles 存的是 NormalizedName，不是显示名）
            var role = await _roles.FindByNormalizedNameAsync(_lookupNormalizer.NormalizeName(roleName));

            if (role != null)
            {
                roleIds.Add(role.Id);
            }
        }

        if (roleIds.Count == 0)
        {
            return null;
        }

        var configured = await _roleScopes.GetListAsync(s => roleIds.Contains(s.RoleId));

        return DataScopes.ResolveEffective(configured.Select(c => c.Scope));
    }

    /// <summary>
    /// 按生效范围拼出 <see cref="UserDataScope"/>。
    ///
    /// 【四种可配置范围各自怎么落】
    ///   All            → 不受限；
    ///   OrgAndChildren → 本组织及后代（默认行为）；
    ///   OrgOnly        → 仅本组织本身（"某市局只看本市、不看他下属县"就是它）；
    ///   SelfOnly       → 仍然限定在"本组织及后代"内，额外再由各查询按创建人过滤
    ///                    （见 `StudentAppService` 里的 `selfOnly`）——
    ///                    这里不能直接返回"只有本人"，因为范围对象本身不知道实体是谁创建的。
    /// </summary>
    private static UserDataScope BuildScope(Guid? userId, SysOrg org, string? configuredScope)
    {
        var scope = configuredScope;

        if (scope == DataScopes.All)
        {
            return UserDataScope.ForAll(userId, org.Id, org.PathCode);
        }

        if (scope == DataScopes.OrgOnly)
        {
            return UserDataScope.ForOrgOnly(org.Id, org.PathCode, userId);
        }

        if (scope == DataScopes.SelfOnly)
        {
            return UserDataScope.ForSelfOnly(org.Id, org.PathCode, userId);
        }

        // OrgAndChildren，以及"没有配置"（scope == null）都落在这里。
        // 注意这里**刻意不区分**"配置了 OrgAndChildren"与"没配置"：
        // 两者的行为本来就一样，多一个分支只会多一处出错的地方。
        return UserDataScope.ForOrgAndChildren(org.Id, org.PathCode, userId);
    }
}
