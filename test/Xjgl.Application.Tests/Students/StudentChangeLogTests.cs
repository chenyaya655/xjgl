using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Guids;
using Volo.Abp.Timing;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Users;
using Xunit;

namespace Xjgl.Students;

/// <summary>
/// 学籍变更流水（留痕）的集成测试。
///
/// 【为什么这些用例重要】
/// 留痕最典型的失败方式是"**看起来记了，其实没记**"：
/// 字段名对不上、快照取在修改之后、密文比较导致"没改也报改了"——
/// 这些在界面上都表现为"有记录"，只有真去核对内容才发现是错的。
/// 因此这里不只断言"有流水"，而是逐条核对**字段、旧值、新值**。
///
/// 【守的四件事】
///   1. 建档 / 修改 / 删除都有留痕，删除后仍能查到是谁；
///   2. 修改**只记变化的字段**（没变的不写），且一次操作共享 BatchId；
///   3. 身份证号变更**只记摘要**，日志表里不留明文；
///   4. 留痕同样受数据范围约束（外校账号读不到）。
/// </summary>
public class StudentChangeLogTests : XjglIntegrationTestBase
{
    private const string Year = "2066-2067";

    private readonly IStudentAppService _students;
    private readonly IStudentChangeLogAppService _logs;
    private readonly IOrgAppService _orgAppService;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IClock _clock;
    private readonly FakeCurrentPrincipalAccessor _principal;

    public StudentChangeLogTests()
    {
        _students = GetRequiredService<IStudentAppService>();
        _logs = GetRequiredService<IStudentChangeLogAppService>();
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

    private async Task<OrgDto> CreateSchoolAsync(string name = "留痕测试学校")
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

    private async Task<StudentDto> CreateStudentAsync(
        Guid schoolId,
        string name = "留痕学生",
        string? idCard = null)
    {
        return await RetryOnDuplicateIdCardAsync(() =>
            _students.CreateAsync(new CreateUpdateStudentDto
            {
                Name = name,
                GenderCode = "1",
                BirthDate = new DateTime(2016, 5, 6),
                IdCardTypeCode = "1",
                IdCardNumber = idCard ?? NewUniqueIdCard(2016),
                SchoolOrgId = schoolId,
                AcademicYear = Year,
                PhoneNumber = "13800000000",
                StudentStatusCode = StudentStatuses.Studying
            }));
    }

    /// <summary>
    /// 把详情 DTO 还原成"更新入参"。
    ///
    /// 注意 <paramref name="idCard"/> 必须传**明文**：详情 DTO 里只有掩码，
    /// 而更新接口要求身份证号必填（传空会报「身份证不能为空」）。
    /// </summary>
    private static CreateUpdateStudentDto ToUpdateDto(StudentDto student, string idCard)
    {
        return new CreateUpdateStudentDto
        {
            Name = student.Name,
            GenderCode = student.GenderCode,
            BirthDate = student.BirthDate,
            IdCardTypeCode = student.IdCardTypeCode,
            IdCardNumber = idCard ?? string.Empty,
            SchoolOrgId = student.SchoolOrgId,
            AcademicYear = student.AcademicYear,
            PhoneNumber = student.PhoneNumber,
            StudentStatusCode = student.StudentStatusCode
        };
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

    /// <summary>换 principal 并新开 DI 作用域（ICurrentUser 是 Scoped 且缓存身份）。</summary>
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

    // ==================================================================
    // 建档 / 修改 / 删除
    // ==================================================================

    [Fact]
    public async Task 新增学生_记录建档流水()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id, "建档学生");

        var logs = await _logs.GetStudentLogsAsync(student.Id);

        logs.Items.Count.ShouldBe(1);
        var row = logs.Items[0];

        row.Action.ShouldBe(StudentChangeActions.Create);
        row.ActionName.ShouldBe("新建学籍");
        row.FieldLabel.ShouldBe("建档");
        row.NewValue.ShouldContain("建档学生");
        row.NewValue!.Contains(student.StudentNo).ShouldBeTrue("学籍辅号要留痕，否则删档后无法对账");
        row.OperatorName.ShouldBe("test-admin");
        row.StudentName.ShouldBe("建档学生");
    }

    [Fact]
    public async Task 修改学生_只记录发生变化的字段()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var card = NewUniqueIdCard(2016);
        var student = await CreateStudentAsync(school.Id, "变更学生", card);

        // 只改 电话 与 学籍状态，姓名/学年等保持不变
        var input = ToUpdateDto(student, card);
        input.PhoneNumber = "13900001111";
        input.StudentStatusCode = StudentStatuses.Suspended;

        await _students.UpdateAsync(student.Id, input);

        var logs = await _logs.GetStudentLogsAsync(student.Id);
        var changes = logs.Items.Where(l => l.Action == StudentChangeActions.Update).ToList();

        changes.Count.ShouldBe(2, "只有电话与学籍状态变了，其它字段不该产生记录");
        changes.Select(c => c.FieldLabel).ShouldContain("联系电话");
        changes.Select(c => c.FieldLabel).ShouldContain("学籍状态");

        var phone = changes.Single(c => c.FieldLabel == "联系电话");
        phone.OldValue.ShouldBe("13800000000");
        phone.NewValue.ShouldBe("13900001111");

        var status = changes.Single(c => c.FieldLabel == "学籍状态");
        status.OldValue.ShouldBe("在读（01）", "码值要翻译成中文，同时保留码值便于追溯");
        status.NewValue.ShouldBe("休学（02）");

