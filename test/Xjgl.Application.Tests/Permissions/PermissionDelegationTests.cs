using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Identity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Uow;
using Xjgl.Data;
using Xunit;

namespace Xjgl.Permissions;

/// <summary>
/// 权限委派的集成测试（ROADMAP 第 19 条）。
///
/// 【这组用例真正守的是什么】
/// 委派同时改**两处**：本项目的台账表 + ABP 的**授权存储**（委派靠用户级授权才能让
/// `[Authorize]` 生效）。两处只要有一处没跟上，就会出现最难发现的两类错误：
///   - 台账写了、授权没给 → "委派了但他说没权限"（用户会反复找你）；
///   - 撤销了台账、授权还在 → **权限收不回来**（安全事件，且台账上看着已经撤销了）。
/// 因此下面每条"生效/失效"的断言都查**授权存储本身**（`IPermissionChecker`），
/// 而不是只看台账字段。
/// </summary>
public class PermissionDelegationTests : XjglIntegrationTestBase
{
    /// <summary>一条**允许委派**的权限（业务审核类）。</summary>
    private const string Delegatable = MetadataPermissions.Chg.Audit.Approve;

    /// <summary>一条**不允许委派**的权限（系统管理类）。</summary>
    private const string NotDelegatable = MetadataPermissions.Sys.User.Create;

    private readonly IPermissionDelegationAppService _service;
    private readonly IPermissionManager _permissionManager;
    private readonly IdentityUserManager _userManager;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly FakeCurrentPrincipalAccessor _principal;
    private readonly IXjglRepository<SysPermissionDelegation> _delegations;

    public PermissionDelegationTests()
    {
        _service = GetRequiredService<IPermissionDelegationAppService>();
        _permissionManager = GetRequiredService<IPermissionManager>();
        _userManager = GetRequiredService<IdentityUserManager>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();
        _principal = GetRequiredService<FakeCurrentPrincipalAccessor>();
        _delegations = GetRequiredService<IXjglRepository<SysPermissionDelegation>>();
    }

    // ------------------------------------------------------------------
    // 脚手架
    // ------------------------------------------------------------------

    /// <summary>
    /// 以指定用户身份执行：换 principal + 新开 DI 作用域，并在**同一个显式工作单元**里
    /// 先授予 <paramref name="grants"/>（模拟"他本来就有这些权限"）、再执行动作、最后提交。
    ///
    /// 【为什么必须在同一个 UoW 里】
    /// `IPermissionManager.SetAsync` 是**写库**（写授权表），它需要一个活动的工作单元才会提交；
    /// 而换身份必须新开作用域（`ICurrentUser` 与权限缓存都是 Scoped）。
    /// 两者凑在一起时，如果授权写在"上一个"作用域、检查发生在"下一个"作用域，
    /// 后者看不到尚未提交的授权 —— 报错是"你没有权限"，看起来像子集约束写错了，
    /// 实际是**测试自己的事务边界问题**（本类第一版 6 条用例全红就是这么来的）。
    /// </summary>
    private async Task<T> AsUserAsync<T>(
        Guid userId,
        Func<IServiceProvider, Task<T>> action,
        params string[] grants)
    {
        _principal.SetUser(userId, $"delegation_{userId:N}"[..20]);

        using var scope = ServiceProvider.CreateScope();

        var provider = scope.ServiceProvider;
        var uow = provider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);

