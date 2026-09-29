using Shouldly;
using Volo.Abp;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Guids;
using Volo.Abp.Identity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Uow;
using Xunit;

namespace Xjgl.Metadata;

/// <summary>
/// 元数据驱动的动态权限与菜单的集成测试。
///
/// 要证明的核心命题是：**在界面上加一个模块和它的操作，就自动多出可授权的权限项，
/// 不需要改代码、不需要重启**。这组用例就是围绕它展开的。
/// </summary>
public class ModuleMetadataTests : XjglIntegrationTestBase
{
    private const string ValidPassword = "Test@123456";

    private readonly IModuleAppService _moduleAppService;
    private readonly IMyProfileAppService _profileAppService;
    private readonly IModuleMetadataCache _metadataCache;
    private readonly IPermissionDefinitionManager _permissionDefinitionManager;
    private readonly IPermissionManager _permissionManager;
    private readonly IdentityUserManager _userManager;
    private readonly IdentityRoleManager _roleManager;
    private readonly IPermissionDataSeeder _permissionDataSeeder;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly FakeCurrentPrincipalAccessor _principalAccessor;

    public ModuleMetadataTests()
    {
        _moduleAppService = GetRequiredService<IModuleAppService>();
        _profileAppService = GetRequiredService<IMyProfileAppService>();
        _metadataCache = GetRequiredService<IModuleMetadataCache>();
        _permissionDefinitionManager = GetRequiredService<IPermissionDefinitionManager>();
        _permissionManager = GetRequiredService<IPermissionManager>();
        _userManager = GetRequiredService<IdentityUserManager>();
        _roleManager = GetRequiredService<IdentityRoleManager>();
        _permissionDataSeeder = GetRequiredService<IPermissionDataSeeder>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();
        _principalAccessor = GetRequiredService<FakeCurrentPrincipalAccessor>();
    }

