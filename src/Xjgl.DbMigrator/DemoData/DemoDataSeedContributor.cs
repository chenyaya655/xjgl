using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Guids;
using Volo.Abp.Identity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Security.Encryption;
using Volo.Abp.Settings;
using Volo.Abp.Timing;
using Volo.Abp.Uow;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Permissions;
using Xjgl.Sequences;
using Xjgl.Settings;
using Xjgl.Students;
using Xjgl.Text;
using Xjgl.Users;
using Xjgl.Validation;

namespace Xjgl.DemoData;

/// <summary>
/// 演示种子数据：一棵「示例省」组织树 + 逐级用户 + 年级班级 + 学生。
///
/// 【为什么放在 DbMigrator 而不是 Xjgl.Domain】
///   两个原因，缺一不可：
///   1) 它要引用 <see cref="MetadataPermissions"/>（属于 Application.Contracts），
///      而 Domain 引用 Application.Contracts 是分层倒挂；
///   2) 它是**开发/演示专用**的东西，不该和领域种子混在一起被当成生产逻辑维护。
///   本项目其余三个种子都在 Domain，它们是生产必需的（元数据、码表、OpenIddict 客户端），
///   这一个不是 —— 默认关闭、可整段删除而不影响任何功能。
///
/// 【为什么整段默认关闭】
///   它会在库里造出「看起来像真的」的组织与人员。生产环境误开的后果不是报错，
///   而是多出一个省厅、一批可登录账号 —— 这种错误不会有人及时发现。
///   所以默认值是 false，且开它的口令必须来自配置/环境变量，不进仓库。
///
/// 【幂等怎么做】
///   每类实体都有自己的自然键，绝不靠"跑一次记一个标记"：
///     组织→Code、用户→UserName、预置→PhoneNumber、年级→(校,学年,年级码)、
///     班级→(校,学年,班号)、学生→IdCardHash（与本系统既有的身份证查重口径一致）。
///   学生用 IdCardHash 还有一个额外好处：重复运行时**不会调用取号服务**，
///   因此 sys_sequence 的当前值不会因为重跑而漂移。
/// </summary>
[UnitOfWork]
public class DemoDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private const string EnabledConfigKey = "Xjgl:DemoData:Enabled";
    private const string PasswordConfigKey = "Xjgl:DemoData:Password";

    /// <summary>
    /// 是否**重置**已存在演示账号的口令（默认 false）。
    ///
    /// 【为什么需要这个开关 —— 一个真实的坑】
    /// `EnsureUserAsync` 对"已存在的用户"直接返回（幂等设计，是对的）。
    /// 但副作用是：**在演示库已经建好之后，改 `Xjgl:DemoData:Password` 不会生效** ——
    /// 账号口令停留在**第一次种子时**的那个值，而 seed 日志照样打印
    /// 「账号 15 个（建账号=True）」，看不出任何异常。
    /// 现场表现就是"文档里写的演示口令登不上，令牌端点回 invalid_grant"，
    /// 而 DemoData 的所有日志与计数都正常 —— 属于"看起来种好了、其实登不上"。
    ///
    /// 默认关闭的理由：口令重置是**破坏性**的（会覆盖现场可能已经改过的口令），
    /// 只能由人显式开启，绝不能顺手执行。
    ///
    /// 用法（演示库重建口令）：
    /// <code>
    /// $env:Xjgl__DemoData__ResetPassword='true'
    /// $env:Xjgl__DemoData__Password='Demo@Xjgl2026'
    /// dotnet src\Xjgl.DbMigrator\bin\Debug\net10.0\Xjgl.DbMigrator.dll
    /// </code>
    /// </summary>
    private const string ResetPasswordConfigKey = "Xjgl:DemoData:ResetPassword";

    /// <summary>身份证摘要的 HMAC 密钥配置键，与 IdCardProtector 保持一致。</summary>
    /// <summary>演示数据的识别标记，写入 StuStudent.SourceSystem。</summary>
    private const string SourceMarker = "XjglDemoSeed";

    /// <summary>
    /// 组织 / 年级 / 班级共用的备注标记。
    ///
    /// 这几张表没有 SourceSystem 那样的专用标记列，但都必须能被识别出来：
    /// 一是让人一眼看出不是真实数据，二是演示库要清理时能按一条谓词删干净，
    /// 不必靠"编码以 99 开头"这种会误伤真实区划的约定。
    /// </summary>
    private const string DemoMark = "演示数据（合成占位），非真实数据";

    /// <summary>预置记录的批次号标记（SysUserPredefine 支持按批次撤销）。</summary>
    private const string DemoBatchNo = "DEMO-SEED";

    /// <summary>演示用户统一使用的非超管角色。刻意不授 admin —— 否则数据范围被超管旁路，就验证不了范围。</summary>
    private const string DemoRoleName = "DemoOperator";

    private const string ProvinceCode = "990000";
    private const int StudentsPerSchool = 5;

    private static readonly string[] DemoRolePermissions =
    [
        MetadataPermissions.Stu.Student.Query,
        MetadataPermissions.Stu.Student.Export,
        MetadataPermissions.Stu.Student.Print,
        MetadataPermissions.Pho.Photo.Query,
        MetadataPermissions.Pho.Photo.Upload,
        MetadataPermissions.Pho.Photo.Export,

        // 预置用户的**写**权限是演示流程的一部分，不是顺手多给的：
        //   「超管建省级用户 → 省级用户建同级与市级 → 市级建县级 …」这条逐级流程
        //   需要发起者能创建预置记录。只给 Query 时演示会卡在 403，
        //   而 403 是权限层给的，会让人误以为"数据范围没配对"。
        //   Update/Delete 一起给是为了让激活码续期、撤销、删除也能在演示里走完。
        //   仍**刻意不给** Sys.UserProvision.Import：批量导入是另一条 UX 路径，
        //   演示用不上，少给一个写入口就少一分风险。
        MetadataPermissions.Sys.UserProvision.Query,
        MetadataPermissions.Sys.UserProvision.Create,
        MetadataPermissions.Sys.UserProvision.Update,
        MetadataPermissions.Sys.UserProvision.Delete,

        // 统计报表：演示的重点之一就是"不同层级看到不同口径的汇总"
        // （省厅看全省、县区看本县），所以必须给，否则统计页面在演示环境里是 403。
        MetadataPermissions.Rpt.Student.Query,
        MetadataPermissions.Rpt.Change.Query,

        MetadataPermissions.Sys.Org.Query,

        // 角色管理：业务侧权限（决定菜单能否看见）。
        MetadataPermissions.Sys.Role.Query,
        MetadataPermissions.Sys.Role.Create,
        MetadataPermissions.Sys.Role.Update,
        MetadataPermissions.Sys.Role.Delete,

        // ★ ABP 内置的身份权限（决定接口能否调用）。
        //
        // 【为什么演示角色必须拿到框架级权限 —— 实测结论，不是顺手多给】
        //   本系统的「用户管理 / 权限管理 / 角色管理」三个页面，操作的是 ABP 框架表
        //   （`AbpUsers` / `AbpRoles` / `AbpPermissionGrants`），因此调用的是 ABP
        //   **自带**的 `/api/identity/*`、`/api/permission-management/*` 控制器。
        //   这些控制器上的 `[Authorize]` 判的是 `AbpIdentity.*`，**不是**我们的业务权限。
        //
        //   没有它们时的现象非常误导人：请求**不是 403，而是 302 跳登录页**；
        //   浏览器 XHR 跟随重定向后拿到登录页 HTML，axios 解 JSON 抛错，
        //   而页面里写的是 `catch { roles.value = [] }` —— 结果是**下拉框空白、
        //   没有任何提示**，看起来就像"这个功能根本不存在"。
        //   （实测：演示账号打 `/api/identity/roles` 与 `/api/identity/roles/all`
        //     都是 302，admin 是 200。）
        //
        // 【为什么给到 Create/Update/Delete，以及刻意不给什么】
        //   不给的话，"建角色 → 给角色配权限 → 把角色分给用户"这条演示主流程走不完，
        //   而这条流程恰恰是「用户管理」与「权限管理」两个页面存在的前提。
        //   刻意**不给** `AbpIdentity.Roles.ManagePermissions` /
        //   `AbpIdentity.Users.ManagePermissions`：那是 ABP 权限管理界面自己的入口，
        //   本系统用的是自己的「权限管理」页（走 `/api/permission-management/permissions`
        //   + 业务权限 `Sys.Permission.Assign`），多给一个入口只增加风险。
        //   同理不给 `AbpTenantManagement.*` 与任何 `FeatureManagement.*`。
        "AbpIdentity.Roles",
        "AbpIdentity.Roles.Create",
        "AbpIdentity.Roles.Update",
        "AbpIdentity.Roles.Delete",

        // 用户管理页要读用户列表、并在编辑弹窗里给用户选角色。
        // `Update.ManageRoles` 是 `PUT /api/identity/users/{id}` 能改角色的前提 ——
        // 少了它，保存时角色改动会被服务端**静默忽略**（保存成功但角色没变）。
        "AbpIdentity.Users",
        "AbpIdentity.Users.Update",
        "AbpIdentity.Users.Update.ManageRoles"
    ];

    /// <summary>
    /// 年级定义。码值与微代码 <c>GRADE</c> 对齐（01=一年级、02=二年级），
    /// 演示校按小学设定，因此只取这两个。
    /// </summary>
    private static readonly (string Code, string Name, int Sort)[] GradeSeeds =
    [
        ("01", "一年级", 10),
        ("02", "二年级", 20)
    ];

    /// <summary>
    /// 合成姓名池。
    ///
    /// 其中第 4、5 个是**刻意放进去的生僻字**，用于验证生僻字链路：
    ///   - <c>䶮</c>（U+4DAE）在 CJK 扩展 A 区，是 BMP 内的单 char；
    ///   - <c>𠮷</c>（U+20BB7）在扩展 B 区，UTF-16 里是**代理对**（占 2 个 char）。
    /// 后者才是真正会出问题的那种 —— 任何按 string.Length / Substring 处理姓名的代码
    /// 都会在它身上出错，所以演示数据里必须有它，否则这条链路等于没验证。
    /// </summary>
    private static readonly string[] DemoStudentNames =
    [
        "赵安平", "钱雨欣", "孙明哲", "李䶮", "周𠮷",
        "吴思远", "郑晓宁", "王嘉禾", "冯若彤", "陈亦航"
    ];

    private readonly IConfiguration _configuration;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IXjglRepository<SysGrade> _grades;
    private readonly IXjglRepository<SysClass> _classes;
    private readonly IXjglRepository<StuStudent> _students;
    private readonly ISequenceService _sequenceService;
    private readonly ISettingProvider _settingProvider;
    private readonly IStringEncryptionService _encryption;
    private readonly IdentityUserManager _userManager;
    private readonly IdentityRoleManager _roleManager;
    private readonly IPermissionDataSeeder _permissionDataSeeder;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IClock _clock;
    private readonly ILogger<DemoDataSeedContributor> _logger;

    public DemoDataSeedContributor(
        IConfiguration configuration,
        IXjglRepository<SysOrg> orgs,
        IXjglRepository<SysUserPredefine> predefines,
        IXjglRepository<SysGrade> grades,
        IXjglRepository<SysClass> classes,
        IXjglRepository<StuStudent> students,
        ISequenceService sequenceService,
        ISettingProvider settingProvider,
        IStringEncryptionService encryption,
        IdentityUserManager userManager,
        IdentityRoleManager roleManager,
        IPermissionDataSeeder permissionDataSeeder,
        IGuidGenerator guidGenerator,
        IClock clock,
        ILogger<DemoDataSeedContributor> logger)
    {
        _configuration = configuration;
        _orgs = orgs;
        _predefines = predefines;
        _grades = grades;
        _classes = classes;
        _students = students;
        _sequenceService = sequenceService;
        _settingProvider = settingProvider;
        _encryption = encryption;
        _userManager = userManager;
        _roleManager = roleManager;
        _permissionDataSeeder = permissionDataSeeder;
        _guidGenerator = guidGenerator;
        _clock = clock;
        _logger = logger;
    }

    public async Task SeedAsync(DataSeedContext context)
    {
        if (!_configuration.GetValue(EnabledConfigKey, false))
        {
            _logger.LogInformation(
                "[演示数据] 未启用（{Key}=false），已跳过。",
                EnabledConfigKey);
            return;
        }

        var password = _configuration[PasswordConfigKey];
        var createAccounts = !string.IsNullOrWhiteSpace(password);

        if (!createAccounts)
        {
            _logger.LogWarning(
                "[演示数据] 已启用但未配置 {Key}：本次只建组织、预置名单与业务数据，" +
                "**不创建任何可登录账号**（预置记录保持「待激活」）。" +
                "口令请用环境变量 Xjgl__DemoData__Password 注入，不要写进仓库。",
                PasswordConfigKey);
        }

        // 重置口令必须显式开启：它是破坏性操作（会覆盖现场可能已改过的口令）
        var resetPassword = createAccounts
            && string.Equals(_configuration[ResetPasswordConfigKey], "true", StringComparison.OrdinalIgnoreCase);

        if (resetPassword)
        {
            _logger.LogWarning(
                "[演示数据] {Key}=true：**将重置已存在演示账号的口令**为当前配置值（这是破坏性操作）。",
                ResetPasswordConfigKey);
        }

        var academicYear = ResolveCurrentAcademicYear();

        _logger.LogInformation("[演示数据] 开始生成（学年 {Year}，建账号={Accounts}）…", academicYear, createAccounts);

        await EnsureRoleAsync();

        var tree = await EnsureOrgTreeAsync();
        var usersByOrg = await EnsureUsersAndPredefinesAsync(createAccounts, password, resetPassword);

        foreach (var school in tree.Schools)
        {
            var (grades, classes) = await EnsureGradesAndClassesAsync(school, academicYear, usersByOrg);

            await EnsureStudentsAsync(school, academicYear, grades, classes, tree.ProvinceId);
        }

        await LogSummaryAsync(academicYear, createAccounts, usersByOrg.Count);
    }

    // ------------------------------------------------------------------
    // 角色与授权
    // ------------------------------------------------------------------

    /// <summary>
    /// 建立演示角色并授予必要权限。
    ///
    /// 授权**复用 admin 全量授权所用的同一套机制**（<see cref="IPermissionDataSeeder"/>
    /// + <c>RolePermissionValueProvider</c>，见 ModuleMetadataDataSeedContributor），
    /// 不另发明一套写 AbpPermissionGrants 的方式 —— 否则权限缓存与后续的
    /// 「权限管理」界面都会认不出这些授权。
    ///
    /// 权限名取自 <see cref="MetadataPermissions"/> 的编译期常量而不是查库：
    /// 常量与元数据种子由仓库约定强制同步（见该类的注释），而查库会在
    /// 「元数据种子还没跑」时静默拿到空集合 —— 结果是一个没有任何权限的角色，
    /// 演示账号登录后处处 403，且日志里看不出原因。
    /// </summary>
    private async Task EnsureRoleAsync()
    {
        if (await _roleManager.FindByNameAsync(DemoRoleName) == null)
        {
            var createResult = await _roleManager.CreateAsync(
                new IdentityRole(_guidGenerator.Create(), DemoRoleName));

            if (!createResult.Succeeded)
            {
                throw new Volo.Abp.AbpException(
                    $"创建演示角色 {DemoRoleName} 失败：{Describe(createResult)}");
            }
        }

        await _permissionDataSeeder.SeedAsync(
            RolePermissionValueProvider.ProviderName,
            DemoRoleName,
            DemoRolePermissions);

        _logger.LogInformation(
            "[演示数据] 角色 {Role} 已确保授权 {Count} 个权限。",
            DemoRoleName,
            DemoRolePermissions.Length);
    }

    // ------------------------------------------------------------------
    // 组织树
    // ------------------------------------------------------------------

    private async Task<DemoTree> EnsureOrgTreeAsync()
    {
        var province = await EnsureOrgAsync(
            OrgTypes.Province, ProvinceCode, "示例省教育厅", parent: null, sort: 10);

        var schools = new List<DemoSchool>();

        var cityIndex = 0;
        foreach (var citySeed in CitySeeds)
        {
            cityIndex++;

            var city = await EnsureOrgAsync(
                OrgTypes.City, citySeed.Code, citySeed.Name, province, sort: cityIndex * 10);

            var countyIndex = 0;
            foreach (var countySeed in citySeed.Counties)
            {
                countyIndex++;

                var county = await EnsureOrgAsync(
                    OrgTypes.County, countySeed.Code, countySeed.Name, city, sort: countyIndex * 10);

                for (var schoolNo = 1; schoolNo <= 2; schoolNo++)
                {
                    // 学校标识码 10 位，前 6 位即所属行政区划码 —— 这不是巧合，
                    // 而是 StudentNumberRule 能靠"学校标识码前缀唯一"推出全局唯一学籍号的前提。
                    var schoolCode = $"{countySeed.Code}{schoolNo:D4}";
                    var schoolName = $"{countySeed.ShortName}第{schoolNo}中学";

                    var school = await EnsureOrgAsync(
                        OrgTypes.School, schoolCode, schoolName, county, sort: schoolNo * 10);

                    schools.Add(new DemoSchool(school, countySeed.ShortName));
                }
            }
        }

        return new DemoTree(province.Id, schools);
    }

    /// <summary>
    /// 按 Code 取组织，不存在才建。
    ///
    /// <c>SetPathFrom</c> 必须由父节点驱动（而不是自己拼 <c>父路径 + "." + 码</c>）：
    /// 物化路径是数据范围过滤的唯一依据，拼错一个分隔符就会让"本级及下级"
    /// 静默漏掉整棵子树 —— 而列表页只会显示"数据少了一些"，不会报错。
    /// </summary>
    private async Task<SysOrg> EnsureOrgAsync(
        string orgType,
        string code,
        string name,
        SysOrg? parent,
        int sort)
    {
        var existing = await _orgs.FindAsync(o => o.Code == code);

        if (existing != null)
        {
            return existing;
        }

        var org = new SysOrg(_guidGenerator.Create(), orgType, code, name, parent?.Id, sort: sort);
        org.SetPathFrom(parent);
        org.Remark = DemoMark;

        return await _orgs.InsertAsync(org, autoSave: true);
    }

    // ------------------------------------------------------------------
    // 用户与预置记录
    // ------------------------------------------------------------------

    private async Task<Dictionary<Guid, IdentityUser>> EnsureUsersAndPredefinesAsync(
        bool createAccounts,
        string? password,
        bool resetPassword = false)
    {
        var usersByOrg = new Dictionary<Guid, IdentityUser>();

        // 组织树的每一级都要有用户：省 1、每市 1、每县 1、每校 1。
        // 这样"逐级建用户"的边界（市级不能给别的市建、县级不能跨县、校级不能跨校）
        // 才有真实的账号可用来验证。
        //
        // 按演示标记取组织，而不是按"编码以 99 开头"—— 后者会误伤真实的 99 号段区划，
        // 而且在真实库里多一条 99 开头的组织就会让演示种子去给它建账号。
        var demoOrgs = (await _orgs.GetListAsync(o => o.Remark == DemoMark))
            .OrderBy(o => o.TreeLevel)
            .ThenBy(o => o.Code, StringComparer.Ordinal)
            .ToList();

        var index = 0;
        foreach (var org in demoOrgs)
        {
            index++;

            var userName = BuildUserName(org);
            var phone = BuildDemoPhone(index);
            var realName = $"{OrgTypes.GetDisplayName(org.OrgType)}演示员{index:D2}";

            IdentityUser? user = null;

            if (createAccounts)
            {
                user = await EnsureUserAsync(userName, realName, phone, password!, resetPassword);

                if (user != null)
                {
                    usersByOrg[org.Id] = user;
                }
            }

            await EnsurePredefineAsync(org, realName, phone, user);
        }

        _logger.LogInformation(
            "[演示数据] 组织 {Orgs} 个，账号 {Users} 个，预置记录 {Predefines} 条。",
            demoOrgs.Count,
            usersByOrg.Count,
            // 按批次号统计而不是 PhoneNumber.StartsWith("199")：
            // StartsWith 在 SqlSugar 上会翻译成 LIKE，能跑但把"演示数据识别"
            // 绑在了手机号段上；批次号是这几个字段里语义最明确的一个。
            await _predefines.GetCountAsync(p => p.BatchNo == DemoBatchNo));

        return usersByOrg;
    }

    private async Task<IdentityUser?> EnsureUserAsync(
        string userName,
        string realName,
        string phone,
        string password,
        bool resetPassword = false)
    {
        var existing = await _userManager.FindByNameAsync(userName);

        if (existing != null)
        {
            // ★ 幂等：已存在的用户不重建。
            //
            // 但这里有一个**必须知道的副作用**：改了 `Xjgl:DemoData:Password` 之后，
            // 旧库里已存在账号的口令**不会跟着变** —— 表现为"种子日志一切正常、
            // 但文档里的演示口令登不上"。所以要重设口令必须显式开 ResetPassword
            // （见 ResetPasswordConfigKey 的说明）。
            if (resetPassword)
            {
                await ResetPasswordAsync(existing, password);
            }

            return existing;
        }

        var user = new IdentityUser(_guidGenerator.Create(), userName, $"{userName}@xjgl.local")
        {
            Name = realName
        };

        var createResult = await _userManager.CreateAsync(user, password);

        if (!createResult.Succeeded)
        {
            // 口令策略（长度/大小写/数字/符号）由 ABP Identity 的 PasswordOptions 决定。
            // 这里必须把 Identity 的错误原文抛出来：否则表现只是"演示账号建不出来"，
            // 而真正的原因（口令不符合策略）全在 createResult.Errors 里，不看就猜不到。
            throw new Volo.Abp.AbpException(
                $"创建演示账号 {userName} 失败：{Describe(createResult)}。" +
                "若提示口令不合规，请调整 Xjgl:DemoData:Password 使其满足 Identity 的密码策略。");
        }

        await _userManager.SetPhoneNumberAsync(user, phone);

        var roleResult = await _userManager.AddToRoleAsync(user, DemoRoleName);
        if (!roleResult.Succeeded)
        {
            throw new Volo.Abp.AbpException(
                $"为演示账号 {userName} 授予角色 {DemoRoleName} 失败：{Describe(roleResult)}");
        }

        return user;
    }

    /// <summary>
    /// 重置已存在演示账号的口令（仅在 <c>Xjgl:DemoData:ResetPassword=true</c> 时调用）。
    ///
    /// 【为什么要分两步：RemovePasswordAsync + AddPasswordAsync】
    /// 直接用 <c>AddPasswordAsync</c> 会**再挂一个**口令哈希（Identity 允许一个用户多套凭据），
    /// 于是旧口令**依然能登录** —— 那是比"登不上"更糟的结果：
    /// 你以为已经换掉了，实际被重置的口令和旧口令同时有效。
    /// 必须先把旧哈希摘掉。
    /// </summary>
    private async Task ResetPasswordAsync(IdentityUser user, string password)
    {
        var removed = await _userManager.RemovePasswordAsync(user);

        if (!removed.Succeeded && !removed.Errors.Any(e => e.Code == "PasswordNotFound"))
        {
            throw new Volo.Abp.AbpException(
                $"重置演示账号 {user.UserName} 的口令失败（清除旧口令阶段）：{Describe(removed)}");
        }

        var added = await _userManager.AddPasswordAsync(user, password);

        if (!added.Succeeded)
        {
            throw new Volo.Abp.AbpException(
                $"重置演示账号 {user.UserName} 的口令失败：{Describe(added)}。" +
                "若提示口令不合规，请调整 Xjgl:DemoData:Password 使其满足 Identity 的密码策略。");
        }

        _logger.LogInformation("[演示数据] 已重置账号 {UserName} 的口令。", user.UserName);
    }

    /// <summary>
    /// 建立（或修复）预置记录。
    ///
    /// ★ <c>ActivatedUserId</c> 与 <c>OrgId</c> 必须同时正确 —— 这是数据范围解析的
    ///   **唯一来源**（见 UserDataScopeProvider：用户 → 预置记录 → 组织）。
    ///   少了它，演示账号登录后 ResolveAsync 会返回 UserDataScope.Empty，
    ///   表现是"学生列表永远是空的"，而接口返回 200、没有任何报错。
    /// </summary>
    private async Task EnsurePredefineAsync(
        SysOrg org,
        string realName,
        string phone,
        IdentityUser? user)
    {
        var predefine = await _predefines.FindAsync(
            p => p.PhoneNumber == phone && p.Status != UserPredefineStatus.Revoked);

        if (predefine == null)
        {
            predefine = new SysUserPredefine(
                _guidGenerator.Create(),
                realName,
                phone,
                org.Id,
                // 演示账号是"已激活"状态，这个激活码永远不会被使用；
                // 仍然随机生成而不是写死，避免它看起来像一个可供使用的凭据。
                Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(),
                _clock.Now.AddDays(30));

            predefine.OrgPathCode = org.PathCode;
            predefine.RoleNames = DemoRoleName;
            predefine.Position = "演示账号（合成占位）";
            predefine.BatchNo = DemoBatchNo;
            predefine.Remark = DemoMark;

            if (user != null)
            {
                predefine.MarkActivated(user.Id, _clock.Now);
            }

            // ⚠ 这里必须靠 `predefine == null` 分流，**不能**用 `predefine.Id == Guid.Empty` 判断新旧。
            //   本项目的实体都要求显式传入 Id（`_guidGenerator.Create()`），所以"新建"的实体
            //   Id 也永远不为空；用 Id 判断会让新记录走进下面的 Update 分支，
            //   而 Update 对一行并不存在的记录是**静默无效**的 ——
            //   实测症状正是：15 个演示账号都建好了，预置记录 0 条，日志里一句报错都没有。
            await _predefines.InsertAsync(predefine, autoSave: true);
            return;
        }

        // 已存在：只在"还没关联到账号"时修复关联关系。
        // 其余字段一律不动 —— 演示种子不该覆盖人工在界面上的修改。
        if (user != null && predefine.ActivatedUserId != user.Id)
        {
            predefine.MarkActivated(user.Id, _clock.Now);
            await _predefines.UpdateAsync(predefine, autoSave: true);
        }
    }

    // ------------------------------------------------------------------
    // 年级 / 班级
    // ------------------------------------------------------------------

    private async Task<(List<SysGrade> Grades, List<SysClass> Classes)> EnsureGradesAndClassesAsync(
        DemoSchool school,
        string academicYear,
        Dictionary<Guid, IdentityUser> schoolUsers)
    {
        schoolUsers.TryGetValue(school.Org.Id, out var headTeacher);
        var headTeacherId = headTeacher?.Id;

        var grades = new List<SysGrade>();
        var classes = new List<SysClass>();

        foreach (var gradeSeed in GradeSeeds)
        {
            var grade = await _grades.FindAsync(g =>
                g.SchoolOrgId == school.Org.Id &&
                g.AcademicYear == academicYear &&
                g.GradeCode == gradeSeed.Code);

            if (grade == null)
            {
                grade = new SysGrade(
                    _guidGenerator.Create(),
                    school.Org.Id,
                    academicYear,
                    gradeSeed.Code,
                    gradeSeed.Name,
                    gradeSeed.Sort)
                {
                    HeadTeacherUserId = headTeacherId,
                    Remark = DemoMark
                };

                grade = await _grades.InsertAsync(grade, autoSave: true);
            }

            grades.Add(grade);

            for (var classNo = 1; classNo <= 2; classNo++)
            {
                var classNoText = classNo.ToString("D2");

                var entity = await _classes.FindAsync(c =>
                    c.SchoolOrgId == school.Org.Id &&
                    c.AcademicYear == academicYear &&
                    c.ClassNo == classNoText &&
                    c.GradeId == grade.Id);

                if (entity == null)
                {
                    entity = new SysClass(
                        _guidGenerator.Create(),
                        school.Org.Id,
                        academicYear,
                        grade.Id,
                        classNoText,
                        $"{gradeSeed.Name}{classNo}班",
                        gradeSeed.Sort + classNo)
                    {
                        HeadTeacherUserId = headTeacherId,
                        Remark = DemoMark
                    };

                    entity = await _classes.InsertAsync(entity, autoSave: true);
                }

                classes.Add(entity);
            }
        }

        return (grades, classes);
    }

    // ------------------------------------------------------------------
    // 学生
    // ------------------------------------------------------------------

    private async Task EnsureStudentsAsync(
        DemoSchool school,
        string academicYear,
        List<SysGrade> grades,
        List<SysClass> classes,
        Guid provinceId)
    {
        var province = await _orgs.FindAsync(provinceId);
        var schoolCode = school.Org.Code;
        var yearStart = int.Parse(academicYear[..4]);

        for (var i = 0; i < StudentsPerSchool; i++)
        {
            // 年级：0→一年级（3 人），1→二年级（2 人）
            var gradeIndex = i < 3 ? 0 : 1;

            if (gradeIndex >= grades.Count)
            {
                continue;
            }

            var grade = grades[gradeIndex];

            var classIndexWithinGrade = i % 2;
            var classIndex = gradeIndex * 2 + classIndexWithinGrade;

            if (classIndex >= classes.Count)
            {
                continue;
            }

            var entity = classes[classIndex];

            var isMale = i % 2 == 0;
            var birthDate = new DateTime(
                yearStart - (6 + gradeIndex),
                (i % 12) + 1,
                1 + (i * 7) % 27);

            var globalIndex = school.Ordinal * StudentsPerSchool + i;

            var idCard = BuildValidIdCard(
                school.CountyRegionCode,
                birthDate,
                globalIndex,
                isMale);

            var idCardHash = ComputeIdCardHash(idCard);

            // 幂等键用身份证摘要，而不是学籍辅号：
            // 学籍辅号来自流水号，重跑时如果先取号再判断，号段就会被白白消耗，
            // 每次重跑都让号段往前跳 —— 演示库看不出来，但这是真实的副作用。
            var exists = await _students.FindAsync(s => s.IdCardHash == idCardHash);

            if (exists != null)
            {
                continue;
            }

            var name = DemoStudentNames[(school.Ordinal + i) % DemoStudentNames.Length];

            if (!RareCharacterText.IsValidPersonName(name, out var nameError))
            {
                throw new Volo.Abp.AbpException(
                    $"演示学生姓名「{name}」不是合法姓名：{nameError}");
            }

            var student = new StuStudent(
                _guidGenerator.Create(),
                await GenerateStudentNoAsync(schoolCode),
                name,
                isMale ? "1" : "2",
                birthDate,
                school.Org.Id,
                academicYear)
            {
                ClassStudentNo = (i + 1).ToString("D2"),
                // 冗余学校物化路径：列表接口按它做数据范围前缀过滤（见 AllowsEntityPath）
                SchoolPathCode = school.Org.PathCode,
                GradeId = grade.Id,
                ClassId = entity.Id,
                IdCardEncrypted = _encryption.Encrypt(idCard),
                IdCardHash = idCardHash,
                EnrollDate = new DateTime(yearStart - gradeIndex, 9, 1),
                EnrollTypeCode = "2",
                StudyModeCode = "1",
                SchoolLengthCode = "6",
                // NATION / COUNTRY 两个码表在种子里是**空的**（见 CodeDataSeedContributor），
                // 因此这里刻意不填民族码与国籍码：填一个不存在的码值，
                // 界面会显示空白，看起来像数据坏了，实际是没有码表。
                PoliticalCode = "13",
                HouseholdNatureCode = (i % 3 + 1).ToString(),
                HealthCode = "1",
                NativePlace = province?.Name,
                BirthPlace = province?.Name,
                HomeAddress = $"{school.CountyShortName}示例路{10 + i}号",
                CurrentAddress = $"{school.CountyShortName}示例路{10 + i}号",
                PostalCode = "990000",
                IsLeftBehindChild = i == 3,
                IsPoor = i == 4,
                SourceSystem = SourceMarker,
                Remark = "演示数据（合成占位），非真实学籍"
            };

            await _students.InsertAsync(student, autoSave: true);
        }
    }

    /// <summary>
    /// 生成学籍辅号。
    ///
    /// ★ 号段名与取出方式必须与业务侧**逐字一致**（见 StudentAppService.GenerateStudentNoAsync）：
    ///   同一个 <c>STU_NO:{学校标识码}</c> 号段。若这里另起一个号段名，
    ///   业务侧从 1 开始发号，就会发出与演示学生**重复的学籍辅号** ——
    ///   而学籍辅号是业务主键，撞号会造成两个学生共用一个号，
    ///   且症状要等到导入或打印时才会浮现。
    ///
    /// 规则模板同样来自系统参数，演示数据因此会跟随现场配置，
    /// 不会在换了号段规则之后变成一批"形状不合法"的历史数据。
    /// </summary>
    private async Task<string> GenerateStudentNoAsync(string schoolCode)
    {
        var template = await _settingProvider.GetOrNullAsync(XjglSettings.StudentNumberRule);

        if (!StudentNumberRule.TryParse(template, out var rule, out _) || rule == null)
        {
            rule = StudentNumberRule.Default;
        }

        var sequence = _sequenceService.NextValue($"STU_NO:{schoolCode}", length: 6);

        return rule.Format(schoolCode, sequence);
    }

    /// <summary>
    /// 造一个**校验位合法**的 18 位身份证号。
    ///
    /// 思路：先拼 17 位，再让 <see cref="IdCardValidator"/> 自己挑出第 18 位 ——
    /// 而不是在种子代码里重算一遍加权和。校验位算法（GB 11643）是被复用对象，
    /// 一旦标准或实现变化，这里自动跟随；自己抄一遍则必然在某次改动后失配，
    /// 而失配的表现是"学生保存时报校验位不正确"，排查成本远高于多写这五行循环。
    ///
    /// 17 位的构成：区划码(6) + 出生日期(8) + 顺序码(3)。
    /// 顺序码的**末位奇偶决定性别**（奇数男、偶数女），因此这里按性别取号，
    /// 保证身份证号与 GenderCode 自洽 —— 否则业务侧用 TryGetGender 校验时会矛盾。
    /// </summary>
    private static string BuildValidIdCard(
        string regionCode,
        DateTime birthDate,
        int globalIndex,
        bool isMale)
    {
        var sequence = (isMale ? 101 : 100) + 2 * globalIndex;

        var id17 = $"{regionCode}{birthDate:yyyyMMdd}{sequence:D3}";

        foreach (var candidate in "0123456789X")
        {
            var idCard = id17 + candidate;

            if (IdCardValidator.TryNormalize(idCard, out var normalized, out _)
                && string.Equals(normalized, idCard, StringComparison.Ordinal))
            {
                return idCard;
            }
        }

        throw new Volo.Abp.AbpException(
            $"无法为区划 {regionCode} 生成合法身份证号（{id17}?），请检查区划码与出生日期。");
    }

    /// <summary>
    /// 与业务侧一致的身份证摘要。算法与密钥取值都复用 Domain.Shared 里的
    /// <see cref="IdCardValidator"/>，本文件不再自己持有任何密钥字面量 ——
    /// 之前那份重复的常量已下沉，避免"改一处忘一处导致摘要不一致"。
    /// </summary>
    private string ComputeIdCardHash(string normalizedIdCard)
    {
        var key = _configuration[IdCardValidator.HmacKeyConfigKey];

        if (key.IsNullOrWhiteSpace())
        {
            key = IdCardValidator.DevFallbackHmacKey;
        }

        return IdCardValidator.ComputeIdCardHash(normalizedIdCard, Encoding.UTF8.GetBytes(key!));
    }

    // ------------------------------------------------------------------
    // 命名与汇总
    // ------------------------------------------------------------------

    private static string BuildUserName(SysOrg org)
    {
        var prefix = org.OrgType switch
        {
            OrgTypes.Province => "prov",
            OrgTypes.City => "city",
            OrgTypes.County => "county",
            _ => "school"
        };

        // 带上组织编码，一眼能看出归属：demo-school-9901010001 属于学校 9901010001
        return $"demo-{prefix}-{org.Code}";
    }

    /// <summary>
    /// 演示手机号。用固定前缀 + 序号，明显是占位号段；
    /// 11 位纯数字是 <c>SysUserPredefine.PhoneNumber</c> 与业务校验的要求。
    /// </summary>
    private static string BuildDemoPhone(int index) => $"1990000{index:D4}";

    /// <summary>
    /// 当前学年。9 月之后算新学年，否则还属于上一学年 ——
    /// 学年维度决定年级班级与学生的归属，算错会让演示数据落在"上一个学年"，
    /// 界面按当前学年筛选时一个都查不到。
    /// </summary>
    private string ResolveCurrentAcademicYear()
    {
        var now = _clock.Now;

        return now.Month >= 9
            ? $"{now.Year}-{now.Year + 1}"
            : $"{now.Year - 1}-{now.Year}";
    }

    /// <summary>
    /// 打印一份可核对的汇总。
    ///
    /// 计数一律按**演示标记**（备注 / 批次号 / 来源系统）查库，而不是用本次运行的内存计数器：
    /// 这样"库里到底有多少演示数据"是查出来的事实，重跑时若数量翻倍会立刻在日志里看出来。
    /// 内存计数则无论幂等是否失效都会显示同样的数字，等于没验证。
    /// </summary>
    private async Task LogSummaryAsync(string academicYear, bool createAccounts, int userCount)
    {
        _logger.LogInformation(
            "[演示数据] 完成。学年={Year} 组织={Orgs} 账号={Users} 预置={Predefines} " +
            "年级={Grades} 班级={Classes} 学生={Students}（建账号={Accounts}）",
            academicYear,
            await _orgs.GetCountAsync(o => o.Remark == DemoMark),
            userCount,
            await _predefines.GetCountAsync(p => p.BatchNo == DemoBatchNo),
            await _grades.GetCountAsync(g => g.Remark == DemoMark),
            await _classes.GetCountAsync(c => c.Remark == DemoMark),
            await _students.GetCountAsync(s => s.SourceSystem == SourceMarker),
            createAccounts);
    }

    private static string Describe(IdentityResult result)
        => string.Join("；", result.Errors.Select(e => $"{e.Code}: {e.Description}"));

    // ------------------------------------------------------------------
    // 演示数据定义
    // ------------------------------------------------------------------

    /// <summary>
    /// 市 / 县定义。区划码全部落在 <c>99xxxx</c>（真实行政区划码之外的保留号段），
    /// 学校标识码取其前 6 位 + 4 位序号，与真实学校标识码不可能相撞。
    /// </summary>
    private static readonly CitySeed[] CitySeeds =
    [
        new("990100", "示例市甲教育局",
        [
            new("990101", "示例甲县教育局", "示例甲县"),
            new("990102", "示例乙县教育局", "示例乙县")
        ]),
        new("990200", "示例市乙教育局",
        [
            new("990201", "示例丙县教育局", "示例丙县"),
            new("990202", "示例丁县教育局", "示例丁县")
        ])
    ];

    private sealed record CitySeed(string Code, string Name, CountySeed[] Counties);

    private sealed record CountySeed(string Code, string Name, string ShortName);

    private sealed record DemoSchool(SysOrg Org, string CountyShortName)
    {
        /// <summary>学校在其所属县区内的序号（0/1），用于稳定地选取姓名与身份证顺序码。</summary>
        public int Ordinal => int.Parse(Org.Code[^1..]) - 1;

        /// <summary>所属县区区划码（学校标识码前 6 位）。</summary>
        public string CountyRegionCode => Org.Code[..6];
    }

    private sealed record DemoTree(Guid ProvinceId, List<DemoSchool> Schools);
}
