using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Guids;
using Volo.Abp.Timing;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Users;
using Xunit;

namespace Xjgl.Students;

/// <summary>
/// 学籍卡 / 花名册打印的集成测试（真实数据库 + 真实 PDF 渲染）。
///
/// 【守三件事】
///   1. **产物是合法 PDF**：不是空字节、不是 JSON 报错、能打开；
///   2. **数据范围**：卡片含出生日期/住址/监护人电话，越权拿到就是泄露个人信息；
///   3. **筛选口径与列表一致**：花名册走的是同一个列表查询，
///      不允许出现"界面只看到 3 个学生、花名册却印出 300 个"。
///
/// 说明：PDF 内部是压缩流，无法直接断言"某个学生名字在不在里面"，
/// 因此用**行数回传**（<see cref="PrintFileResult.RowCount"/>）来断言内容规模，
/// 再用"不同数据 → 不同字节长度"侧面验证文本真的进了版面。
/// </summary>
public class StudentPrintTests : XjglIntegrationTestBase
{
    private const string Year = "2069-2070";

    private readonly IStudentPrintService _print;
    private readonly IStudentAppService _students;
    private readonly IOrgAppService _orgAppService;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IClock _clock;
    private readonly FakeCurrentPrincipalAccessor _principal;

    public StudentPrintTests()
    {
        _print = GetRequiredService<IStudentPrintService>();
        _students = GetRequiredService<IStudentAppService>();
        _orgAppService = GetRequiredService<IOrgAppService>();
        _predefines = GetRequiredService<IXjglRepository<SysUserPredefine>>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _clock = GetRequiredService<IClock>();
        _principal = GetRequiredService<FakeCurrentPrincipalAccessor>();
    }

    // ------------------------------------------------------------------
    // 脚手架
    // ------------------------------------------------------------------

    private void SignInAsSuperAdmin()
        => _principal.SetUser(Guid.NewGuid(), "test-admin", "admin");

