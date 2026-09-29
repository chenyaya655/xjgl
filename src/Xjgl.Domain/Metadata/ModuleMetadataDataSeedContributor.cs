using Microsoft.Extensions.Logging;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Guids;
using Volo.Abp.PermissionManagement;
using Xjgl.Data;

namespace Xjgl.Metadata;

/// <summary>
/// 模块元数据种子：子系统 → 模块 → 操作。
///
/// 权限名规则：<c>{子系统编码}.{模块编码}.{操作编码}</c>，
/// 例如 <c>Stu.Student.Import</c>、<c>Chg.Audit.Approve</c>。
///
/// 这几个子系统是「内置」的：管理员可以在界面上调整名称、路径、排序、启停，
/// 也可以新增自己的模块；种子只保证首次部署后系统可用。
///
/// 幂等：已存在的子系统/模块/操作不会重复插入，也不会覆盖人工修改。
/// </summary>
public class ModuleMetadataDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly IXjglRepository<SysSubsystem> _subsystems;
    private readonly IXjglRepository<SysModule> _modules;
    private readonly IXjglRepository<SysModuleAction> _actions;
    private readonly IPermissionDataSeeder _permissionDataSeeder;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ILogger<ModuleMetadataDataSeedContributor> _logger;

    public ModuleMetadataDataSeedContributor(
        IXjglRepository<SysSubsystem> subsystems,
        IXjglRepository<SysModule> modules,
        IXjglRepository<SysModuleAction> actions,
        IPermissionDataSeeder permissionDataSeeder,
        IGuidGenerator guidGenerator,
        ILogger<ModuleMetadataDataSeedContributor> logger)
    {
        _subsystems = subsystems;
        _modules = modules;
        _actions = actions;
        _permissionDataSeeder = permissionDataSeeder;
        _guidGenerator = guidGenerator;
        _logger = logger;
    }

    public async Task SeedAsync(DataSeedContext context)
    {
        // ---- 系统管理 ----
        await AddSubsystemAsync("Sys", "系统管理", "Setting", 10, new[]
        {
            Module("Module", "模块管理", "/system/module", "system/module/index", "Grid", 10,
                Action("Query", "查看"), Action("Create", "添加"), Action("Update", "修改"), Action("Delete", "删除")),

            Module("Permission", "权限管理", "/system/permission", "system/permission/index", "Lock", 20,
                Action("Query", "查看"), Action("Assign", "分配权限")),

            //
            // ★ 角色管理：为什么用业务权限 `Sys.Role`，而不是 ABP 的 `AbpIdentity.Roles`
            //
            //   菜单是被 `MyProfileAppService.BuildMenus` 按**模块权限名**过滤的，
            //   而它只认元数据里定义的权限（`snapshot.AllPermissionNames`）。
            //   ABP 内置的 `AbpIdentity.Roles` **不在**元数据快照里 —— 也就是说，
            //   若把本模块的权限名写成 `AbpIdentity.Roles`，过滤时会取到空值，
            //   菜单**永远不会出现**（而且不报错，属于静默失效）。
            //
            //   代价是这里存在**两个权限层次**，必须知道：
            //     - 菜单能否看见  → 业务权限 `Sys.Role`（本行定义，可正常授权/显示）；
            //     - 接口能否调用  → ABP 内置 `AbpIdentity.Roles`（框架控制器自带的 `[Authorize]`）。
            //   本页面调用的是 ABP 自带的 `/api/identity/roles`，所以**两个都要有**。
            //   种子把 admin 全部授予；演示角色按需授予（见 DemoDataSeedContributor）。
            //
            //   这个坑是实测出来的：演示账号没有 `AbpIdentity.Roles` 时，
            //   请求 `/api/identity/roles` **不是 403 而是 302 跳登录页**，
            //   前端 axios 跟随重定向后拿到 HTML、解 JSON 抛错，
            //   而页面里写着 `catch { roles.value = [] }` —— 于是下拉框空白、
            //   一句提示都没有，看起来就像"这个功能不存在"。
            Module("Role", "角色管理", "/system/role", "system/role/index", "Avatar", 25,
                Action("Query", "查看"), Action("Create", "添加"), Action("Update", "修改"), Action("Delete", "删除")),

            // 账号清单导出（ROADMAP 第 17 条）真的做了才声明 Export；
            // **Import 刻意不做**（直接导入账号会绕过"预置 + 本人激活"里
            // 手机号/姓名/身份证的核对），理由写在 MetadataPermissions.Sys.User 里。
            Module("User", "用户管理", "/system/user", "system/user/index", "User", 30,
                Action("Query", "查看"), Action("Create", "添加"), Action("Update", "修改"), Action("Delete", "删除"),
                Action("Export", "导出")),

            Module("UserProvision", "预置与激活", "/system/user-provision", "system/user-provision/index", "UserFilled", 40,
                Action("Query", "查看"), Action("Create", "添加"), Action("Update", "修改"), Action("Delete", "删除"),
                Action("Import", "导入"),
                // 预置名单导出：列与"本人要核对的身份信息"一致（身份证只出后 4 位）
                Action("Export", "导出")),

            Module("Code", "微代码管理", "/system/code", "system/code/index", "Collection", 50,
                Action("Query", "查看"), Action("Create", "添加"), Action("Update", "修改"), Action("Delete", "删除"),
                // 与组织同理：实现了才声明。码表是环境相关的数据（测试调好的要搬去生产）
                Action("Import", "导入"), Action("Export", "导出")),

            Module("Org", "组织管理", "/system/org", "system/org/index", "OfficeBuilding", 60,
                Action("Query", "查看"), Action("Create", "添加"), Action("Update", "修改"), Action("Delete", "删除"),
                // 导入导出是**真的实现了**才声明（ROADMAP 第 17 条）：
                // 组织/学校名录导入是部署前置条件 —— 上万所学校不可能靠界面一个个点。
                Action("Import", "导入"), Action("Export", "导出"))
        });

        // ---- 学生管理 ----
        await AddSubsystemAsync("Stu", "学生管理", "School", 20, new[]
        {
            Module("Student", "学生管理", "/student/list", "student/list/index", "User", 10,
                Action("Query", "查看"), Action("Create", "添加"), Action("Update", "修改"), Action("Delete", "删除"),
                Action("Import", "导入"), Action("Export", "导出"), Action("Print", "打印"))
        });

        // ---- 异动管理 ----
        await AddSubsystemAsync("Chg", "异动管理", "Switch", 30, new[]
        {
            Module("Type", "异动类型管理", "/change/type", "change/type/index", "Document", 10,
                Action("Query", "查看"), Action("Create", "添加"), Action("Update", "修改"), Action("Delete", "删除")),

            Module("Apply", "异动申请", "/change/apply", "change/apply/index", "EditPen", 20,
                Action("Query", "查看"), Action("Create", "添加"), Action("Update", "修改"), Action("Delete", "删除"),
                // 导出是**真的实现了**才声明（ROADMAP 第 17 条）
                Action("Export", "导出")),

            Module("Audit", "异动审核", "/change/audit", "change/audit/index", "CircleCheck", 30,
                Action("Query", "查看"), Action("Approve", "审核"), Action("RevokeApprove", "撤销审核"),
                // 审核**流水**导出：一个审批节点一行，与申请单导出是两份不同的产物
                Action("Export", "导出"))
        });

        // ---- 照片管理 ----
        await AddSubsystemAsync("Pho", "照片管理", "Picture", 40, new[]
        {
            Module("Photo", "照片管理", "/photo/list", "photo/list/index", "PictureFilled", 10,
                Action("Query", "查看"), Action("Upload", "上传"), Action("Delete", "删除"), Action("Export", "导出"),
                Action("Compensate", "存储补偿"))
        });

        await AddSubsystemAsync("Rpt", "统计报表", "DataAnalysis", 50, new[]
        {
            Module("Student", "学生统计", "/report/student", "report/student/index", "PieChart", 10,
                Action("Query", "查看")),
            Module("Change", "异动统计", "/report/change", "report/change/index", "TrendCharts", 20,
                Action("Query", "查看"))
        });

        var subsystemCount = await _subsystems.GetCountAsync();
        var moduleCount = await _modules.GetCountAsync();
        var actionCount = await _actions.GetCountAsync();

        _logger.LogInformation(
            "[模块元数据] 当前共有 {Subsystems} 个子系统、{Modules} 个模块、{Actions} 个操作。",
            subsystemCount, moduleCount, actionCount);

        await GrantAllModulePermissionsToAdminAsync();
    }

    /// <summary>
    /// 把全部模块权限授予内置的 admin 角色。
    ///
    /// 为什么必须在种子阶段显式做这件事：
    ///   ABP 的 Identity 种子只会把**应用启动时已定义**的权限授予 admin 角色，
    ///   而动态权限定义是在启动时从元数据表读出来的 —— 全新数据库首次执行
    ///   DbMigrator 时表还没建，读到的是空快照，于是 admin 一条业务权限都拿不到。
    ///   这里在元数据写完之后直接按库里的权限名补授，保证全新安装后 admin 可用。
    ///
    /// 幂等：IPermissionDataSeeder 内部对已存在的授权不会重复写。
    /// </summary>
    private async Task GrantAllModulePermissionsToAdminAsync()
    {
        var modules = await _modules.GetListAsync();
        var actions = await _actions.GetListAsync();

        var permissionNames = modules.Select(m => m.PermissionName)
            .Concat(actions.Select(a => a.PermissionName))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (permissionNames.Length == 0)
        {
            return;
        }

        await _permissionDataSeeder.SeedAsync(
            RolePermissionValueProvider.ProviderName,
            "admin",
            permissionNames);

        _logger.LogInformation(
            "[模块元数据] 已确保 admin 角色拥有全部 {Count} 个模块权限。",
            permissionNames.Length);
    }

    private async Task AddSubsystemAsync(
        string code,
        string name,
        string? icon,
        int sort,
        ModuleSeed[] moduleSeeds)
    {
        var subsystem = await _subsystems.FindAsync(s => s.Code == code);

        if (subsystem == null)
        {
            subsystem = new SysSubsystem(_guidGenerator.Create(), code, name, icon, sort);
            await _subsystems.InsertAsync(subsystem, autoSave: true);
        }

        var existingModules = await _modules.GetListAsync(m => m.SubsystemId == subsystem.Id);
        var existingByCode = existingModules.ToDictionary(m => m.Code, StringComparer.Ordinal);

        foreach (var moduleSeed in moduleSeeds)
        {
            if (existingByCode.TryGetValue(moduleSeed.Code, out var existingModule))
            {
                await EnsureActionsAsync(existingModule, code, moduleSeed);
                continue;
            }

            var module = new SysModule(
                _guidGenerator.Create(),
                subsystem.Id,
                moduleSeed.Code,
                moduleSeed.Name,
                $"{code}.{moduleSeed.Code}",
                moduleSeed.Path,
                moduleSeed.Component,
                moduleSeed.Sort)
            {
                Icon = moduleSeed.Icon
            };

            await _modules.InsertAsync(module, autoSave: true);
            await EnsureActionsAsync(module, code, moduleSeed);
        }
    }

    private async Task EnsureActionsAsync(SysModule module, string subsystemCode, ModuleSeed moduleSeed)
    {
        var existing = await _actions.GetListAsync(a => a.ModuleId == module.Id);
        var existingCodes = existing.Select(a => a.Code).ToHashSet(StringComparer.Ordinal);

        var order = 0;
        foreach (var actionSeed in moduleSeed.Actions)
        {
            order += 10;

            if (existingCodes.Contains(actionSeed.Code))
            {
                continue;
            }

            var action = new SysModuleAction(
                _guidGenerator.Create(),
                module.Id,
                actionSeed.Code,
                actionSeed.Name,
                actionSeed.ActionType,
                $"{subsystemCode}.{moduleSeed.Code}.{actionSeed.Code}",
                order);

            await _actions.InsertAsync(action, autoSave: true);
        }
    }

    // ---- 种子定义辅助 ----

    private static ModuleSeed Module(
        string code,
        string name,
        string? path,
        string? component,
        string? icon,
        int sort,
        params ActionSeed[] actions)
    {
        return new ModuleSeed(code, name, path, component, icon, sort, actions);
    }

    private static ActionSeed Action(string actionType, string name)
    {
        return new ActionSeed(actionType, name);
    }

    private sealed record ModuleSeed(
        string Code,
        string Name,
        string? Path,
        string? Component,
        string? Icon,
        int Sort,
        ActionSeed[] Actions);

    private sealed record ActionSeed(string ActionType, string Name)
    {
        /// <summary>操作编码与操作类型同名（Query/Create/Update/...）。</summary>
        public string Code => ActionType;
    }
}
