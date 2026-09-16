using Shouldly;
using Volo.Abp;
using Volo.Abp.Authorization.Permissions;
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