    private async Task<OrgDto> CreateSchoolAsync(string name = "打印测试学校")
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return await _orgAppService.CreateAsync(new CreateUpdateOrgDto
                {
                    OrgType = OrgTypes.School,
                    Code = "41" + Random.Shared.Next(10000000, 99999999),
                    Name = name
                });
            }
            catch (BusinessException)
            {
                // 学校标识码撞号（测试库跨轮累积），换一组重试
            }
        }

        throw new InvalidOperationException("多次尝试后仍无法生成唯一的学校标识码。");
    }

    private async Task<StudentDto> CreateStudentAsync(Guid schoolId, string name, string? academicYear = null)
    {
        return await RetryOnDuplicateIdCardAsync(() =>
            _students.CreateAsync(new CreateUpdateStudentDto
            {
                Name = name,
                GenderCode = "1",
                BirthDate = new DateTime(2016, 5, 6),
                IdCardTypeCode = "1",
                IdCardNumber = NewUniqueIdCard(2016),
                SchoolOrgId = schoolId,
                AcademicYear = academicYear ?? Year,
                HomeAddress = "郑州市金水区某某路 1 号",
                PhoneNumber = "13800000000",
                StudentStatusCode = StudentStatuses.Studying
            }));
    }

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

    private async Task<T> InScopeAsAsync<T>(
        Guid userId,
        string userName,
        Func<IServiceProvider, Task<T>> action)
    {
        _principal.SetUser(userId, userName, Array.Empty<string>());

        using var scope = ServiceProvider.CreateScope();

        return await action(scope.ServiceProvider);
    }

    private static void AssertValidPdf(byte[] pdf, int minBytes = 2000)
    {
        pdf.Length.ShouldBeGreaterThan(minBytes);
        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-", "产物必须是 PDF（否则浏览器会当成损坏文件）");
        Encoding.ASCII.GetString(pdf, pdf.Length - 6, 6).Trim().ShouldBe("%%EOF");
    }

    // ==================================================================
    // 学籍卡
    // ==================================================================

    [Fact]
    public async Task 学籍卡_范围内学生_产出合法PDF()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id, "打印学生");

        var file = await _print.RenderStudentCardAsync(student.Id);

        AssertValidPdf(file.Content);
        file.ContentType.ShouldBe("application/pdf");
        file.FileName.ShouldContain("学籍卡");
        file.FileName.ShouldEndWith(".pdf");
        file.FileName.ShouldContain(student.StudentNo);
        file.RowCount.ShouldBeNull("学籍卡是单人文档，没有「行数」的概念");
    }

    [Fact]
    public async Task 学籍卡_不同学生_产物不同()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var a = await CreateStudentAsync(school.Id, "打印学生甲");
        var b = await CreateStudentAsync(school.Id, "打印学生乙");

        var cardA = await _print.RenderStudentCardAsync(a.Id);
        var cardB = await _print.RenderStudentCardAsync(b.Id);

        // 断言"整份字节不同"而不是"长度不同"：PDF 是压缩流，
        // 两份内容不同但长度恰好相同的概率并不低（这组用例就翻过一次车，表现为偶发失败）
        cardA.Content.SequenceEqual(cardB.Content).ShouldBeFalse(
            "换了学生，产物必须不同：完全一致说明姓名/辅号没进版面");
        cardA.FileName.ShouldNotBe(cardB.FileName);
    }

    [Fact]
    public async Task 学籍卡_含监护人与照片缺失_依然出卡()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id, "打印学生丙");

        // 学生没有照片（未上传）时，卡片应当留出照片位并写"未采集照片"，而不是报错
        var file = await _print.RenderStudentCardAsync(student.Id);

        AssertValidPdf(file.Content);
    }

    [Fact]
    public async Task 学籍卡_外校账号_被拒绝()
    {
        SignInAsSuperAdmin();
        var schoolA = await CreateSchoolAsync("打印甲校");
        var schoolB = await CreateSchoolAsync("打印乙校");
        var studentInA = await CreateStudentAsync(schoolA.Id, "甲校打印学生");

        var outsider = await LinkUserToOrgAsync(schoolB.Id, "打印乙校");

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            InScopeAsAsync(outsider, "打印乙校", sp =>
                sp.GetRequiredService<IStudentPrintService>().RenderStudentCardAsync(studentInA.Id)));

        ex.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);
    }

    [Fact]
    public async Task 学籍卡_学生不存在_抛实体不存在()
    {
        SignInAsSuperAdmin();

        await Should.ThrowAsync<EntityNotFoundException>(() =>
            _print.RenderStudentCardAsync(Guid.NewGuid()));
    }

    // ==================================================================
    // 花名册
    // ==================================================================

    [Fact]
    public async Task 花名册_按学校过滤_行数与列表一致()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync("花名册测试学校");

        await CreateStudentAsync(school.Id, "花名册学生一");
        await CreateStudentAsync(school.Id, "花名册学生二");
        await CreateStudentAsync(school.Id, "花名册学生三");

        var file = await _print.RenderRosterAsync(new StudentRosterQueryDto
        {
            SchoolOrgId = school.Id,
            AcademicYear = Year,
            MaxRows = 500
        });

        AssertValidPdf(file.Content, 3000);

        // 与列表查询口径一致 —— 这是"界面看到 3 个、表里印出 300 个"这类问题的防线
        var list = await _students.GetListAsync(new StudentQueryDto
        {
            SchoolOrgId = school.Id, AcademicYear = Year, MaxResultCount = 500
        });

        file.RowCount.ShouldBe(list.Items.Count);
        file.RowCount!.Value.ShouldBeGreaterThanOrEqualTo(3);
        file.FileName.ShouldContain("花名册");
        file.FileName.ShouldContain("花名册测试学校");
    }

    [Fact]
    public async Task 花名册_没有学生时也出表()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync("空花名册学校");

        var file = await _print.RenderRosterAsync(new StudentRosterQueryDto
        {
            SchoolOrgId = school.Id,
            AcademicYear = Year
        });

        AssertValidPdf(file.Content, 1000);
        file.RowCount.ShouldBe(0, "空表也要出，并在表里写明「没有符合条件的学生」，而不是报错");
    }

    [Fact]
    public async Task 花名册_姓名可脱敏()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync("脱敏花名册学校");
        await CreateStudentAsync(school.Id, "脱敏学生");

        var masked = await _print.RenderRosterAsync(new StudentRosterQueryDto
        {
            SchoolOrgId = school.Id, AcademicYear = Year, MaskNames = true
        });

        var plain = await _print.RenderRosterAsync(new StudentRosterQueryDto
        {
            SchoolOrgId = school.Id, AcademicYear = Year, MaskNames = false
        });

        AssertValidPdf(masked.Content, 1000);

        // 脱敏会改变姓名文本，产物必须跟着变
        masked.Content.SequenceEqual(plain.Content).ShouldBeFalse("脱敏开关没有影响产物，说明姓名没被真正替换");
    }

    [Fact]
    public async Task 花名册_请求范围外的学校_被拒绝()
    {
        SignInAsSuperAdmin();
        var schoolA = await CreateSchoolAsync("花名册甲校");
        var schoolB = await CreateSchoolAsync("花名册乙校");

        await CreateStudentAsync(schoolA.Id, "花名册甲校学生");

        var outsider = await LinkUserToOrgAsync(schoolB.Id, "花名册乙校");

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            InScopeAsAsync(outsider, "花名册乙校", sp =>
                sp.GetRequiredService<IStudentPrintService>().RenderRosterAsync(new StudentRosterQueryDto
                {
                    SchoolOrgId = schoolA.Id, MaxRows = 50
                })));

        ex.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);
    }

    [Fact]
    public async Task 花名册_外校账号不指定学校_不会打印别人的学生()
    {
        SignInAsSuperAdmin();
        var schoolA = await CreateSchoolAsync("范围甲校");
        var schoolB = await CreateSchoolAsync("范围乙校");

        await CreateStudentAsync(schoolA.Id, "范围甲校学生");
        var mine = await CreateStudentAsync(schoolB.Id, "范围乙校学生");

        var user = await LinkUserToOrgAsync(schoolB.Id, "范围乙校");

        var file = await InScopeAsAsync(user, "范围乙校", sp =>
            sp.GetRequiredService<IStudentPrintService>().RenderRosterAsync(new StudentRosterQueryDto
            {
                MaxRows = 500
            }));

        // 不指定学校时也不能把范围外的学生印出来
        file.RowCount!.Value.ShouldBeGreaterThanOrEqualTo(1);

        var visibleIds = new List<Guid>();

        await InScopeAsAsync(user, "范围乙校", async sp =>
        {
            var page = await sp.GetRequiredService<IStudentAppService>().GetListAsync(new StudentQueryDto
            {
                MaxResultCount = 500
            });

            visibleIds.AddRange(page.Items.Select(i => i.Id));

            return true;
        });

        visibleIds.ShouldContain(mine.Id);
        file.RowCount.ShouldBe(visibleIds.Count, "花名册行数必须等于当前身份能看到的行数");
    }
}
