using Volo.Abp.Authorization.Permissions;
using Volo.Abp.DependencyInjection;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Timing;
using Xjgl.Data;

namespace Xjgl.Permissions;

/// <summary>
/// 权限委派的"生效/回收"动作（ROADMAP 第 19 条）。
///
/// 【为什么单独抽一层】
/// 有三处都要做这两件事：应用服务（新建/撤销）、后台任务（到期回收）、
/// 以及将来可能的批量清理。授权存储的写法（provider 名、providerKey 用 Id 字符串）
/// 是**ABP 的约定**，散落三份的话，改一处忘一处就会出现
/// "台账说已撤销、实际还能用"这种最危险的不一致。
/// </summary>
public interface IPermissionDelegationManager
{
    /// <summary>把委派的权限真正授予被委派人（用户级授权）。</summary>
    Task GrantAsync(SysPermissionDelegation delegation);

    /// <summary>
    /// 回收授权。**只有当没有别的生效委派仍在授予同一权限时**才真的收回 ——
    /// 否则"撤销其中一条"会把另一条还在有效期内的委派一起失效。
    /// </summary>
    Task RevokeGrantIfUnusedAsync(SysPermissionDelegation delegation);
}

public class PermissionDelegationManager : IPermissionDelegationManager, ITransientDependency
{
    private readonly IPermissionManager _permissionManager;
    private readonly IXjglRepository<SysPermissionDelegation> _delegations;
    private readonly IClock _clock;

    public PermissionDelegationManager(
        IPermissionManager permissionManager,
        IXjglRepository<SysPermissionDelegation> delegations,
        IClock clock)
    {
        _permissionManager = permissionManager;
        _delegations = delegations;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task GrantAsync(SysPermissionDelegation delegation)
    {
        await _permissionManager.SetAsync(
            delegation.PermissionName,
            // 用户级授权：providerKey 是**用户 Id 的字符串形式**（ABP 的约定）
            UserPermissionValueProvider.ProviderName,
            delegation.DelegateeUserId.ToString(),
            true);
    }

    /// <inheritdoc />
    public async Task RevokeGrantIfUnusedAsync(SysPermissionDelegation delegation)
    {
        var now = _clock.Now;

        // 还有别的生效委派在授予同一个人同一条权限 → 不能收回
        var others = await _delegations.GetListAsync(d =>
            d.Id != delegation.Id
            && d.DelegateeUserId == delegation.DelegateeUserId
            && d.PermissionName == delegation.PermissionName
            && !d.IsRevoked);

        if (others.Any(o => o.IsActive(now)))
        {
            return;
        }

        await _permissionManager.SetAsync(
            delegation.PermissionName,
            UserPermissionValueProvider.ProviderName,
            delegation.DelegateeUserId.ToString(),
            // false 只会**移除用户级授权**；若被委派人本来就通过角色拥有这条权限，
            // 角色授权不受影响（ABP 按 provider 分别存储）
            false);
    }
}
