using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Guids;
using Volo.Abp.Timing;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Photos;
using Xjgl.Users;
using Xunit;

namespace Xjgl.Students;

/// <summary>
/// 学生档案子表（监护人 / 学习简历 / 奖励 / 处分 / 资助 / 体检）的集成测试。
///
/// 【为什么这组用例必须存在】
/// 上一轮这批接口只有"端到端探针"覆盖（`tools/probe-subtable.ps1`，需要手工启动主机才能跑），
/// 也就是说 **CI 守不住它们**：改坏了不会有人知道。这里把探针里的关键断言固化成集成测试。
///
/// 【这组用例守的四件事】
///   1. **身份证号只出不进地"半可见"**：写明文、读掩码、留空不覆盖 —— 少了任何一条，
///      要么泄露家长身份信息，要么把掩码当成新号码写回数据库；
///   2. **业务规则**：第一监护人唯一、体检一学年一条、处分撤销留痕、简历起止日期有序；
///   3. **越权防护**：子表是独立接口，**不会**自动继承学生主档的数据范围，
///      因此必须有用例证明"外校账号读/写别人学校的子表会被拦住"；
///   4. **码值翻译**：relationName / rewardTypeName / healthName 等由后端给出。
/// </summary>
public class StudentProfileTests : XjglIntegrationTestBase
{
    private const string Year = "2066-2067";

    private readonly IStudentProfileAppService _profile;
    private readonly IStudentAppService _students;
    private readonly IOrgAppService _orgAppService;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IXjglRepository<StuChangeLog> _changeLogs;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IClock _clock;
    private readonly FakeCurrentPrincipalAccessor _principal;

    public StudentProfileTests()
    {
        _profile = GetRequiredService<IStudentProfileAppService>();
        _students = GetRequiredService<IStudentAppService>();
        _orgAppService = GetRequiredService<IOrgAppService>();
        _predefines = GetRequiredService<IXjglRepository<SysUserPredefine>>();
        _changeLogs = GetRequiredService<IXjglRepository<StuChangeLog>>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _clock = GetRequiredService<IClock>();
        _principal = GetRequiredService<FakeCurrentPrincipalAccessor>();
    }

    // ==================================================================
    // 脚手架
    // ==================================================================

    private void SignInAsSuperAdmin()
        => _principal.SetUser(Guid.NewGuid(), "admin", "admin");

    private async Task<OrgDto> CreateSchoolAsync(string name = "子表测试学校")
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

