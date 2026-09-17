using Shouldly;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Guids;
using Volo.Abp.Timing;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Students;
using Xjgl.Users;
using Xunit;

namespace Xjgl.DataScope;

/// <summary>
/// 数据范围（越权防护）的集成测试。
///
/// 【这组用例守的是什么】
/// 在实现数据范围之前，任何一个登录用户只要传一个别的学校的组织 Id，
/// 就能查到那个学校的学生 —— 也就是说"数据范围"完全由调用方决定。
/// 这组用例把几种身份的行为钉死：
///   省厅账号（全量）/ 县区管理员（本县子树）/ 校级账号（本校）/ 未关联组织（什么都看不到）。
///
/// 【为什么每次换身份都要新开 DI 作用域】
/// `ICurrentUser` 是 **Scoped** 的，并且会缓存用户 Id 与角色。
/// 集成测试里整个测试类共用根作用域，如果在同一个作用域里换身份，
/// 第二次之后 `ICurrentUser` 仍然返回第一次的值 ——
/// 表现是"所有身份都像第一个身份"，越权断言全部失效（本类最初 8 个用例全挂就是这个原因）。
/// 因此"以某个身份做某事"必须走 <see cref="InScopeAsAsync{T}"/>：换 principal + 新开作用域。
/// </summary>
public class DataScopeTests : XjglIntegrationTestBase
{
    private const string Year = "2070-2071";

    private readonly IOrgAppService _orgAppService;
    private readonly IStudentAppService _studentAppService;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IClock _clock;
    private readonly FakeCurrentPrincipalAccessor _principal;

    public DataScopeTests()
    {
        _orgAppService = GetRequiredService<IOrgAppService>();
        _studentAppService = GetRequiredService<IStudentAppService>();
        _predefines = GetRequiredService<IXjglRepository<SysUserPredefine>>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _clock = GetRequiredService<IClock>();
        _principal = GetRequiredService<FakeCurrentPrincipalAccessor>();
    }

    // ------------------------------------------------------------------
    // 脚手架
    // ------------------------------------------------------------------

    /// <summary>以超级管理员身份做数据准备（根作用域即可，全程都是同一个身份）。</summary>
    private void SignInAsSuperAdmin()
        => _principal.SetUser(Guid.NewGuid(), "admin", "admin");

    /// <summary>
    /// 以指定用户身份执行一段操作：**换 principal 并新开 DI 作用域**。
    /// 新作用域才会拿到新的 <c>ICurrentUser</c>（原因见类注释）。
    /// </summary>
    private async Task<T> InScopeAsAsync<T>(
        Guid userId,
        string userName,
        string[] roles,
        Func<IServiceProvider, Task<T>> action)
    {
        _principal.SetUser(userId, userName, roles);

        using var scope = ServiceProvider.CreateScope();

        return await action(scope.ServiceProvider);
    }

    /// <summary>插入预置记录并把用户关联到组织（数据范围的唯一来源）。</summary>
    private async Task<Guid> LinkUserToOrgAsync(Guid orgId, string prefix)
    {
        var userId = _guidGenerator.Create();

        var predefine = new SysUserPredefine(
            _guidGenerator.Create(),
            $"{prefix}用户",
            "139" + Random.Shared.Next(10000000, 99999999),
            orgId,
            "TESTCODE",
            _clock.Now.AddDays(30));

        predefine.MarkActivated(userId, _clock.Now);
        await _predefines.InsertAsync(predefine, autoSave: true);

        return userId;
    }