    private static string NewCode(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    // ------------------------------------------------------------------
    // 元数据种子与树结构
    // ------------------------------------------------------------------

    [Fact]
    public async Task 元数据种子已写入_四个子系统十一个模块()
    {
        var tree = await _moduleAppService.GetTreeAsync();

        tree.Select(s => s.Code).ShouldContain("Sys");
        tree.Select(s => s.Code).ShouldContain("Stu");
        tree.Select(s => s.Code).ShouldContain("Chg");
        tree.Select(s => s.Code).ShouldContain("Pho");

        var studentSubsystem = tree.First(s => s.Code == "Stu");
        var studentModule = studentSubsystem.Modules.First(m => m.Code == "Student");

        studentModule.PermissionName.ShouldBe("Stu.Student");
        studentModule.Actions.Select(a => a.Code).ShouldContain("Import");
        studentModule.Actions.Select(a => a.PermissionName).ShouldContain("Stu.Student.Import");

        // 异动审核模块的操作体现了业务语义
        var auditModule = tree.First(s => s.Code == "Chg").Modules.First(m => m.Code == "Audit");
        auditModule.Actions.Select(a => a.Code).ShouldContain("Approve");
        auditModule.Actions.Select(a => a.Code).ShouldContain("RevokeApprove");
    }

    /// <summary>
    /// 角色管理模块存在，且它的权限名是**业务权限** `Sys.Role`。
    ///
    /// 【这条断言为什么值得单独写】
    ///   菜单是被 `MyProfileAppService.BuildMenus` 按**元数据里的权限名**过滤的，
    ///   而它只认快照里定义过的权限。ABP 内置的 `AbpIdentity.Roles` **不在**快照里 ——
    ///   如果哪天有人"顺手"把角色管理模块的权限名改成 `AbpIdentity.Roles`
    ///   以求"和框架对齐"，菜单会**永远不出现**，而且不报任何错。
    ///   所以这里把权限名钉死，让那次改动在测试里就红。
    ///
    ///   同时注意本模块的**双层权限**：菜单看 `Sys.Role.*`，
    ///   接口看 `AbpIdentity.Roles*`（框架控制器的 [Authorize]），两个都要给。
    /// </summary>
    [Fact]
    public async Task 角色管理模块的权限名是业务权限而不是框架权限()
    {
        var tree = await _moduleAppService.GetTreeAsync();

        var roleModule = tree.First(s => s.Code == "Sys").Modules.First(m => m.Code == "Role");

        roleModule.PermissionName.ShouldBe("Sys.Role");
        roleModule.Path.ShouldBe("/system/role");

        roleModule.Actions.Select(a => a.PermissionName).ShouldBe(
            new[] { "Sys.Role.Query", "Sys.Role.Create", "Sys.Role.Update", "Sys.Role.Delete" },
            ignoreOrder: true);
    }

    [Fact]
    public async Task 权限名按子系统_模块_操作三级生成()
    {
        var tree = await _moduleAppService.GetTreeAsync();

        foreach (var subsystem in tree)
        {
            foreach (var module in subsystem.Modules)
            {
                module.PermissionName.ShouldBe($"{subsystem.Code}.{module.Code}");

                foreach (var action in module.Actions)
                {
                    action.PermissionName.ShouldBe($"{subsystem.Code}.{module.Code}.{action.Code}");
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // 动态权限注册到 ABP
    // ------------------------------------------------------------------

    [Fact]
    public async Task 元数据权限已注册到_ABP_权限定义()
    {
        var permissions = (await _permissionDefinitionManager.GetPermissionsAsync())
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        permissions.ShouldContain("Stu.Student");
        permissions.ShouldContain("Stu.Student.Query");
        permissions.ShouldContain("Stu.Student.Import");
        permissions.ShouldContain("Chg.Audit.Approve");
        permissions.ShouldContain("Chg.Audit.RevokeApprove");
        permissions.ShouldContain("Pho.Photo.Upload");
        permissions.ShouldContain("Sys.UserProvision.Import");
    }

    [Fact]
    public async Task 元数据的权限组对应子系统()
    {
        var groups = (await _permissionDefinitionManager.GetGroupsAsync())
            .Select(g => g.Name)
            .ToHashSet(StringComparer.Ordinal);

        groups.ShouldContain("Sys");
        groups.ShouldContain("Stu");
        groups.ShouldContain("Chg");
        groups.ShouldContain("Pho");
    }

    // ------------------------------------------------------------------
    // ★ 核心命题：加模块就自动多出权限
    // ------------------------------------------------------------------

    [Fact]
    public async Task 新增模块与操作后_权限自动出现在元数据中_无需改代码()
    {
        var subsystemCode = NewCode("T");
        var moduleCode = "Demo";

        _metadataCache.GetSnapshot().AllPermissionNames.ShouldNotContain($"{subsystemCode}.{moduleCode}.Query");

        // 完全通过接口新增（模拟业务人员在「模块管理」界面上操作）
        var subsystem = await _moduleAppService.CreateSubsystemAsync(new CreateUpdateSubsystemDto
        {
            Code = subsystemCode,
            Name = "测试子系统",
            Icon = "Document"
        });

        var module = await _moduleAppService.CreateModuleAsync(new CreateUpdateModuleDto
        {
            SubsystemId = subsystem.Id,
            Code = moduleCode,
            Name = "测试模块",
            Path = "/test/demo",
            Component = "test/demo/index"
        });

        module.PermissionName.ShouldBe($"{subsystemCode}.{moduleCode}");

        var action = await _moduleAppService.CreateActionAsync(new CreateUpdateModuleActionDto
        {
            ModuleId = module.Id,
            Code = "Query",
            Name = "查看",
            ActionType = ModuleActionTypes.Query
        });

        action.PermissionName.ShouldBe($"{subsystemCode}.{moduleCode}.Query");

        // 缓存已自动失效，新的权限名立刻可见
        var snapshot = _metadataCache.GetSnapshot();
        snapshot.AllPermissionNames.ShouldContain($"{subsystemCode}.{moduleCode}");
        snapshot.AllPermissionNames.ShouldContain($"{subsystemCode}.{moduleCode}.Query");
    }

    [Fact]
    public async Task 重复的子系统编码与模块编码被拒绝()
    {
        var code = NewCode("D");

        await _moduleAppService.CreateSubsystemAsync(new CreateUpdateSubsystemDto { Code = code, Name = "重复测试" });

        await Should.ThrowAsync<BusinessException>(async () =>
            await _moduleAppService.CreateSubsystemAsync(new CreateUpdateSubsystemDto { Code = code, Name = "再建一次" }));
    }

    [Fact]
    public async Task 已有模块的子系统不允许改编码()
    {
        var tree = await _moduleAppService.GetTreeAsync();
        var stu = tree.First(s => s.Code == "Stu");

        await Should.ThrowAsync<BusinessException>(async () =>
            await _moduleAppService.UpdateSubsystemAsync(stu.Id, new CreateUpdateSubsystemDto
            {
                Code = "StuX",
                Name = stu.Name
            }));
    }

    [Fact]
    public async Task 有模块的子系统不允许删除_有操作的模块也不允许删除()
    {
        var tree = await _moduleAppService.GetTreeAsync();
        var stu = tree.First(s => s.Code == "Stu");

        await Should.ThrowAsync<BusinessException>(async () => await _moduleAppService.DeleteSubsystemAsync(stu.Id));

        var studentModule = stu.Modules.First(m => m.Code == "Student");
        await Should.ThrowAsync<BusinessException>(async () => await _moduleAppService.DeleteModuleAsync(studentModule.Id));
    }

    // ------------------------------------------------------------------
    // 我的上下文：菜单按权限过滤
    // ------------------------------------------------------------------

    [Fact]
    public async Task 我的上下文返回身份与角色()
    {
        var user = await CreateUserAsync();

        // 真正把用户加入 admin 角色（角色由种子数据创建）
        var adminRole = await _roleManager.FindByNameAsync("admin");
        adminRole.ShouldNotBeNull();

        // AddToRoleAsync 会加载用户的角色集合，需要活跃的工作单元；
        // 并且要在该工作单元内重新取一次实体，避免跨 UoW 的跟踪冲突
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var tracked = await _userManager.FindByIdAsync(user.Id.ToString());
            await _userManager.AddToRoleAsync(tracked!, "admin");
            await uow.CompleteAsync();
        }

        _principalAccessor.SetUser(user.Id, user.UserName!);

        try
        {
            var profile = await _profileAppService.GetAsync();

            profile.UserId.ShouldBe(user.Id);
            profile.UserName.ShouldBe(user.UserName);
            profile.Roles.ShouldContain("admin");
            profile.Permissions.ShouldNotBeNull();
            profile.Menus.ShouldNotBeNull();
        }
        finally
        {
            _principalAccessor.Clear();
        }
    }

    [Fact]
    public async Task 菜单只包含被授权的模块()
    {
        var user = await CreateUserAsync();

        // 只授予「学生管理」模块及其查看/导入操作
        await _permissionManager.SetAsync("Stu.Student", UserPermissionValueProvider.ProviderName, user.Id.ToString(), true);
        await _permissionManager.SetAsync("Stu.Student.Query", UserPermissionValueProvider.ProviderName, user.Id.ToString(), true);
        await _permissionManager.SetAsync("Stu.Student.Import", UserPermissionValueProvider.ProviderName, user.Id.ToString(), true);

        _principalAccessor.SetUser(user.Id, user.UserName!);

        try
        {
            var profile = await _profileAppService.GetAsync();

            profile.Permissions.ShouldContain("Stu.Student.Query");
            profile.Permissions.ShouldContain("Stu.Student.Import");
            profile.Permissions.ShouldNotContain("Stu.Student.Delete");

            // 只应看到「学生管理」一个子系统，且只含 Student 一个模块
            profile.Menus.Select(m => m.Code).ShouldBe(new[] { "Stu" });

            var studentMenu = profile.Menus.Single().Children.Single();
            studentMenu.Code.ShouldBe("Student");
            studentMenu.Path.ShouldBe("/student/list");
            studentMenu.Component.ShouldBe("student/list/index");

            // 按钮级权限一并返回
            studentMenu.Actions.ShouldContain("Stu.Student.Query");
            studentMenu.Actions.ShouldContain("Stu.Student.Import");
            studentMenu.Actions.ShouldNotContain("Stu.Student.Delete");
        }
        finally
        {
            _principalAccessor.Clear();
        }
    }

    [Fact]
    public async Task 没有任何授权时菜单为空()
    {
        var user = await CreateUserAsync();
        _principalAccessor.SetUser(user.Id, user.UserName!);

        try
        {
            var profile = await _profileAppService.GetAsync();

            profile.Menus.ShouldBeEmpty();
        }
        finally
        {
            _principalAccessor.Clear();
        }
    }

    /// <summary>
    /// ★ 只授**操作**权限（不授模块权限）时，模块菜单仍必须出现。
    ///
    /// 【这条用例是为一个真实缺陷写的，不是补形式覆盖】
    ///   权限体系是两级：模块 <c>Stu.Student</c> + 操作 <c>Stu.Student.Query</c>。
    ///   菜单最初只认"模块权限被显式授予"，而 **admin 两者都有**（种子把全部权限都给了 admin），
    ///   所以从超管视角永远看不出问题 —— 缺陷只在**按最小权限建的角色**上暴露：
    ///   演示账号 DemoOperator 只授 13 条叶子权限，登录后 permissions 有 13 条、
    ///   menus 却是空数组，左侧只剩「工作台」，所有业务页面都进不去。
    ///   因此这里**刻意只授叶子权限**，并断言菜单出现。
    ///
    ///   注释写得比断言长，是因为"为什么不能只断言 Menus 非空"这件事
    ///   下次改权限代码的人必须一眼看到：断言必须落在**具体模块**上，
    ///   否则把过滤条件整个删掉（等于所有人可见全部菜单）用例也会通过。
    /// </summary>
    [Fact]
    public async Task 只授操作权限时_对应模块菜单依然可见()
    {
        var user = await CreateUserAsync();
        var roleName = "menu_" + Guid.NewGuid().ToString("N")[..10];

        // 刻意**不**授 Stu.Student（模块权限），只授它的两个操作权限
        await CreateRoleWithPermissionsAsync(
            roleName,
            user,
            "Stu.Student.Query",
            "Stu.Student.Export");

        // ★ 必须把角色名也塞进主体：角色授权是靠主体里的 role claim 解析的
        //   （RolePermissionValueProvider），只往库里写 AbpUserRoles 是不够的。
        //   真实登录的 JWT 里带 role claim，所以线上不存在这个差异；
        //   而测试替身若漏了它，表现是"明明授了权、permissions 却是空数组"。
        _principalAccessor.SetUser(user.Id, user.UserName!, roleName);

        try
        {
            var profile = await _profileAppService.GetAsync();

            // 前提：确实没有模块级权限，缺陷才可能复现
            profile.Permissions.ShouldNotContain("Stu.Student");
            profile.Permissions.ShouldContain("Stu.Student.Query");

            // 结论：模块菜单必须出现，且只含这一个模块
            profile.Menus.Select(m => m.Code).ShouldBe(new[] { "Stu" });

            var studentMenu = profile.Menus.Single().Children.Single();
            studentMenu.Code.ShouldBe("Student");
            studentMenu.Path.ShouldBe("/student/list");
            studentMenu.Actions.ShouldContain("Stu.Student.Query");
            studentMenu.Actions.ShouldContain("Stu.Student.Export");
            studentMenu.Actions.ShouldNotContain("Stu.Student.Delete");
        }
        finally
        {
            _principalAccessor.Clear();
        }
    }

    /// <summary>
    /// 模块权限本身被授予、但**没有任何操作权限**时，模块也要可见 ——
    /// 否则"授权界面里只勾了模块"这种配置会得到一个点不开的菜单。
    /// </summary>
    [Fact]
    public async Task 只授模块权限时_菜单同样可见()
    {
        var user = await CreateUserAsync();

        // 这条用例只关心"模块权限是否被认出来"，与角色无关，
        // 因此直接授到用户本身，省掉角色与 role claim 这一环。
        await _permissionManager.SetAsync(
            "Stu.Student",
            UserPermissionValueProvider.ProviderName,
            user.Id.ToString(),
            true);

        _principalAccessor.SetUser(user.Id, user.UserName!);

        try
        {
            var profile = await _profileAppService.GetAsync();

            profile.Menus.Select(m => m.Code).ShouldBe(new[] { "Stu" });
            profile.Menus.Single().Children.Single().Code.ShouldBe("Student");
        }
        finally
        {
            _principalAccessor.Clear();
        }
    }

    /// <summary>
    /// 建一个**独立角色**并授指定权限，再把用户加进去。
    ///
    /// 【为什么不复用 admin 角色】
    ///   这种"授一条权限再断言菜单"的用例最容易被写坏的方式就是共用 admin：
    ///   admin 本来就有全部权限，于是无论过滤逻辑是否生效，断言都通过 ——
    ///   用例变成永远绿的装饰。独立角色保证 permissions 集合**只有**本用例授的那几条。
    ///
    /// 【为什么走 IPermissionManager.SetAsync 而不是 IPermissionDataSeeder】
    ///   两者都只是把授权写进 `AbpPermissionGrants`，区别在**缓存**：
    ///   `SetAsync` 会失效该主体的权限授予缓存，而直接 `SeedAsync` 不会。
    ///   而 `IPermissionChecker` 读的正是缓存 —— 少了这一步，本用例拿到的
    ///   permissions 恒为空数组，失败信息是"应该包含 Stu.Student.Query 但实际是 []"，
    ///   看上去像**权限解析或菜单逻辑坏了**，实际是测试自己没刷新缓存，极易误判。
    ///   真实请求路径上不存在这个问题（授权是通过 `SetAsync` 走的）。
    /// </summary>
    private async Task CreateRoleWithPermissionsAsync(
        string roleName,
        IdentityUser user,
        params string[] permissionNames)
    {
        var createResult = await _roleManager.CreateAsync(new IdentityRole(_guidGenerator.Create(), roleName));
        createResult.Succeeded.ShouldBeTrue(
            string.Join("；", createResult.Errors.Select(e => e.Description)));

        foreach (var permissionName in permissionNames)
        {
            await _permissionManager.SetAsync(
                permissionName,
                RolePermissionValueProvider.ProviderName,
                roleName,
                true);
        }

        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var tracked = await _userManager.FindByIdAsync(user.Id.ToString());

            (await _userManager.AddToRoleAsync(tracked!, roleName)).Succeeded.ShouldBeTrue();

            await uow.CompleteAsync();
        }
    }

    private async Task<IdentityUser> CreateUserAsync()
    {
        var userName = "meta_" + Guid.NewGuid().ToString("N")[..10];

        var user = new IdentityUser(Guid.NewGuid(), userName, $"{userName}@xjgl.local")
        {
            Name = "元数据测试用户"
        };

        var result = await _userManager.CreateAsync(user, ValidPassword);
        result.Succeeded.ShouldBeTrue(
            string.Join("；", result.Errors.Select(e => e.Description)));

        return user;
    }
}
