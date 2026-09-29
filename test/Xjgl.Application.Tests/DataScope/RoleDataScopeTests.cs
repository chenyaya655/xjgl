using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Guids;
using Volo.Abp.Identity;
using Volo.Abp.Timing;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Students;
using Xjgl.Users;
using Xunit;

namespace Xjgl.DataScope;

/// <summary>
/// 角色级数据范围配置（ROADMAP 第 9 条）的集成测试。
///
/// 【这组用例守的是什么】
/// 在此之前数据范围**只能**按组织层级推导：省厅=全省、市/县/校=本级及下级。
/// 于是"某市局只看本市、不看他下属的县"这类**例外**根本无法表达 ——
/// 而这是很常见的口径差异（汇总口径 ≠ 管辖口径）。
///
/// 【为什么每条用例都做"同一用户、同一份数据、只改配置"的对照】
/// 只断言"配了 OrgOnly 之后看不到下级"是不够的：**看不到**也可能是别的原因造成的
/// （数据没建出来、权限不对、组织挂错）。所以每条用例都先在**未配置**状态下
/// 断言"看得到"，再配置成 OrgOnly 断言"看不到" —— 两次之间只改了配置这一个变量。
/// 这样"看不到"才真的归因于数据范围。
///
/// 【为什么每次换身份都要新开 DI 作用域】
/// `ICurrentUser` 是 Scoped 且会缓存用户与角色（见 DataScopeTests 的类注释），
/// 换身份必须新开作用域，否则所有身份看起来都像第一个。
/// </summary>
public class RoleDataScopeTests : XjglIntegrationTestBase
{
    private const string Year = "2071-2072";

    private readonly IOrgAppService _orgs;
    private readonly IStudentAppService _students;
    private readonly IUserProvisionAppService _provisions;
    private readonly IRoleDataScopeAppService _roleScopes;
    private readonly IdentityRoleManager _roleManager;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IGuidGenerator _guid;
    private readonly IClock _clock;
    private readonly FakeCurrentPrincipalAccessor _principal;

    public RoleDataScopeTests()
    {
        _orgs = GetRequiredService<IOrgAppService>();
        _students = GetRequiredService<IStudentAppService>();
        _provisions = GetRequiredService<IUserProvisionAppService>();
        _roleScopes = GetRequiredService<IRoleDataScopeAppService>();
        _roleManager = GetRequiredService<IdentityRoleManager>();
        _predefines = GetRequiredService<IXjglRepository<SysUserPredefine>>();
        _guid = GetRequiredService<IGuidGenerator>();
        _clock = GetRequiredService<IClock>();
        _principal = GetRequiredService<FakeCurrentPrincipalAccessor>();

        // 准备数据一律以超管身份进行（超管旁路，不受数据范围影响）
        _principal.SetUser(Guid.NewGuid(), "admin", "admin");
    }

    // ------------------------------------------------------------------
    // 脚手架
    // ------------------------------------------------------------------

    /// <summary>以指定用户与角色执行一段查询：换 principal + 新开 DI 作用域。</summary>
    private async Task<T> AsUserAsync<T>(Guid userId, string roleName, Func<IServiceProvider, Task<T>> action)
    {
        _principal.SetUser(userId, "role_scope_tester", roleName);

        using var scope = ServiceProvider.CreateScope();

        return await action(scope.ServiceProvider);
    }

    /// <summary>建一个 ABP 角色，并给它配置数据范围（scope 为 null 表示不配置）。</summary>
    private async Task<string> CreateRoleAsync(string? scope)
    {
        var roleName = "RoleScope_" + Guid.NewGuid().ToString("N")[..8];

        var created = await _roleManager.CreateAsync(new IdentityRole(_guid.Create(), roleName));

        created.Succeeded.ShouldBeTrue(string.Join("；", created.Errors.Select(e => e.Description)));

        if (scope != null)
        {
            var role = await _roleManager.FindByNameAsync(roleName);

            await _roleScopes.SetAsync(new SetRoleDataScopeDto { RoleId = role!.Id, Scope = scope });
        }

        return roleName;
    }

    /// <summary>把用户关联到某个组织（数据范围的唯一来源是预置记录）。</summary>
    private async Task<Guid> LinkUserToOrgAsync(Guid orgId)
    {
        var userId = _guid.Create();

        var predefine = new SysUserPredefine(
            _guid.Create(),
            "角色范围测试用户",
            "138" + Random.Shared.Next(10000000, 99999999),
            orgId,
            "TESTCODE",
            _clock.Now.AddDays(30));

        predefine.MarkActivated(userId, _clock.Now);

        await _predefines.InsertAsync(predefine, autoSave: true);

        return userId;
    }

