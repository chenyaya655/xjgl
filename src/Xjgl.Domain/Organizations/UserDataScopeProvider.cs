using Volo.Abp.DependencyInjection;
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
/// 数据范围解析：**当前用户 → 所属组织 → 范围**。
///
/// 【范围怎么定】
/// 本项目暂时**按组织层级自动决定**，不引入额外的配置表：
///
/// | 用户的组织 | 范围 |
/// |---|---|
/// | 省厅（Province） | 全部数据 |
/// | 地市 / 县区 / 学校 | 本级及下级 |
/// | 未关联组织 + 内置 admin 角色 | 全部数据（超管旁路） |
/// | 未关联组织 + 非 admin | **什么都看不到** |
///
/// 为什么不立刻做 `sys_role_data_scope` 配置表：
///   四级行政体系里"上级能看下级"是绝对主流，按层级推导已经覆盖绝大多数场景；
///   配置表解决的是少数例外（例如某市局只看本市不看他下属县）。
///   先让**默认安全**（没有范围就看不到数据）落地，比先做配置界面更重要 ——
///   当前真实状态是"任何登录用户都能传任意组织 Id 查全省学生"。
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

    private UserDataScope? _cached;

    public UserDataScopeProvider(
        ICurrentUser currentUser,
        IXjglRepository<SysUserPredefine> predefines,
        IXjglRepository<SysOrg> orgs)
    {
        _currentUser = currentUser;
        _predefines = predefines;
        _orgs = orgs;
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

        // 省厅：全省数据
        if (org.OrgType == OrgTypes.Province)
        {
            return UserDataScope.ForAll(userId, org.Id, org.PathCode);
        }

        // 地市 / 县区 / 学校：本级及下级
        return UserDataScope.ForOrgAndChildren(org.Id, org.PathCode, userId);
    }
}
