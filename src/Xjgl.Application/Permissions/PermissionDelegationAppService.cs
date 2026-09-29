using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Identity;
using Volo.Abp.Timing;
using Volo.Abp.Users;
using Xjgl.Data;
using Xjgl.Metadata;

namespace Xjgl.Permissions;

/// <summary>
/// 权限委派（ROADMAP 第 19 条）。
///
/// 【生效方式】新建委派 = 往 ABP 的授权存储里给被委派人加一条**用户级授权**，
/// `[Authorize(PermissionName)]` 立刻生效（理由见 <see cref="SysPermissionDelegation"/>）。
/// 本服务因此同时维护**两处**：台账（本表）与授权存储。两处必须一致 ——
/// 所以"授予/回收"的动作全部走 <see cref="IPermissionDelegationManager"/>，不在各处手写。
/// </summary>
[Authorize]
public class PermissionDelegationAppService : XjglAppService, IPermissionDelegationAppService
{
    /// <summary>内置超级管理员角色名（可以撤销任何人的委派，用于人员离职等场景）。</summary>
    private const string SuperAdminRole = "admin";

    private readonly IXjglRepository<SysPermissionDelegation> _delegations;
    private readonly IPermissionDelegationManager _manager;
    private readonly IPermissionChecker _permissionChecker;
    private readonly IdentityUserManager _userManager;
    private readonly IModuleMetadataCache _metadataCache;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public PermissionDelegationAppService(
        IXjglRepository<SysPermissionDelegation> delegations,
        IPermissionDelegationManager manager,
        IPermissionChecker permissionChecker,
        IdentityUserManager userManager,
        IModuleMetadataCache metadataCache,
        ICurrentUser currentUser,
        IClock clock)
    {
        _delegations = delegations;
        _manager = manager;
        _permissionChecker = permissionChecker;
        _userManager = userManager;
        _metadataCache = metadataCache;
        _currentUser = currentUser;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<List<PermissionDelegationDto>> GetListAsync()
    {
        var me = RequireUserId();

        // 两个方向都要：我借出去的（要能撤销）+ 别人借给我的（要知道自己为什么多了这个权限）
        var rows = await _delegations.GetSortedListAsync(
            d => d.DelegatorUserId == me || d.DelegateeUserId == me,
            "CreationTime DESC");

        var result = new List<PermissionDelegationDto>();

        foreach (var row in rows)
        {
            result.Add(await ToDtoAsync(row, me));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<List<DelegatablePermissionDto>> GetDelegatablePermissionsAsync()
    {
        var snapshot = _metadataCache.GetSnapshot();

        var all = snapshot.AllPermissionNames.ToArray();

        if (all.Length == 0)
        {
            return new List<DelegatablePermissionDto>();
        }

        var granted = await _permissionChecker.IsGrantedAsync(all);

        // 只列"我拥有的 ∩ 允许委派的"：下拉里出现一个我根本没有的权限，
        // 提交时才被拒 —— 那是最让人困惑的交互（"为什么不让我选"）
        var names = granted.Result
            .Where(kv => kv.Value == PermissionGrantResult.Granted)
            .Select(kv => kv.Key)
            .Where(PermissionDelegationRules.IsDelegatable)
            .ToList();

        var displayNames = BuildDisplayNameMap(snapshot);

        return names
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new DelegatablePermissionDto
            {
                PermissionName = n,
                DisplayName = displayNames.GetValueOrDefault(n)
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<PermissionDelegationDto> CreateAsync(CreatePermissionDelegationDto input)
    {
        var me = RequireUserId();

        if (input.DelegateeUserId == me)
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", "不能把权限委派给自己。");
        }

        // ① 允许委派吗（系统管理类 / 导出类 / 运维动作不允许）
        if (!PermissionDelegationRules.IsDelegatable(input.PermissionName))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", PermissionDelegationRules.DescribeWhyNot(input.PermissionName));
        }

        // ② 子集约束：只能借出**自己拥有**的权限
        if (!await _permissionChecker.IsGrantedAsync(input.PermissionName))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", $"你没有权限「{input.PermissionName}」，不能把它委派给别人。");
        }

        // ③ 被委派人必须存在
        var delegatee = await _userManager.FindByIdAsync(input.DelegateeUserId.ToString())
            ?? throw new BusinessException(XjglDomainErrorCodes.DataNotFound)
                .WithData("reason", "被委派的用户不存在。");

        // ④ 到期时间必须在将来（传一个过去的时间等于"建了一条立刻失效的委派"）
        if (input.ExpiresAt.HasValue && input.ExpiresAt.Value <= _clock.Now)
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", "到期时间必须晚于当前时间。");
        }

        // ⑤ 同一个人 + 同一条权限，已有生效中的委派就别重复建
        var existing = await _delegations.GetListAsync(d =>
            d.DelegatorUserId == me
            && d.DelegateeUserId == input.DelegateeUserId
            && d.PermissionName == input.PermissionName
            && !d.IsRevoked);

        if (existing.Any(d => d.IsActive(_clock.Now)))
        {
            throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                .WithData("reason", "这条权限已经委派给该用户且仍在生效中，无需重复委派。");
        }

        var meUser = await _userManager.FindByIdAsync(me.ToString());

        var entity = new SysPermissionDelegation(
            GuidGenerator.Create(),
            me,
            input.DelegateeUserId,
            input.PermissionName)
        {
            DelegatorName = meUser?.Name ?? _currentUser.UserName,
            DelegateeName = delegatee.Name,
            ExpiresAt = input.ExpiresAt,
            Remark = input.Remark
        };

        await _delegations.InsertAsync(entity, autoSave: true);

        // 台账落库之后再授权：反过来的话，授权成功但台账写失败会留下一条查不到的授权
        await _manager.GrantAsync(entity);

        return await ToDtoAsync(entity, me);
    }

    /// <inheritdoc />
    public async Task<PermissionDelegationDto> RevokeAsync(Guid id)
    {
        var me = RequireUserId();

        var entity = await _delegations.GetAsync(id);

        var isMine = entity.DelegatorUserId == me;
        var isAdmin = _currentUser.Roles.Contains(SuperAdminRole, StringComparer.OrdinalIgnoreCase);

        // 只有借出人本人（或管理员，用于离职等场景）能撤销 ——
        // 被委派人自己不能"退回"（那会让台账失去借出方的意志）
        if (!isMine && !isAdmin)
        {
            throw new BusinessException(XjglDomainErrorCodes.TaskNotAssignedToYou)
                .WithData("reason", "只能撤销自己委派出去的权限。");
        }

        if (!entity.IsRevoked)
        {
            entity.MarkRevoked(_clock.Now, isMine ? "已撤销" : "管理员撤销");

            await _delegations.UpdateAsync(entity, autoSave: true);

            await _manager.RevokeGrantIfUnusedAsync(entity);
        }

        return await ToDtoAsync(entity, me);
    }

    // ------------------------------------------------------------------
    // 内部实现
    // ------------------------------------------------------------------

    private Guid RequireUserId()
        => _currentUser.Id
           ?? throw new BusinessException(XjglDomainErrorCodes.DataNotFound)
               .WithData("reason", "未登录。");

    private async Task<PermissionDelegationDto> ToDtoAsync(SysPermissionDelegation row, Guid me)
    {
        var now = _clock.Now;
        var displayNames = BuildDisplayNameMap(_metadataCache.GetSnapshot());

        return new PermissionDelegationDto
        {
            Id = row.Id,
            DelegatorUserId = row.DelegatorUserId,
            DelegatorName = row.DelegatorName,
            DelegateeUserId = row.DelegateeUserId,
            DelegateeName = row.DelegateeName,
            PermissionName = row.PermissionName,
            // 查不到就回落成权限码：显示一个代码总比显示空白强（空白会被读成"没有权限"）
            PermissionDisplayName = displayNames.GetValueOrDefault(row.PermissionName),
            ExpiresAt = row.ExpiresAt,
            IsRevoked = row.IsRevoked,
            RevokedAt = row.RevokedAt,
            RevokedReason = row.RevokedReason,
            IsActive = row.IsActive(now),
            IsMine = row.DelegatorUserId == me,
            Remark = row.Remark,
            CreationTime = row.CreationTime
        };
    }

    /// <summary>
    /// 权限码 → 中文名（取自元数据快照的操作名）。
    ///
    /// 模块级权限（如 `Stu.Student`）在快照里也有名字，一并收进来。
    /// </summary>
    private static Dictionary<string, string> BuildDisplayNameMap(ModuleMetadataSnapshot snapshot)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var subsystem in snapshot.Subsystems)
        {
            foreach (var module in subsystem.Modules)
            {
                map[module.PermissionName] = $"{subsystem.Name} · {module.Name}";

                foreach (var action in module.Actions)
                {
                    map[action.PermissionName] = $"{module.Name} · {action.Name}";
                }
            }
        }

        return map;
    }
}