    /// <summary>省 → 市 → 甲县(两所学校) / 乙县(一所学校)。</summary>
    private async Task<(
        Guid Province, Guid CountyA, Guid SchoolA1, Guid SchoolA2,
        Guid CountyB, Guid SchoolB1)> BuildTreeAsync()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return await BuildTreeOnceAsync();
            }
            catch (BusinessException)
            {
                // 区划码撞号（测试库会累积数据），换一组重试
            }
        }

        throw new InvalidOperationException("多次尝试后仍无法生成唯一的组织编码。");
    }

    private async Task<(
        Guid Province, Guid CountyA, Guid SchoolA1, Guid SchoolA2,
        Guid CountyB, Guid SchoolB1)> BuildTreeOnceAsync()
    {
        var province = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.Province, Code = NewDivisionCode(), Name = "范围测试省厅"
        });

        var city = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            ParentId = province.Id, OrgType = OrgTypes.City,
            Code = NewDivisionCode(), Name = "范围测试市局"
        });

        var countyA = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            ParentId = city.Id, OrgType = OrgTypes.County,
            Code = NewDivisionCode(), Name = "范围测试甲县"
        });

        var countyB = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            ParentId = city.Id, OrgType = OrgTypes.County,
            Code = NewDivisionCode(), Name = "范围测试乙县"
        });

        var schoolA1 = await CreateSchoolAsync(countyA.Id, "范围测试甲县一中");
        var schoolA2 = await CreateSchoolAsync(countyA.Id, "范围测试甲县二中");
        var schoolB1 = await CreateSchoolAsync(countyB.Id, "范围测试乙县一中");

        return (province.Id, countyA.Id, schoolA1.Id, schoolA2.Id, countyB.Id, schoolB1.Id);
    }

    private async Task<OrgDto> CreateSchoolAsync(Guid parentId, string name)
    {
        return await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            ParentId = parentId, OrgType = OrgTypes.School,
            Code = new string('4', 5) + Random.Shared.Next(10000, 99999), Name = name
        });
    }

    private static string NewDivisionCode()
        => "41" + Random.Shared.Next(1000, 9999) + Random.Shared.Next(10, 99);

    private static string NewIdCard()
    {
        var first17 = "410102"
                      + $"20{Random.Shared.Next(10, 20)}{Random.Shared.Next(1, 13):D2}{Random.Shared.Next(1, 28):D2}"
                      + Random.Shared.Next(100, 999).ToString("D3");

        var weights = new[] { 7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2 };
        const string checkCodes = "10X98765432";

        var sum = 0;
        for (var i = 0; i < 17; i++)
        {
            sum += (first17[i] - '0') * weights[i];
        }

        return first17 + checkCodes[sum % 11];
    }

    private async Task<StudentDto> CreateStudentAsync(Guid schoolId, string name)
    {
        // 身份证号是"地区(6)+生日(8)+顺序(3)+校验位"，随机取值空间有限，
        // 而测试库会累积历史数据 —— 偶发撞号会让整条用例失败且看起来毫无规律。
        // 与其它测试类一致：撞号就换一个重试。
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return await _studentAppService.CreateAsync(new CreateUpdateStudentDto
                {
                    Name = name,
                    GenderCode = "1",
                    BirthDate = new DateTime(2016, 3, 1),
                    IdCardTypeCode = "1",
                    IdCardNumber = NewIdCard(),
                    SchoolOrgId = schoolId,
                    AcademicYear = Year,
                    StudentStatusCode = StudentStatuses.Studying
                });
            }
            catch (BusinessException ex) when (ex.Code == XjglDomainErrorCodes.DuplicateIdCard)
            {
                // 换一个身份证号重试
            }
        }

        throw new InvalidOperationException("多次尝试后仍无法生成不重复的身份证号。");
    }

    private static StudentQueryDto QueryAll() => new() { MaxResultCount = 500 };

    // ==================================================================
    // 省厅账号：全量
    // ==================================================================

    [Fact]
    public async Task 省厅账号_可以看到任意组织的学生()
    {
        SignInAsSuperAdmin();
        var tree = await BuildTreeAsync();
        var studentInB = await CreateStudentAsync(tree.SchoolB1, "乙县学生");

        // 省厅组织 → 范围 = All（与内置 admin 角色旁路效果一致，但不依赖角色）
        var provinceUser = await LinkUserToOrgAsync(tree.Province, "省厅");

        var count = await InScopeAsAsync(provinceUser, "省厅", Array.Empty<string>(), async sp =>
            (await sp.GetRequiredService<IStudentAppService>().GetListAsync(QueryAll())).TotalCount);

        count.ShouldBeGreaterThan(0);

        var inB = await InScopeAsAsync(provinceUser, "省厅", Array.Empty<string>(), async sp =>
            (await sp.GetRequiredService<IStudentAppService>().GetListAsync(new StudentQueryDto
            {
                SchoolOrgId = tree.SchoolB1, MaxResultCount = 100
            })).Items.Any(s => s.Id == studentInB.Id));

        inB.ShouldBeTrue("省厅账号应当能看到任意学校的学生");
    }

    [Fact]
    public async Task 内置admin角色_即使没有组织也不受范围限制()
    {
        SignInAsSuperAdmin();
        var tree = await BuildTreeAsync();
        var studentInB = await CreateStudentAsync(tree.SchoolB1, "乙县学生");

        // admin 角色 + 不关联任何组织 → 走超管旁路
        var adminId = Guid.NewGuid();

        var found = await InScopeAsAsync(adminId, "admin", new[] { "admin" }, async sp =>
            (await sp.GetRequiredService<IStudentAppService>().GetListAsync(new StudentQueryDto
            {
                SchoolOrgId = tree.SchoolB1, MaxResultCount = 100
            })).Items.Any(s => s.Id == studentInB.Id));

        found.ShouldBeTrue("超管旁路必须存在，否则系统管理员看不到任何数据");
    }

    // ==================================================================
    // 县区管理员：本县子树
    // ==================================================================

    [Fact]
    public async Task 县区管理员_只能看到本县学生()
    {
        SignInAsSuperAdmin();
        var tree = await BuildTreeAsync();
        var studentA1 = await CreateStudentAsync(tree.SchoolA1, "甲县一中学生");
        var studentA2 = await CreateStudentAsync(tree.SchoolA2, "甲县二中学生");
        var studentB = await CreateStudentAsync(tree.SchoolB1, "乙县学生");

        var countyUser = await LinkUserToOrgAsync(tree.CountyA, "甲县");

        var items = await InScopeAsAsync(countyUser, "甲县", Array.Empty<string>(), async sp =>
            (await sp.GetRequiredService<IStudentAppService>().GetListAsync(QueryAll())).Items);

        items.ShouldContain(s => s.Id == studentA1.Id);
        items.ShouldContain(s => s.Id == studentA2.Id, "本县另一所学校的学生也应可见");
        items.ShouldNotContain(s => s.Id == studentB.Id);
    }

    [Fact]
    public async Task 县区管理员_请求范围外的学校_返回空而不是报错()
    {
        SignInAsSuperAdmin();
        var tree = await BuildTreeAsync();
        await CreateStudentAsync(tree.SchoolB1, "乙县学生");

        var countyUser = await LinkUserToOrgAsync(tree.CountyA, "甲县");

        // 故意传乙县的学校 Id —— 这是越权尝试的典型形式
        var result = await InScopeAsAsync(countyUser, "甲县", Array.Empty<string>(), async sp =>
            await sp.GetRequiredService<IStudentAppService>().GetListAsync(new StudentQueryDto
            {
                SchoolOrgId = tree.SchoolB1, MaxResultCount = 100
            }));

        result.TotalCount.ShouldBe(0, "范围外的组织应当查不到任何数据");
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task 县区管理员_按Id读取范围外的学生_被拒绝()
    {
        SignInAsSuperAdmin();
        var tree = await BuildTreeAsync();
        var studentB = await CreateStudentAsync(tree.SchoolB1, "乙县学生");

        var countyUser = await LinkUserToOrgAsync(tree.CountyA, "甲县");

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await InScopeAsAsync(countyUser, "甲县", Array.Empty<string>(), async sp =>
                await sp.GetRequiredService<IStudentAppService>().GetAsync(studentB.Id)));

        exception.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);
    }

    [Fact]
    public async Task 县区管理员_不能把学生改到范围外的学校()
    {
        SignInAsSuperAdmin();
        var tree = await BuildTreeAsync();
        var studentA = await CreateStudentAsync(tree.SchoolA1, "甲县一中学生");

        var countyUser = await LinkUserToOrgAsync(tree.CountyA, "甲县");

        var exception = await Should.ThrowAsync<BusinessException>(async () =>
            await InScopeAsAsync(countyUser, "甲县", Array.Empty<string>(), async sp =>
                await sp.GetRequiredService<IStudentAppService>().UpdateAsync(studentA.Id, new CreateUpdateStudentDto
                {
                    Name = "甲县一中学生",
                    GenderCode = "1",
                    BirthDate = new DateTime(2016, 3, 1),
                    IdCardTypeCode = "1",
                    IdCardNumber = NewIdCard(),
                    // 改到乙县的学校 —— 越权
                    SchoolOrgId = tree.SchoolB1,
                    AcademicYear = Year,
                    StudentStatusCode = StudentStatuses.Studying
                })));

        exception.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);
    }

    // ==================================================================
    // 校级账号：只看本校
    // ==================================================================

    [Fact]
    public async Task 校级账号_只能看到本校学生()
    {
        SignInAsSuperAdmin();
        var tree = await BuildTreeAsync();
        var studentA1 = await CreateStudentAsync(tree.SchoolA1, "一中学生");
        var studentA2 = await CreateStudentAsync(tree.SchoolA2, "二中学生");

        var schoolUser = await LinkUserToOrgAsync(tree.SchoolA1, "一中");

        var items = await InScopeAsAsync(schoolUser, "一中", Array.Empty<string>(), async sp =>
            (await sp.GetRequiredService<IStudentAppService>().GetListAsync(QueryAll())).Items);

        items.ShouldContain(s => s.Id == studentA1.Id);
        items.ShouldNotContain(s => s.Id == studentA2.Id, "同县区的另一所学校也看不到");
    }

    // ==================================================================
    // 未关联组织
    // ==================================================================

    [Fact]
    public async Task 未关联组织的账号_什么都看不到()
    {
        SignInAsSuperAdmin();
        var tree = await BuildTreeAsync();
        await CreateStudentAsync(tree.SchoolA1, "甲县一中学生");

        // 登录了，但既没有预置记录、也没有 admin 角色 → 解析不出组织
        var orphanId = Guid.NewGuid();

        var result = await InScopeAsAsync(orphanId, "orphan-user", Array.Empty<string>(), async sp =>
            await sp.GetRequiredService<IStudentAppService>().GetListAsync(QueryAll()));

        result.TotalCount.ShouldBe(0, "解析不出组织时必须什么都看不到，而不是默认全都能看");
        result.Items.ShouldBeEmpty();
    }
}