    private async Task<OrgDto> CreateOrgAsync(string orgType, string name, Guid? parentId)
    {
        return await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = orgType,
            // 编码用 GUID 片段保证唯一：随机行政区划码会随测试库累积而撞号（§22.4 记过这个坑）。
            // 只有**学校**必须是 10 位学校标识码（学籍辅号的 {SchoolCode:10} 段依赖它）。
            Code = orgType == OrgTypes.School
                ? "41" + Random.Shared.Next(1000000, 9999999).ToString("D7")
                : Guid.NewGuid().ToString("N")[..8],
            Name = name,
            ParentId = parentId
        });
    }

    private async Task<StudentDto> CreateStudentAsync(Guid schoolId, string name)
    {
        return await _students.CreateAsync(new CreateUpdateStudentDto
        {
            Name = name,
            GenderCode = "1",
            BirthDate = new DateTime(2014, 5, 1),
            IdCardTypeCode = "1",
            IdCardNumber = NewUniqueIdCard(),
            SchoolOrgId = schoolId,
            AcademicYear = Year,
            StudentStatusCode = StudentStatuses.Studying
        });
    }

    /// <summary>省 → 市 → 县（两所学校）/ 另一个县（一所学校）。</summary>
    private async Task<(
        OrgDto Province, OrgDto City, OrgDto County, OrgDto SchoolA1, OrgDto SchoolA2,
        OrgDto OtherCounty, OrgDto OtherSchool)> BuildTreeAsync()
    {
        var province = await CreateOrgAsync(OrgTypes.Province, "角色范围测试省厅", null);
        var city = await CreateOrgAsync(OrgTypes.City, "角色范围测试市局", province.Id);
        var county = await CreateOrgAsync(OrgTypes.County, "角色范围测试甲县", city.Id);

        var schoolA1 = await CreateOrgAsync(OrgTypes.School, "甲县一中", county.Id);
        var schoolA2 = await CreateOrgAsync(OrgTypes.School, "甲县二中", county.Id);

        var otherCounty = await CreateOrgAsync(OrgTypes.County, "角色范围测试乙县", city.Id);
        var otherSchool = await CreateOrgAsync(OrgTypes.School, "乙县一中", otherCounty.Id);

        return (province, city, county, schoolA1, schoolA2, otherCounty, otherSchool);
    }

    /// <summary>
    /// 数一数某个组织子树里当前身份能看到的学生。
    ///
    /// 必须带 <paramref name="orgId"/>：测试库是**长期累积**的（跑过若干轮之后里面
    /// 已经有几百个学生），不带条件的查询会把别人的数据也算进来，
    /// 断言就只能写成 `>=`，而那等于没断言。
    /// </summary>
    private static async Task<long> CountVisibleStudentsAsync(IServiceProvider provider, Guid orgId)
    {
        var students = provider.GetRequiredService<IStudentAppService>();

        var page = await students.GetListAsync(new StudentQueryDto
        {
            SchoolOrgId = orgId,
            IncludeDescendantOrgs = true,
            MaxResultCount = 100
        });

        return page.TotalCount;
    }

    // ==================================================================
    // 验收：能创建「只看本级」的角色，且下级数据不可见
    // ==================================================================

    [Fact]
    public async Task 角色配置为仅本级_看不到下级学校的学生()
    {
        var tree = await BuildTreeAsync();

        await CreateStudentAsync(tree.SchoolA1.Id, "甲县一中学生");
        await CreateStudentAsync(tree.SchoolA2.Id, "甲县二中学生");

        // 用户挂在**县区**上
        var userId = await LinkUserToOrgAsync(tree.County.Id);

        // ---- 第一段：不配置范围 → 按层级推导（本级及下级）→ 看得到两所学校的学生
        var unconfiguredRole = await CreateRoleAsync(scope: null);

        var visibleBefore = await AsUserAsync(
            userId, unconfiguredRole, sp => CountVisibleStudentsAsync(sp, tree.County.Id));

        visibleBefore.ShouldBe(2, "未配置时按组织层级推导：县区管理员能看本县两所学校");

        // ---- 第二段：同一个用户、同一份数据，只把角色的范围配成「仅本级」
        var orgOnlyRole = await CreateRoleAsync(DataScopes.OrgOnly);

        var visibleAfter = await AsUserAsync(
            userId, orgOnlyRole, sp => CountVisibleStudentsAsync(sp, tree.County.Id));

        // 学生挂在**学校**上，学校是县区的下级 → 「仅本级」下全都看不到。
        // 这正是"不看他下属的县/校"这条口径在该身份上的表现。
        visibleAfter.ShouldBe(0, "配置为「仅本级」后，下级学校的学生必须不可见");
    }

    /// <summary>
    /// 反过来也要成立：配成「全部数据」时必须看得到**全省**（不能只会收紧）。
    ///
    /// 少了这条，"配置只用来收紧"就成了唯一被测过的路径 ——
    /// 而一个只能收紧的配置表在真实使用中是没用的（它同样要能把口径放宽）。
    /// </summary>
    [Fact]
    public async Task 角色配置为全部数据_能看到全省学生()
    {
        var tree = await BuildTreeAsync();

        await CreateStudentAsync(tree.SchoolA1.Id, "甲县一中学生");
        await CreateStudentAsync(tree.OtherSchool.Id, "乙县一中学生");

        // 用户挂在**学校**上（默认只能看本校）
        var userId = await LinkUserToOrgAsync(tree.SchoolA1.Id);

        var defaultRole = await CreateRoleAsync(scope: null);
        var defaultVisible = await AsUserAsync(
            userId, defaultRole, sp => CountVisibleStudentsAsync(sp, tree.SchoolA1.Id));

        defaultVisible.ShouldBe(1, "未配置时校级账号只看得到本校学生");

        var allRole = await CreateRoleAsync(DataScopes.All);
        var allVisible = await AsUserAsync(
            userId, allRole, sp => CountVisibleStudentsAsync(sp, tree.Province.Id));

        allVisible.ShouldBe(2, "配置为「全部数据」后应当看得到全省（含别的县）的学生");
    }

    // ==================================================================
    // 这条正是 ROADMAP/README 里举的例子：某市局只看本市、不看他下属县
    // ==================================================================

    [Fact]
    public async Task 市局配置为仅本级_看得到本局预置名单_看不到下属县的()
    {
        // 预置名单可以挂在**任意层级**的组织上（学生只能挂学校），
        // 所以它是"某市局只看本市、不看他下属县"这个场景最贴切的载体。

        var tree = await BuildTreeAsync();

        // 市局自己的预置记录 + 下属县的一条
        await _provisions.CreateAsync(new CreatePredefineUserDto
        {
            RealName = "市局工作人员",
            PhoneNumber = "137" + Random.Shared.Next(10000000, 99999999),
            OrgId = tree.City.Id,
            RoleNames = "SchoolAdmin"
        });

        await _provisions.CreateAsync(new CreatePredefineUserDto
        {
            RealName = "下属县工作人员",
            PhoneNumber = "137" + Random.Shared.Next(10000000, 99999999),
            OrgId = tree.County.Id,
            RoleNames = "SchoolAdmin"
        });

        var userId = await LinkUserToOrgAsync(tree.City.Id);

        async Task<long> CountVisibleProvisionsAsync(IServiceProvider provider)
        {
            var provisions = provider.GetRequiredService<IUserProvisionAppService>();

            var page = await provisions.GetListAsync(new PredefineUserQueryDto
            {
                OrgId = tree.City.Id,
                IncludeDescendantOrgs = true,
                MaxResultCount = 100
            });

            return page.TotalCount;
        }

        // ---- 未配置：本级及下级 → 市局自己 + 下属县都能看到
        var defaultRole = await CreateRoleAsync(scope: null);
        var before = await AsUserAsync(userId, defaultRole, CountVisibleProvisionsAsync);

        before.ShouldBe(2, "未配置时市局能看到本局与下属县的预置名单");

        // ---- 配成「仅本级」：只剩市局自己那条
        var orgOnlyRole = await CreateRoleAsync(DataScopes.OrgOnly);
        var after = await AsUserAsync(userId, orgOnlyRole, CountVisibleProvisionsAsync);

        after.ShouldBe(1, "「仅本级」下应当看不到下属县的预置名单");
    }

    // ==================================================================
    // 配置表的读写
    // ==================================================================

    [Fact]
    public async Task 清除配置后_回到按组织层级推导()
    {
        var tree = await BuildTreeAsync();

        await CreateStudentAsync(tree.SchoolA1.Id, "甲县一中学生");

        var userId = await LinkUserToOrgAsync(tree.County.Id);

        var roleName = "RoleScope_" + Guid.NewGuid().ToString("N")[..8];
        var created = await _roleManager.CreateAsync(new IdentityRole(_guid.Create(), roleName));
        created.Succeeded.ShouldBeTrue();

        var role = await _roleManager.FindByNameAsync(roleName);

        await _roleScopes.SetAsync(new SetRoleDataScopeDto { RoleId = role!.Id, Scope = DataScopes.OrgOnly });

        var withOrgOnly = await AsUserAsync(
            userId, roleName, sp => CountVisibleStudentsAsync(sp, tree.County.Id));

        withOrgOnly.ShouldBe(0);

        // 传空 = 清除配置。这是必要的：否则一旦配过就再也回不到默认值，
        // 而默认值才是绝大多数角色该用的。
        var cleared = await _roleScopes.SetAsync(new SetRoleDataScopeDto { RoleId = role.Id, Scope = null });

        cleared.Scope.ShouldBeNull();
        cleared.IsConfigured.ShouldBeFalse();

        var afterClear = await AsUserAsync(
            userId, roleName, sp => CountVisibleStudentsAsync(sp, tree.County.Id));

        afterClear.ShouldBe(1, "清除配置后应当回到「本级及下级」的默认行为（本用例只建了 1 个学生）");
    }

    [Fact]
    public async Task 列表返回全部角色_未配置的Scope为空()
    {
        var roleName = "RoleScope_" + Guid.NewGuid().ToString("N")[..8];
        var created = await _roleManager.CreateAsync(new IdentityRole(_guid.Create(), roleName));
        created.Succeeded.ShouldBeTrue();

        var list = await _roleScopes.GetListAsync();

        var mine = list.Single(r => r.RoleName == roleName);

        mine.Scope.ShouldBeNull("新建角色默认没有配置，沿用按组织层级推导");
        mine.IsConfigured.ShouldBeFalse();
    }

    /// <summary>
    /// 「自定义组织集合」必须被**明确拒绝**，而不是悄悄按"本级及下级"生效。
    ///
    /// 各查询的 SQL 谓词按"单一根路径"生成（路径相等 OR 前缀匹配），落不了多个根。
    /// 若接受配置再退化成默认范围，那就**比配置更宽** ——
    /// 管理员以为收紧了，实际放开了，而且没有任何提示。
    /// </summary>
    [Fact]
    public async Task 自定义组织集合暂不支持_被明确拒绝()
    {
        var roleName = "RoleScope_" + Guid.NewGuid().ToString("N")[..8];
        var created = await _roleManager.CreateAsync(new IdentityRole(_guid.Create(), roleName));
        created.Succeeded.ShouldBeTrue();

        var role = await _roleManager.FindByNameAsync(roleName);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _roleScopes.SetAsync(new SetRoleDataScopeDto
            {
                RoleId = role!.Id,
                Scope = DataScopes.Custom
            }));

        // 报错里要说清"为什么不行、现在该用什么"，而不是只给一个错误码
        ex.Data["reason"]!.ToString()!.ShouldContain("自定义组织集合");
    }

    [Fact]
    public async Task 未知的范围值_被拒绝()
    {
        var roleName = "RoleScope_" + Guid.NewGuid().ToString("N")[..8];
        var created = await _roleManager.CreateAsync(new IdentityRole(_guid.Create(), roleName));
        created.Succeeded.ShouldBeTrue();

        var role = await _roleManager.FindByNameAsync(roleName);

        await Should.ThrowAsync<BusinessException>(async () =>
            await _roleScopes.SetAsync(new SetRoleDataScopeDto
            {
                RoleId = role!.Id,
                Scope = "NotAScope"
            }));
    }

    [Fact]
    public async Task 可配置的范围选项_来自领域常量()
    {
        var options = await _roleScopes.GetOptionsAsync();

        options.Select(o => o.Value).ShouldBe(DataScopes.Configurable);
        options.ShouldContain(o => o.Label == "仅本级");

        // 说明文案也必须由后端给：前端不写"码值 → 文案"的映射（那就是第二份码值）。
        // 少了这条断言，"说明没了"只会在界面上表现为少一行小字，没人会发现。
        foreach (var option in options)
        {
            option.Description.ShouldNotBeNullOrWhiteSpace(
                $"范围「{option.Value}」必须带一句解释（前端不再自己维护文案）");
        }

        // Custom 不在可配置列表里（前后端都不该给出这个选项）
        options.ShouldNotContain(o => o.Value == DataScopes.Custom);
    }
}