        changes.Select(c => c.BatchId).Distinct().Count().ShouldBe(1, "一次修改内的多行共享同一个批次号");
        changes.ShouldAllBe(c => c.OperatorName == "test-admin");
    }

    [Fact]
    public async Task 修改学生_值没变时不写流水()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var card = NewUniqueIdCard(2016);
        var student = await CreateStudentAsync(school.Id, "无变化学生", card);

        // 原样提交（界面上的「保存」经常什么都没改）
        await _students.UpdateAsync(student.Id, ToUpdateDto(student, card));

        var logs = await _logs.GetStudentLogsAsync(student.Id);

        logs.Items.Count(l => l.Action == StudentChangeActions.Update)
            .ShouldBe(0, "没有实际变化就不该产生留痕，否则流水会被「空保存」淹没");
    }

    [Fact]
    public async Task 修改学生_更换身份证号_只记摘要不记明文()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var originalCard = NewUniqueIdCard(2016);
        var student = await CreateStudentAsync(school.Id, "换证学生", originalCard);

        var newCard = NewUniqueIdCard(2016);

        await _students.UpdateAsync(student.Id, ToUpdateDto(student, newCard));
        var logs = await _logs.GetStudentLogsAsync(student.Id);
        var idCardRow = logs.Items.Single(l => l.FieldLabel == "身份证号");

        idCardRow.NewValue!.ShouldStartWith("摘要");
        idCardRow.NewValue.Contains(newCard).ShouldBeFalse("日志表里绝不能出现身份证明文");
        idCardRow.OldValue!.Contains(originalCard).ShouldBeFalse();
        idCardRow.OldValue.ShouldNotBe(idCardRow.NewValue, "换了号码，摘要前缀应当不同");
    }

    [Fact]
    public async Task 删除学生_记录删除流水并保留姓名与辅号()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id, "待删学生");

        await _students.DeleteAsync(student.Id);

        var logs = await _logs.GetStudentLogsAsync(student.Id);
        var deleted = logs.Items.Single(l => l.Action == StudentChangeActions.Delete);

        deleted.OldValue.ShouldContain("待删学生");
        deleted.OldValue.ShouldContain(student.StudentNo);
        deleted.StudentName.ShouldBe("待删学生", "学生删掉之后，流水里仍要能看出这是谁");
    }

    [Fact]
    public async Task 变更流水_按时间倒序返回()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var card = NewUniqueIdCard(2016);
        var student = await CreateStudentAsync(school.Id, "倒序学生", card);

        var input = ToUpdateDto(student, card);
        input.PhoneNumber = "13700002222";
        await _students.UpdateAsync(student.Id, input);

        var logs = await _logs.GetStudentLogsAsync(student.Id);

        logs.Items.Count.ShouldBeGreaterThanOrEqualTo(2);

        for (var i = 1; i < logs.Items.Count; i++)
        {
            logs.Items[i - 1].OccurredAt.ShouldBeGreaterThanOrEqualTo(
                logs.Items[i].OccurredAt,
                "审计流水按时间倒序，最新的排在最前");
        }
    }

    // ==================================================================
    // 数据范围
    // ==================================================================

    [Fact]
    public async Task 外校账号_读取别人学校学生的流水_被拒绝()
    {
        SignInAsSuperAdmin();
        var schoolA = await CreateSchoolAsync("留痕甲校");
        var schoolB = await CreateSchoolAsync("留痕乙校");
        var studentInA = await CreateStudentAsync(schoolA.Id, "甲校留痕学生");

        var outsider = await LinkUserToOrgAsync(schoolB.Id, "乙校");

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            InScopeAsAsync(outsider, "乙校", Array.Empty<string>(), sp =>
                sp.GetRequiredService<IStudentChangeLogAppService>().GetStudentLogsAsync(studentInA.Id)));

        ex.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);
    }

    [Fact]
    public async Task 全局查询_只返回本范围内的流水()
    {
        SignInAsSuperAdmin();
        var schoolA = await CreateSchoolAsync("留痕丙校");
        var schoolB = await CreateSchoolAsync("留痕丁校");

        var studentInA = await CreateStudentAsync(schoolA.Id, "丙校留痕学生");
        var studentInB = await CreateStudentAsync(schoolB.Id, "丁校留痕学生");

        var countyUser = await LinkUserToOrgAsync(schoolA.Id, "丙校");

        var page = await InScopeAsAsync(countyUser, "丙校", Array.Empty<string>(), sp =>
            sp.GetRequiredService<IStudentChangeLogAppService>().GetListAsync(new StudentChangeLogQueryDto
            {
                MaxResultCount = 500
            }));

        page.Items.Any(l => l.StudentId == studentInA.Id)
            .ShouldBeTrue("本范围内学生的流水应当能查到（用于审计抽查）");

        page.Items.Any(l => l.StudentId == studentInB.Id)
            .ShouldBeFalse("范围外学校学生的流水不能出现 —— 留痕里有姓名与身份证摘要，越权读到同样是泄露");

        page.TotalCount.ShouldBe(page.Items.Count);
    }

    [Fact]
    public async Task 全局查询_可按动作过滤()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync("留痕动作校");
        var student = await CreateStudentAsync(school.Id, "动作过滤学生");

        var page = await _logs.GetListAsync(new StudentChangeLogQueryDto
        {
            Action = StudentChangeActions.Create,
            MaxResultCount = 200
        });

        page.Items.ShouldNotBeEmpty();
        page.Items.ShouldAllBe(l => l.Action == StudentChangeActions.Create);
        page.Items.Any(l => l.StudentId == student.Id).ShouldBeTrue();
    }
}