        try
        {
            var manager = provider.GetRequiredService<IPermissionManager>();

            foreach (var permission in grants)
            {
                await manager.SetAsync(
                    permission, UserPermissionValueProvider.ProviderName, userId.ToString(), true);
            }

            var result = await action(provider);

            await uow.CompleteAsync();

            return result;
        }
        finally
        {
            uow.Dispose();
        }
    }

    private Task AsUserAsync(Guid userId, Func<IServiceProvider, Task> action, params string[] grants)
        => AsUserAsync(userId, async sp =>
        {
            await action(sp);
            return true;
        }, grants);

    private static Task<bool> IsGrantedAsync(IServiceProvider provider, string permissionName)
        => provider.GetRequiredService<IPermissionChecker>().IsGrantedAsync(permissionName);

    /// <summary>在被委派人身份下查"他到底有没有这条权限"（走授权存储，不是台账）。</summary>
    private Task<bool> DelegateeHasAsync(Guid delegatee, string permissionName)
        => AsUserAsync(delegatee, sp => IsGrantedAsync(sp, permissionName));

    /// <summary>
    /// 造一个**真实存在**的用户。
    ///
    /// ⚠ 委派会校验"被委派人存在"（`IdentityUserManager.FindByIdAsync`），
    /// 所以不能拿随机 GUID 当被委派人 —— 那会以"被委派的用户不存在"失败，
    /// 而报错的位置看起来像子集约束那一段。本类第一版就是这么写的（6 条用例全红）。
    /// </summary>
    private async Task<IdentityUser> CreateUserAsync()
    {
        var userName = "deleg_" + Guid.NewGuid().ToString("N")[..10];

        var user = new IdentityUser(Guid.NewGuid(), userName, $"{userName}@xjgl.local")
        {
            Name = "委派测试用户"
        };

        var result = await _userManager.CreateAsync(user, "1q2w3E*");

        result.Succeeded.ShouldBeTrue(string.Join("；", result.Errors.Select(e => e.Description)));

        return user;
    }

    /// <summary>
    /// 给某个**用户**授一条权限（测试里代替"他本来就有这个权限"）。
    ///
    /// **必须显式提交**：授权是写库，而后面每个身份都是**新作用域**（各自的工作单元），
    /// 不提交的话它们看不到这条授权 —— 报错会是"你没有权限"，
    /// 看起来像子集约束写错了，实际是测试自己的事务边界问题。
    /// </summary>
    private async Task GrantAsync(Guid userId, string permissionName)
    {
        var uow = _unitOfWorkManager.Begin(requiresNew: true);

        try
        {
            await _permissionManager.SetAsync(
                permissionName,
                UserPermissionValueProvider.ProviderName,
                userId.ToString(),
                true);

            await uow.CompleteAsync();
        }
        finally
        {
            uow.Dispose();
        }
    }

    // ==================================================================
    // 委派规则（允许清单）
    // ==================================================================

    /// <summary>
    /// 默认**不可委派**，只有明确的业务办理类可以。
    ///
    /// 方向很重要：记错一个开关的代价是"把系统管理权限借出去了"，
    /// 而那种错误在界面上完全看不出来（委派记录看起来一切正常）。
    /// </summary>
    [Fact]
    public void 委派规则_导出与系统管理类不可委派()
    {
        // 业务办理类：可委派
        PermissionDelegationRules.IsDelegatable(MetadataPermissions.Chg.Audit.Approve).ShouldBeTrue();
        PermissionDelegationRules.IsDelegatable(MetadataPermissions.Stu.Student.Create).ShouldBeTrue();
        PermissionDelegationRules.IsDelegatable(MetadataPermissions.Stu.Student.Query).ShouldBeTrue();

        // 导出类：不可委派（等于把数据带走）
        PermissionDelegationRules.IsDelegatable(MetadataPermissions.Stu.Student.Export).ShouldBeFalse();
        PermissionDelegationRules.IsDelegatable(MetadataPermissions.Pho.Photo.Export).ShouldBeFalse();

        // 系统管理类：不可委派
        PermissionDelegationRules.IsDelegatable(MetadataPermissions.Sys.User.Create).ShouldBeFalse();
        PermissionDelegationRules.IsDelegatable(MetadataPermissions.Sys.Role.Update).ShouldBeFalse();
        PermissionDelegationRules.IsDelegatable(MetadataPermissions.Sys.Org.Update).ShouldBeFalse();

        // 运维动作：不可委派
        PermissionDelegationRules.IsDelegatable(MetadataPermissions.Pho.Photo.Compensate).ShouldBeFalse();

        // 原因要能说清"为什么不行"
        PermissionDelegationRules.DescribeWhyNot(MetadataPermissions.Stu.Student.Export)
            .ShouldContain("导出");
        PermissionDelegationRules.DescribeWhyNot(MetadataPermissions.Sys.User.Create)
            .ShouldContain("系统管理");
    }

    // ==================================================================
    // 新建
    // ==================================================================

    [Fact]
    public async Task 委派后_被委派人真的拿到权限()
    {
        var delegator = (await CreateUserAsync()).Id;
        var delegatee = (await CreateUserAsync()).Id;

        await GrantAsync(delegator, Delegatable);

        var created = await AsUserAsync(delegator, sp =>
            sp.GetRequiredService<IPermissionDelegationAppService>()
                .CreateAsync(new CreatePermissionDelegationDto
                {
                    DelegateeUserId = delegatee,
                    PermissionName = Delegatable,
                    Remark = "休假代审"
                }));

        created.PermissionName.ShouldBe(Delegatable);
        created.IsActive.ShouldBeTrue();
        created.IsMine.ShouldBeTrue();

        // ★ 关键：查**授权存储**，而不是只看台账
        (await AsUserAsync(delegatee, sp => IsGrantedAsync(sp, Delegatable)))
            .ShouldBeTrue("委派必须让 [Authorize] 真的通过（走 ABP 用户级授权）");
    }

    /// <summary>子集约束：只能借出自己拥有的权限，否则委派就成了提权。</summary>
    [Fact]
    public async Task 委派自己没有的权限_被拒绝()
    {
        var delegator = (await CreateUserAsync()).Id;
        var delegatee = (await CreateUserAsync()).Id;

        // 刻意不给 delegator 授这条权限
        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await AsUserAsync(delegator, sp =>
                sp.GetRequiredService<IPermissionDelegationAppService>()
                    .CreateAsync(new CreatePermissionDelegationDto
                    {
                        DelegateeUserId = delegatee,
                        PermissionName = Delegatable
                    })));

        ex.Data["reason"]!.ToString().ShouldContain("你没有权限");

        // 被拒绝之后当然也不能真的拿到权限
        (await AsUserAsync(delegatee, sp => IsGrantedAsync(sp, Delegatable))).ShouldBeFalse();
    }

    [Fact]
    public async Task 不允许委派的权限_即使自己有也被拒绝()
    {
        var delegator = (await CreateUserAsync()).Id;
        var delegatee = (await CreateUserAsync()).Id;

        // 让 delegator 拥有这条系统管理权限（模拟管理员）
        await GrantAsync(delegator, NotDelegatable);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await AsUserAsync(delegator, sp =>
                sp.GetRequiredService<IPermissionDelegationAppService>()
                    .CreateAsync(new CreatePermissionDelegationDto
                    {
                        DelegateeUserId = delegatee,
                        PermissionName = NotDelegatable
                    })));

        ex.Data["reason"]!.ToString().ShouldContain("系统管理");
    }

    [Fact]
    public async Task 不能委派给自己()
    {
        var me = (await CreateUserAsync()).Id;

        await GrantAsync(me, Delegatable);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await AsUserAsync(me, sp =>
                sp.GetRequiredService<IPermissionDelegationAppService>()
                    .CreateAsync(new CreatePermissionDelegationDto
                    {
                        DelegateeUserId = me,
                        PermissionName = Delegatable
                    })));

        ex.Data["reason"]!.ToString().ShouldContain("自己");
    }

    [Fact]
    public async Task 重复委派同一条_被拒绝()
    {
        var delegator = (await CreateUserAsync()).Id;
        var delegatee = (await CreateUserAsync()).Id;

        await GrantAsync(delegator, Delegatable);

        await AsUserAsync(delegator, sp =>
            sp.GetRequiredService<IPermissionDelegationAppService>()
                .CreateAsync(new CreatePermissionDelegationDto
                {
                    DelegateeUserId = delegatee,
                    PermissionName = Delegatable
                }));

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await AsUserAsync(delegator, sp =>
                sp.GetRequiredService<IPermissionDelegationAppService>()
                    .CreateAsync(new CreatePermissionDelegationDto
                    {
                        DelegateeUserId = delegatee,
                        PermissionName = Delegatable
                    })));

        ex.Data["reason"]!.ToString().ShouldContain("仍在生效");
    }

    // ==================================================================
    // 撤销
    // ==================================================================

    [Fact]
    public async Task 撤销后_被委派人失去权限()
    {
        var delegator = (await CreateUserAsync()).Id;
        var delegatee = (await CreateUserAsync()).Id;

        await GrantAsync(delegator, Delegatable);

        var created = await AsUserAsync(delegator, sp =>
            sp.GetRequiredService<IPermissionDelegationAppService>()
                .CreateAsync(new CreatePermissionDelegationDto
                {
                    DelegateeUserId = delegatee,
                    PermissionName = Delegatable
                }));

        (await AsUserAsync(delegatee, sp => IsGrantedAsync(sp, Delegatable))).ShouldBeTrue();

        var revoked = await AsUserAsync(delegator, sp =>
            sp.GetRequiredService<IPermissionDelegationAppService>().RevokeAsync(created.Id));

        revoked.IsActive.ShouldBeFalse();
        revoked.RevokedReason.ShouldBe("已撤销");

        // ★ 关键：撤销必须**真的把授权收回来**（只在台账上标一下等于没收回）
        (await AsUserAsync(delegatee, sp => IsGrantedAsync(sp, Delegatable)))
            .ShouldBeFalse("撤销之后授权存储里也不能再有这条用户级授权");
    }

    /// <summary>
    /// 同一个人 + 同一条权限有**两条生效委派**时，撤销其中一条**不能**把权限收走 ——
    /// 否则"撤销我借出去的那条"会顺手让别人的委派失效，而台账上两条看起来互不相干。
    /// </summary>
    [Fact]
    public async Task 还有另一条生效委派时_撤销其一不收回权限()
    {
        var delegatorA = (await CreateUserAsync()).Id;
        var delegatorB = (await CreateUserAsync()).Id;
        var delegatee = (await CreateUserAsync()).Id;

        await GrantAsync(delegatorA, Delegatable);
        await GrantAsync(delegatorB, Delegatable);

        var first = await AsUserAsync(delegatorA, sp =>
            sp.GetRequiredService<IPermissionDelegationAppService>()
                .CreateAsync(new CreatePermissionDelegationDto
                {
                    DelegateeUserId = delegatee, PermissionName = Delegatable
                }));

        await AsUserAsync(delegatorB, sp =>
            sp.GetRequiredService<IPermissionDelegationAppService>()
                .CreateAsync(new CreatePermissionDelegationDto
                {
                    DelegateeUserId = delegatee, PermissionName = Delegatable
                }));

        // 撤掉 A 借出的那条
        await AsUserAsync(delegatorA, sp =>
            sp.GetRequiredService<IPermissionDelegationAppService>().RevokeAsync(first.Id));

        (await AsUserAsync(delegatee, sp => IsGrantedAsync(sp, Delegatable)))
            .ShouldBeTrue("B 借出的那条仍在生效，权限不能被收回");
    }

    /// <summary>只有借出人本人（或管理员）能撤销 —— 被委派人不能自己"退回"。</summary>
    [Fact]
    public async Task 被委派人不能自己撤销()
    {
        var delegator = (await CreateUserAsync()).Id;
        var delegatee = (await CreateUserAsync()).Id;

        await GrantAsync(delegator, Delegatable);

        var created = await AsUserAsync(delegator, sp =>
            sp.GetRequiredService<IPermissionDelegationAppService>()
                .CreateAsync(new CreatePermissionDelegationDto
                {
                    DelegateeUserId = delegatee, PermissionName = Delegatable
                }));

        await Should.ThrowAsync<BusinessException>(async () =>
            await AsUserAsync(delegatee, sp =>
                sp.GetRequiredService<IPermissionDelegationAppService>().RevokeAsync(created.Id)));
    }

    // ==================================================================
    // 查询
    // ==================================================================

    /// <summary>两个方向都要能看到：我借出的（能撤销）+ 别人借给我的（知道自己为什么多了权限）。</summary>
    [Fact]
    public async Task 列表_包含借出与借入两个方向()
    {
        var delegator = (await CreateUserAsync()).Id;
        var delegatee = (await CreateUserAsync()).Id;

        await GrantAsync(delegator, Delegatable);

        var created = await AsUserAsync(delegator, sp =>
            sp.GetRequiredService<IPermissionDelegationAppService>()
                .CreateAsync(new CreatePermissionDelegationDto
                {
                    DelegateeUserId = delegatee, PermissionName = Delegatable
                }));

        var mine = await AsUserAsync(delegator, sp =>
            sp.GetRequiredService<IPermissionDelegationAppService>().GetListAsync());

        mine.ShouldContain(d => d.Id == created.Id && d.IsMine, "借出方要能看到自己借出去的");

        var theirs = await AsUserAsync(delegatee, sp =>
            sp.GetRequiredService<IPermissionDelegationAppService>().GetListAsync());

        theirs.ShouldContain(d => d.Id == created.Id && !d.IsMine, "被委派人要能看到别人借给自己的");
    }

    /// <summary>下拉里只列"我拥有的 ∩ 允许委派的"：出现一个我根本没有的权限，提交时才被拒最让人困惑。</summary>
    [Fact]
    public async Task 可委派权限列表_只列自己拥有的且允许委派的()
    {
        var me = (await CreateUserAsync()).Id;

        await GrantAsync(me, Delegatable);
        await GrantAsync(me, MetadataPermissions.Stu.Student.Export);

        var list = await AsUserAsync(me, sp =>
            sp.GetRequiredService<IPermissionDelegationAppService>().GetDelegatablePermissionsAsync());

        list.ShouldContain(p => p.PermissionName == Delegatable);
        list.ShouldNotContain(p => p.PermissionName == MetadataPermissions.Stu.Student.Export,
            "导出类不可委派，不该出现在下拉里");

        // 中文名要带上（下拉直接显示"异动审核 · 审核"这种可读文案）
        list.Single(p => p.PermissionName == Delegatable).DisplayName.ShouldNotBeNullOrWhiteSpace();
    }
}