    private async Task<StudentDto> CreateStudentAsync(Guid schoolId, string name = "子表学生")
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
                AcademicYear = Year,
                StudentStatusCode = StudentStatuses.Studying
            }));
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

    /// <summary>
    /// 以指定用户身份执行一段操作：**换 principal 并新开 DI 作用域**。
    ///
    /// `ICurrentUser` 是 Scoped 且会缓存用户 Id 与角色，同一作用域里换身份不会生效
    /// （详见 DataScopeTests 类注释里那次"8 个用例全挂"的教训）。
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

    // ==================================================================
    // 监护人
    // ==================================================================

    [Fact]
    public async Task 监护人_新增_接口只回掩码且不含明文()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        // 监护人身份证用不同出生年段，避免与学生号段混淆（监护人不做查重，仅便于人工排查）
        var idCard = NewUniqueIdCard(1975);

        var dto = await _profile.CreateGuardianAsync(student.Id, new CreateUpdateGuardianDto
        {
            Name = "家长甲",
            RelationCode = "1",
            IdCardNumber = idCard,
            PhoneNumber = "13800000001",
            IsPrimary = true
        });

        dto.MaskedIdCard.ShouldNotBeNull();
        dto.MaskedIdCard!.ShouldContain("*");
        dto.MaskedIdCard.Contains(idCard).ShouldBeFalse("掩码里不能出现完整明文");
        dto.MaskedIdCard.ShouldStartWith(idCard[..6]);
        dto.MaskedIdCard.ShouldEndWith(idCard[^4..]);
        dto.HasIdCard.ShouldBeTrue();
        dto.RelationName.ShouldBe("父亲", "关系码要由后端翻译成中文");
        dto.Seq.ShouldBe(1, "序号由服务端分配");
    }

    [Fact]
    public async Task 监护人_修改时不回传身份证号_保留原号()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var created = await _profile.CreateGuardianAsync(student.Id, new CreateUpdateGuardianDto
        {
            Name = "家长乙", RelationCode = "2", IdCardNumber = NewUniqueIdCard(1976)
        });

        // 界面上只显示掩码，用户没改就不会回传身份证号 —— 此时必须保留原号
        var updated = await _profile.UpdateGuardianAsync(created.Id, new CreateUpdateGuardianDto
        {
            Name = "家长乙", RelationCode = "2", PhoneNumber = "13900000009"
        });

        updated.HasIdCard.ShouldBeTrue("留空必须表示「不修改」，而不是「清空」");
        updated.MaskedIdCard.ShouldBe(created.MaskedIdCard);
        updated.PhoneNumber.ShouldBe("13900000009");
    }

    [Fact]
    public async Task 监护人_把第二个设为第一监护人_前一个自动降级()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var first = await _profile.CreateGuardianAsync(student.Id, new CreateUpdateGuardianDto
        {
            Name = "家长甲", RelationCode = "1", IsPrimary = true
        });

        var second = await _profile.CreateGuardianAsync(student.Id, new CreateUpdateGuardianDto
        {
            Name = "家长乙", RelationCode = "2", IsPrimary = true
        });

        var list = await _profile.GetGuardiansAsync(student.Id);

        list.Count(g => g.IsPrimary).ShouldBe(1, "第一监护人必须唯一，否则出事时不知道该联系谁");
        list.Single(g => g.IsPrimary).Id.ShouldBe(second.Id);
        list.Single(g => g.Id == first.Id).IsPrimary.ShouldBeFalse();
    }

    [Fact]
    public async Task 监护人_身份证校验位错误_被拒绝()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var bad = NewUniqueIdCard(1977);
        bad = bad[..17] + (bad[^1] == '0' ? '1' : '0'); // 故意改坏校验位

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _profile.CreateGuardianAsync(student.Id, new CreateUpdateGuardianDto
            {
                Name = "家长丙", RelationCode = "1", IdCardNumber = bad
            }));

        ex.Data["reason"].ToString().ShouldContain("身份证号不合法");
    }

    [Fact]
    public async Task 监护人_删除后列表为空()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var created = await _profile.CreateGuardianAsync(student.Id, new CreateUpdateGuardianDto
        {
            Name = "家长丁", RelationCode = "9"
        });

        await _profile.DeleteGuardianAsync(created.Id);

        (await _profile.GetGuardiansAsync(student.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task 监护人_序号按当前最大值递增()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var a = await _profile.CreateGuardianAsync(student.Id, new CreateUpdateGuardianDto { Name = "家长1" });
        var b = await _profile.CreateGuardianAsync(student.Id, new CreateUpdateGuardianDto { Name = "家长2" });
        var c = await _profile.CreateGuardianAsync(student.Id, new CreateUpdateGuardianDto { Name = "家长3" });

        a.Seq.ShouldBe(1);
        b.Seq.ShouldBe(2);
        c.Seq.ShouldBe(3);

        // 删掉中间一条后再新增：新序号必须比现有的都大，不能"补空位"——
        // 补空位会让两个人的序号相同，排序结果随数据库返回顺序漂移
        await _profile.DeleteGuardianAsync(b.Id);

        var d = await _profile.CreateGuardianAsync(student.Id, new CreateUpdateGuardianDto { Name = "家长4" });

        d.Seq.ShouldBe(4);
    }

    // ==================================================================
    // 学习简历
    // ==================================================================

    [Fact]
    public async Task 简历_结束日期早于开始日期_被拒绝()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _profile.CreateResumeAsync(student.Id, new CreateUpdateResumeDto
            {
                SchoolName = "某小学",
                StartDate = new DateTime(2020, 9, 1),
                EndDate = new DateTime(2019, 9, 1)
            }));

        ex.Data["reason"].ToString().ShouldContain("结束日期不能早于开始日期");
    }

    [Fact]
    public async Task 简历_增改删全流程()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var created = await _profile.CreateResumeAsync(student.Id, new CreateUpdateResumeDto
        {
            SchoolName = "附属幼儿园",
            StartDate = new DateTime(2016, 9, 1),
            EndDate = new DateTime(2019, 7, 1),
            Post = "小组长"
        });

        var updated = await _profile.UpdateResumeAsync(created.Id, new CreateUpdateResumeDto
        {
            SchoolName = "附属幼儿园",
            StartDate = new DateTime(2016, 9, 1),
            Post = "班长"
        });

        updated.Post.ShouldBe("班长");
        updated.EndDate.ShouldBeNull("更新时未传结束日期应清空（在读经历）");

        (await _profile.GetResumesAsync(student.Id)).Count.ShouldBe(1);

        await _profile.DeleteResumeAsync(created.Id);

        (await _profile.GetResumesAsync(student.Id)).ShouldBeEmpty();
    }

    // ==================================================================
    // 奖励 / 处分 / 资助
    // ==================================================================

    [Fact]
    public async Task 奖励_类型与级别都翻译成中文()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        await _profile.CreateRewardAsync(student.Id, new CreateUpdateRewardDto
        {
            RewardDate = new DateTime(2025, 6, 1),
            RewardName = "三好学生",
            RewardTypeCode = "1",
            RewardLevelCode = "1",
            AwardOrg = "子表测试学校"
        });

        var list = await _profile.GetRewardsAsync(student.Id);

        list.Count.ShouldBe(1);
        list[0].RewardTypeName.ShouldBe("三好学生");
        list[0].RewardLevelName.ShouldBe("校级");
    }

    [Fact]
    public async Task 处分_撤销后保留记录并自动补撤销日期()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var created = await _profile.CreatePunishmentAsync(student.Id, new CreateUpdatePunishmentDto
        {
            PunishDate = new DateTime(2024, 11, 1),
            PunishName = "警告",
            PunishTypeCode = "1",
            IsRevoked = true,
            RevokeReason = "申诉成功"
        });

        created.IsRevoked.ShouldBeTrue();
        created.RevokeDate.ShouldNotBeNull("撤销了却没有日期，档案上会是一条悬空记录");

        var list = await _profile.GetPunishmentsAsync(student.Id);

        list.Count.ShouldBe(1, "撤销不等于删除：曾经有过处分这段事实必须留痕");
        list[0].PunishTypeName.ShouldBe("警告");

        // 反向：把撤销状态改回去，撤销日期必须一并清掉
        var restored = await _profile.UpdatePunishmentAsync(created.Id, new CreateUpdatePunishmentDto
        {
            PunishDate = new DateTime(2024, 11, 1),
            PunishName = "警告",
            IsRevoked = false
        });

        restored.RevokeDate.ShouldBeNull();
    }

    [Fact]
    public async Task 资助_金额按decimal原样保存()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        await _profile.CreateSubsidyAsync(student.Id, new CreateUpdateSubsidyDto
        {
            AcademicYear = Year,
            SubsidyName = "寄宿生生活补助",
            SubsidyTypeCode = "3",
            Amount = 625.55m,
            FundingOrg = "县教育局"
        });

        var list = await _profile.GetSubsidiesAsync(student.Id);

        list.Count.ShouldBe(1);
        list[0].Amount.ShouldBe(625.55m, "金额用 decimal，不能出现浮点误差");
        list[0].SubsidyTypeName.ShouldBe("寄宿生生活补助");
    }

    // ==================================================================
    // 体质健康（体检）
    // ==================================================================

    [Fact]
    public async Task 体检_同一学年重复录入_被拒绝()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        await _profile.CreatePhysicalAsync(student.Id, new CreateUpdatePhysicalDto
        {
            AcademicYear = Year, Height = 132.5m, Weight = 28.4m, HealthCode = "1"
        });

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _profile.CreatePhysicalAsync(student.Id, new CreateUpdatePhysicalDto
            {
                AcademicYear = Year, Height = 133m
            }));

        ex.Data["reason"].ToString().ShouldContain("已存在");
    }

    [Fact]
    public async Task 体检_多学年记录按学年倒序返回()
    {
        SignInAsSuperAdmin();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        await _profile.CreatePhysicalAsync(student.Id, new CreateUpdatePhysicalDto
        {
            AcademicYear = "2024-2025", Height = 128m, HealthCode = "1"
        });

        await _profile.CreatePhysicalAsync(student.Id, new CreateUpdatePhysicalDto
        {
            AcademicYear = Year, Height = 132.5m, LeftVision = 5.0m, RightVision = 4.9m, HealthCode = "1"
        });

        var list = await _profile.GetPhysicalsAsync(student.Id);

        list.Count.ShouldBe(2);
        list[0].AcademicYear.ShouldBe(Year, "最新的学年排在最前");
        list[0].HealthName.ShouldBe("健康");
        list[0].LeftVision.ShouldBe(5.0m);
    }

    // ==================================================================
    // 越权防护（子表是独立接口，不会自动继承主档的数据范围）
    // ==================================================================

    [Fact]
    public async Task 外校账号_读取别人学校的子表_被拒绝()
    {
        SignInAsSuperAdmin();

        var schoolA = await CreateSchoolAsync("子表甲校");
        var schoolB = await CreateSchoolAsync("子表乙校");
        var studentInA = await CreateStudentAsync(schoolA.Id, "甲校学生");

        await _profile.CreateGuardianAsync(studentInA.Id, new CreateUpdateGuardianDto
        {
            Name = "甲校家长", RelationCode = "1", PhoneNumber = "13700000001"
        });

        // 把账号关联到 B 校 → 数据范围只有 B 校及其下级
        var outsider = await LinkUserToOrgAsync(schoolB.Id, "乙校");

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            InScopeAsAsync(outsider, "乙校", Array.Empty<string>(), sp =>
                sp.GetRequiredService<IStudentProfileAppService>().GetGuardiansAsync(studentInA.Id)));

        ex.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);
        ex.Data["reason"].ToString().ShouldContain("不在您的数据范围内");

        // 写方向同样要挡住 —— 只挡读不挡写，等于允许把数据塞进别人的学校
        var writeEx = await Should.ThrowAsync<BusinessException>(() =>
            InScopeAsAsync(outsider, "乙校", Array.Empty<string>(), sp =>
                sp.GetRequiredService<IStudentProfileAppService>().CreateGuardianAsync(
                    studentInA.Id,
                    new CreateUpdateGuardianDto { Name = "越权写入", RelationCode = "1" })));

        writeEx.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);
    }

    [Fact]
    public async Task 外校账号_修改子表行_同样被拒绝()
    {
        SignInAsSuperAdmin();

        var schoolA = await CreateSchoolAsync("子表丙校");
        var schoolB = await CreateSchoolAsync("子表丁校");
        var studentInA = await CreateStudentAsync(schoolA.Id, "丙校学生");

        var guardian = await _profile.CreateGuardianAsync(studentInA.Id, new CreateUpdateGuardianDto
        {
            Name = "丙校家长", RelationCode = "1"
        });

        var outsider = await LinkUserToOrgAsync(schoolB.Id, "丁校");

        // 修改/删除走的是"子表行 Id"，因此必须由行上的 StudentId 反查学生再校验范围；
        // 只校验子表行本身存在与否就漏了越权
        var ex = await Should.ThrowAsync<BusinessException>(() =>
            InScopeAsAsync(outsider, "丁校", Array.Empty<string>(), sp =>
                sp.GetRequiredService<IStudentProfileAppService>().UpdateGuardianAsync(
                    guardian.Id,
                    new CreateUpdateGuardianDto { Name = "被篡改", RelationCode = "1" })));

        ex.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);

        // 数据没有被改动
        var after = await _profile.GetGuardiansAsync(studentInA.Id);

        after.Single().Name.ShouldBe("丙校家长");
    }

    [Fact]
    public async Task 学生不存在_读取子表_抛实体不存在()
    {
        SignInAsSuperAdmin();

        await Should.ThrowAsync<EntityNotFoundException>(() =>
            _profile.GetGuardiansAsync(Guid.NewGuid()));
    }

    // ==================================================================
    // 删除学生时的级联软删（债 17）
    // ==================================================================

    [Fact]
    public async Task 删除学生_六张子表与留痕一并软删()
    {
        // 【这条用例守的是什么】
        // 子表都是独立路由，主档软删之后它们仍然是 is_deleted = false。
        // 后果不只是"统计多算了几个人"——监护人表里有**加密的身份证号与联系电话**，
        // 学生档案删了这些敏感信息还留在库里，属于"以为删了其实没删"。
        //
        // 断言方式：**直接查仓储**（`GetListAsync` 走的是"只返回未删"的口径），
        // 因此"查得到"就等于"没删干净"。用 API 查是不行的 ——
        // 主档一删，子表接口会因为"学生不存在"直接抛异常，断言会失去意义。
        SignInAsSuperAdmin();

        var school = await CreateSchoolAsync("级联测试学校");
        var student = await CreateStudentAsync(school.Id, "级联测试学生");

        // 六张子表各造一行（体检用另一个学年，避免与同年限制冲突）
        await _profile.CreateGuardianAsync(student.Id, new CreateUpdateGuardianDto
        {
            Name = "级联家长", RelationCode = "1", IdCardNumber = NewUniqueIdCard(1978),
            PhoneNumber = "13600000001"
        });

        await _profile.CreateResumeAsync(student.Id, new CreateUpdateResumeDto
        {
            SchoolName = "级联幼儿园",
            StartDate = new DateTime(2016, 9, 1),
            EndDate = new DateTime(2019, 7, 1)
        });

        await _profile.CreateRewardAsync(student.Id, new CreateUpdateRewardDto
        {
            RewardDate = new DateTime(2025, 6, 1), RewardName = "级联三好学生",
            RewardTypeCode = "1", RewardLevelCode = "1", AwardOrg = "级联测试学校"
        });

        await _profile.CreatePunishmentAsync(student.Id, new CreateUpdatePunishmentDto
        {
            PunishDate = new DateTime(2024, 11, 1), PunishName = "级联警告", PunishTypeCode = "1"
        });

        await _profile.CreateSubsidyAsync(student.Id, new CreateUpdateSubsidyDto
        {
            AcademicYear = Year, SubsidyName = "级联补助", SubsidyTypeCode = "3", Amount = 625.55m
        });

        await _profile.CreatePhysicalAsync(student.Id, new CreateUpdatePhysicalDto
        {
            AcademicYear = Year, Height = 132.5m, Weight = 28.4m, HealthCode = "1"
        });

        // 变更留痕：上面这些创建动作本身就会写入流水
        var logsBefore = await _changeLogs.GetListAsync(l => l.StudentId == student.Id);

        logsBefore.ShouldNotBeEmpty("子表增删改都会写留痕，这是本次断言的前提");
        var guardians = GetRequiredService<IXjglRepository<StuGuardian>>();
        var resumes = GetRequiredService<IXjglRepository<StuResume>>();
        var rewards = GetRequiredService<IXjglRepository<StuReward>>();
        var punishments = GetRequiredService<IXjglRepository<StuPunishment>>();
        var subsidies = GetRequiredService<IXjglRepository<StuSubsidy>>();
        var physicals = GetRequiredService<IXjglRepository<StuPhysical>>();
        var photos = GetRequiredService<IXjglRepository<StuPhoto>>();

        // 删之前每张表确实有数据（否则"删完是空"这个断言是假绿）
        (await guardians.GetListAsync(g => g.StudentId == student.Id)).Count.ShouldBe(1);
        (await resumes.GetListAsync(r => r.StudentId == student.Id)).Count.ShouldBe(1);
        (await rewards.GetListAsync(r => r.StudentId == student.Id)).Count.ShouldBe(1);
        (await punishments.GetListAsync(p => p.StudentId == student.Id)).Count.ShouldBe(1);
        (await subsidies.GetListAsync(s => s.StudentId == student.Id)).Count.ShouldBe(1);
        (await physicals.GetListAsync(p => p.StudentId == student.Id)).Count.ShouldBe(1);

        await _students.DeleteAsync(student.Id);

        (await guardians.GetListAsync(g => g.StudentId == student.Id))
            .ShouldBeEmpty("监护人表含加密身份证号与电话，必须随学生一起软删");
        (await resumes.GetListAsync(r => r.StudentId == student.Id)).ShouldBeEmpty();
        (await rewards.GetListAsync(r => r.StudentId == student.Id)).ShouldBeEmpty();
        (await punishments.GetListAsync(p => p.StudentId == student.Id)).ShouldBeEmpty();
        (await subsidies.GetListAsync(s => s.StudentId == student.Id)).ShouldBeEmpty();
        (await physicals.GetListAsync(p => p.StudentId == student.Id)).ShouldBeEmpty();
        (await photos.GetListAsync(p => p.StudentId == student.Id)).ShouldBeEmpty();

        // ★ 留痕**必须留下**，而且要比删除前更多（多了一条「删除」流水）。
        // 这是本次删除唯一的凭证：主档已经查不到了，"谁删了哪个学生"只能靠它。
        // 审计数据与业务数据的生命周期是**分开**的 —— 我第一版把留痕也一起软删了，
        // 结果打红了既有用例 `删除学生_记录删除流水并保留姓名与辅号`。
        var logsAfter = await _changeLogs.GetListAsync(l => l.StudentId == student.Id);

        logsAfter.Count.ShouldBeGreaterThan(logsBefore.Count,
            "删除动作本身也要留痕，且历史流水不能被级联删掉");

        // 主档本身也读不到了（与本用例的基线口径一致）
        (await GetRequiredService<IXjglRepository<StuStudent>>()
                .GetListAsync(s => s.Id == student.Id))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task 删除学生_只删该学生的子表_不动别人的()
    {
        // 级联删除最容易写错的地方是**少写了 StudentId 条件**：
        // 那样一次删学生会把全库同表的行都软删掉，而这种破坏性错误
        // 在"只造一个学生"的用例里根本看不出来 —— 所以这里必须造两个。
        SignInAsSuperAdmin();

        var school = await CreateSchoolAsync("级联隔离学校");
        var victim = await CreateStudentAsync(school.Id, "被删学生");
        var bystander = await CreateStudentAsync(school.Id, "保留学生");

        await _profile.CreateGuardianAsync(victim.Id, new CreateUpdateGuardianDto
        {
            Name = "被删家长", RelationCode = "1"
        });

        await _profile.CreateGuardianAsync(bystander.Id, new CreateUpdateGuardianDto
        {
            Name = "保留家长", RelationCode = "1"
        });

        var guardians = GetRequiredService<IXjglRepository<StuGuardian>>();

        await _students.DeleteAsync(victim.Id);

        (await guardians.GetListAsync(g => g.StudentId == victim.Id)).ShouldBeEmpty();
        (await guardians.GetListAsync(g => g.StudentId == bystander.Id))
            .ShouldHaveSingleItem().Name.ShouldBe("保留家长");
    }
}
