using Shouldly;
using Volo.Abp;
using Xjgl.Organizations;
using Xunit;

namespace Xjgl.Students;

/// <summary>
/// 学生学籍管理的集成测试。
///
/// 重点验证三件事：
///   1) 学籍辅号生成（规则拼装 + 按学校自增的流水号）；
///   2) 重复学籍检测（身份证号摘要跨校查重）；
///   3) 生僻字姓名与身份证号脱敏在真实数据库读写链路里不出问题。
/// </summary>
public class StudentAppServiceTests : XjglIntegrationTestBase
{
    private const string Year = "2050-2051";

    private readonly IStudentAppService _studentAppService;
    private readonly IOrgAppService _orgAppService;
    private readonly IGradeClassAppService _gradeClassAppService;

    public StudentAppServiceTests()
    {
        _studentAppService = GetRequiredService<IStudentAppService>();
        _orgAppService = GetRequiredService<IOrgAppService>();
        _gradeClassAppService = GetRequiredService<IGradeClassAppService>();
    }

    // ------------------------------------------------------------------
    // 测试辅助
    // ------------------------------------------------------------------

    private async Task<OrgDto> CreateSchoolAsync()
    {
        return await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School,
            // 学校标识码 10 位，前 6 位是行政区划码
            Code = "41" + Random.Shared.Next(10000000, 99999999),
            Name = "学生测试学校"
        });
    }

    // 身份证号统一用基类的 NewUniqueIdCard()（计数器推导，跨轮几乎不重复）。
    // 本文件早期自带了一个"固定地区码 + 3 位随机顺序码"的生成器，号段只有 899 个，
    // 测试库跑过若干轮之后会随机撞上历史学生 —— 查重用例的首个 CreateAsync 会直接抛
    // DuplicateIdCard，表现为用例偶发变红。生成器已在基类里统一。

    private async Task<StudentDto> CreateStudentAsync(
        Guid schoolId,
        string name = "张三",
        string? idCard = null,
        DateTime? birthDate = null,
        Guid? gradeId = null,
        Guid? classId = null)
    {
        CreateUpdateStudentDto Build(string? card) => new()
        {
            Name = name,
            GenderCode = "1",
            BirthDate = birthDate ?? new DateTime(2015, 6, 1),
            IdCardTypeCode = "1",
            IdCardNumber = card ?? NewUniqueIdCard(),
            SchoolOrgId = schoolId,
            AcademicYear = Year,
            GradeId = gradeId,
            ClassId = classId,
            StudentStatusCode = StudentStatuses.Studying
        };

        // 显式传入身份证号的用例（查重、加密存储）必须原样使用，不能换号重试；
        // 自动生成的号则允许撞号后重试
        return idCard != null
            ? await _studentAppService.CreateAsync(Build(idCard))
            : await RetryOnDuplicateIdCardAsync(() => _studentAppService.CreateAsync(Build(null)));
    }

    // ------------------------------------------------------------------
    // 学籍辅号生成
    // ------------------------------------------------------------------

    [Fact]
    public async Task 新增学生_自动生成16位学籍辅号()
    {
        var school = await CreateSchoolAsync();

        var student = await CreateStudentAsync(school.Id);

        student.StudentNo.Length.ShouldBe(16);
        // 学籍辅号 = 学校标识码(10) + 流水号(6)
        student.StudentNo.ShouldStartWith(school.Code);
        student.StudentNo.ShouldEndWith("000001");
        student.AcademicYear.ShouldBe(Year);
        student.StudentStatusName.ShouldBe("在读");
        student.GenderName.ShouldBe("男");
    }

    [Fact]
    public async Task 同一学校的流水号依次递增()
    {
        var school = await CreateSchoolAsync();

        var first = await CreateStudentAsync(school.Id, "学生甲");
        var second = await CreateStudentAsync(school.Id, "学生乙");
        var third = await CreateStudentAsync(school.Id, "学生丙");

        first.StudentNo.ShouldEndWith("000001");
        second.StudentNo.ShouldEndWith("000002");
        third.StudentNo.ShouldEndWith("000003");
    }

    [Fact]
    public async Task 不同学校的流水号互相独立()
    {
        var schoolA = await CreateSchoolAsync();
        var schoolB = await CreateSchoolAsync();

        var a1 = await CreateStudentAsync(schoolA.Id, "A校学生");
        var b1 = await CreateStudentAsync(schoolB.Id, "B校学生");

        a1.StudentNo.ShouldStartWith(schoolA.Code);
        b1.StudentNo.ShouldStartWith(schoolB.Code);

        // 两校的 1 号学生，学籍辅号必然不同（学校标识码不同）
        a1.StudentNo.ShouldNotBe(b1.StudentNo);
        a1.StudentNo.ShouldEndWith("000001");
        b1.StudentNo.ShouldEndWith("000001");
    }

    // ------------------------------------------------------------------
    // 重复学籍检测
    // ------------------------------------------------------------------

    [Fact]
    public async Task 同一身份证号不允许重复建籍_即使跨校()
    {
        var schoolA = await CreateSchoolAsync();
        var schoolB = await CreateSchoolAsync();
        var idCard = NewUniqueIdCard();

        await CreateStudentAsync(schoolA.Id, "张三", idCard);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await CreateStudentAsync(schoolB.Id, "张三", idCard));

        // 断言错误码而不是本地化消息（消息取决于资源文件，错误码才是契约）
        ex.Code.ShouldBe(XjglDomainErrorCodes.DuplicateIdCard);
        ex.Data["reason"].ShouldNotBeNull();
    }

    [Fact]
    public async Task 重复学籍检测接口_能跨校命中并识别疑似重复()
    {
        var schoolA = await CreateSchoolAsync();
        var schoolB = await CreateSchoolAsync();
        var idCard = NewUniqueIdCard();
        var birthDate = new DateTime(2015, 6, 1);

        var existing = await CreateStudentAsync(schoolA.Id, "李四", idCard, birthDate);

        var result = await _studentAppService.CheckDuplicateAsync(new CheckStudentDuplicateDto
        {
            Name = "李四",
            IdCardNumber = idCard,
            BirthDate = birthDate
        });

        result.HasDuplicate.ShouldBeTrue();
        result.Matched.ShouldContain(m => m.Id == existing.Id);
        result.Reasons.ShouldContain(r => r.Contains("疑似重复建籍"));
    }

    [Fact]
    public async Task 重复学籍检测_排除自己后不报重复()
    {
        var school = await CreateSchoolAsync();
        var idCard = NewUniqueIdCard();
        var student = await CreateStudentAsync(school.Id, "王五", idCard);

        var result = await _studentAppService.CheckDuplicateAsync(new CheckStudentDuplicateDto
        {
            Name = "王五",
            IdCardNumber = idCard,
            ExcludeId = student.Id
        });

        result.HasDuplicate.ShouldBeFalse();
    }

    [Fact]
    public async Task 重复学籍检测_身份证号非法时直接给出原因()
    {
        var result = await _studentAppService.CheckDuplicateAsync(new CheckStudentDuplicateDto
        {
            Name = "赵六",
            IdCardNumber = "110105194912310021"
        });

        result.HasDuplicate.ShouldBeFalse();
        result.Reasons.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task 非法身份证号不允许建籍()
    {
        var school = await CreateSchoolAsync();

        await Should.ThrowAsync<BusinessException>(async () =>
            await CreateStudentAsync(school.Id, "孙七", "110105194912310021"));
    }

    // ------------------------------------------------------------------
    // 生僻字与脱敏
    // ------------------------------------------------------------------

    [Fact]
    public async Task 生僻字姓名可以正常建籍与查询()
    {
        var school = await CreateSchoolAsync();

        // U+20BB7（CJK 扩展 B 区）
        var student = await CreateStudentAsync(school.Id, "\U00020BB7田");

        student.Name.ShouldBe("\U00020BB7田");
        student.HasRareCharacter.ShouldBeTrue();

        var loaded = await _studentAppService.GetAsync(student.Id);
        loaded.Name.ShouldBe("\U00020BB7田");
        loaded.HasRareCharacter.ShouldBeTrue();

        // 按姓名关键字能查到（生僻字按原样匹配）
        //
        // 注意必须同时按学校过滤：测试库会累积历史数据，而每次运行都会新增一个
        // 同名的"𰻞田"学生；只按关键字查、再取前 20 条时，新学生可能被挤出结果集 ——
        // 表现为"某次运行突然失败、再跑一次又过了"的偶发失败。
        // 本次学校是新建的，因此加上学校条件后结果稳定唯一。
        var list = await _studentAppService.GetListAsync(new StudentQueryDto
        {
            SchoolOrgId = school.Id,
            Keyword = "\U00020BB7田",
            MaxResultCount = 20
        });

        list.Items.ShouldContain(i => i.Id == student.Id);
    }

    [Fact]
    public async Task 常用字姓名不标记为生僻字()
    {
        var school = await CreateSchoolAsync();

        var student = await CreateStudentAsync(school.Id, "张三");

        student.HasRareCharacter.ShouldBeFalse();
    }

    [Fact]
    public async Task 身份证号在返回时脱敏_保留前6后4()
    {
        var school = await CreateSchoolAsync();
        var idCard = NewUniqueIdCard();

        var student = await CreateStudentAsync(school.Id, "周八", idCard);

        student.MaskedIdCard.ShouldNotBeNull();
        student.MaskedIdCard!.ShouldNotBe(idCard);
        student.MaskedIdCard.ShouldContain("********");
        student.MaskedIdCard.ShouldStartWith(idCard[..6]);
        student.MaskedIdCard.ShouldEndWith(idCard[^4..]);
    }

    [Fact]
    public async Task 年龄按出生日期计算()
    {
        var school = await CreateSchoolAsync();

        var birthDate = DateTime.Today.AddYears(-10).AddDays(-1);
        var student = await CreateStudentAsync(school.Id, "年龄测试", birthDate: birthDate);

        student.Age.ShouldBe(10);
    }

    // ------------------------------------------------------------------
    // 查询
    // ------------------------------------------------------------------

    [Fact]
    public async Task 按学校与学年查询学生()
    {
        var school = await CreateSchoolAsync();
        var created = await CreateStudentAsync(school.Id);

        var list = await _studentAppService.GetListAsync(new StudentQueryDto
        {
            SchoolOrgId = school.Id,
            AcademicYear = Year,
            MaxResultCount = 50
        });

        list.TotalCount.ShouldBeGreaterThanOrEqualTo(1);
        list.Items.ShouldContain(i => i.Id == created.Id);
    }

    [Fact]
    public async Task 按班级查询学生()
    {
        var school = await CreateSchoolAsync();

        var grade = (await _gradeClassAppService.BatchGenerateGradesAsync(new BatchGenerateGradesDto
        {
            SchoolOrgId = school.Id, AcademicYear = Year, GradeCodes = new List<string> { "01" }
        })).Items.Single();

        var classes = await _gradeClassAppService.BatchGenerateClassesAsync(new BatchGenerateClassesDto
        {
            GradeId = grade.Id, ClassCount = 2
        });

        var targetClass = classes.Items.First();

        var student = await CreateStudentAsync(school.Id, "班级学生", gradeId: grade.Id, classId: targetClass.Id);

        var list = await _studentAppService.GetListAsync(new StudentQueryDto
        {
            ClassId = targetClass.Id,
            MaxResultCount = 50
        });

        list.Items.ShouldContain(i => i.Id == student.Id);
        list.Items.Single(i => i.Id == student.Id).ClassName.ShouldBe(targetClass.ClassName);
        list.Items.Single(i => i.Id == student.Id).GradeName.ShouldBe(grade.GradeName);
    }

    [Fact]
    public async Task 按组织子树查询学生()
    {
        var province = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.Province,
            Code = "9" + Guid.NewGuid().ToString("N")[..8],
            Name = "学生测试省厅"
        });

        // 必须补齐"市 → 县"两级：学校只能挂在县区下（见 OrgAppService.EnsureHierarchy），
        // 直接建在省厅下会被拒绝。市局/县局的编码没有格式要求 ——
        // 只有**学校**必须是 10 位学校标识码（学籍辅号的 {SchoolCode:10} 段依赖它）；
        // 这里沿用 GUID 片段而不是随机 6 位行政区划码，是为了不随测试库累积而撞号。
        var city = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.City,
            Code = "9" + Guid.NewGuid().ToString("N")[..8],
            Name = "学生测试市局",
            ParentId = province.Id
        });

        var county = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.County,
            Code = "9" + Guid.NewGuid().ToString("N")[..8],
            Name = "学生测试县局",
            ParentId = city.Id
        });

        var school = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School,
            Code = "41" + Random.Shared.Next(10000000, 99999999),
            Name = "下属学校",
            ParentId = county.Id
        });

        var student = await CreateStudentAsync(school.Id, "下属学校学生");

        var list = await _studentAppService.GetListAsync(new StudentQueryDto
        {
            SchoolOrgId = province.Id,
            IncludeDescendantOrgs = true,
            MaxResultCount = 100
        });

        list.Items.ShouldContain(i => i.Id == student.Id);
    }

    [Fact]
    public async Task 按学籍状态与性别过滤()
    {
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id, "状态测试");

        var byStatus = await _studentAppService.GetListAsync(new StudentQueryDto
        {
            StudentStatusCode = StudentStatuses.Studying,
            SchoolOrgId = school.Id,
            MaxResultCount = 50
        });
        byStatus.Items.ShouldContain(i => i.Id == student.Id);

        var byGender = await _studentAppService.GetListAsync(new StudentQueryDto
        {
            GenderCode = "1",
            SchoolOrgId = school.Id,
            MaxResultCount = 50
        });
        byGender.Items.ShouldContain(i => i.Id == student.Id);
    }

    [Fact]
    public async Task 学生只能挂在学校类型的组织下()
    {
        var province = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.Province,
            Code = "9" + Guid.NewGuid().ToString("N")[..8],
            Name = "非法挂靠省厅"
        });

        await Should.ThrowAsync<BusinessException>(async () =>
            await CreateStudentAsync(province.Id, "挂错地方"));
    }

    [Fact]
    public async Task 修改学生信息并保持学籍辅号不变()
    {
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id, "改名前");

        var updated = await _studentAppService.UpdateAsync(student.Id, new CreateUpdateStudentDto
        {
            Name = "改名后",
            GenderCode = "2",
            BirthDate = student.BirthDate,
            IdCardTypeCode = "1",
            IdCardNumber = NewUniqueIdCard(),
            SchoolOrgId = school.Id,
            AcademicYear = Year,
            HouseholdAddress = "河南省郑州市金水区某某路 1 号",
            IsLeftBehindChild = true
        });

        updated.Name.ShouldBe("改名后");
        updated.GenderCode.ShouldBe("2");
        updated.StudentNo.ShouldBe(student.StudentNo); // 学籍辅号不随信息修改变化
        updated.HouseholdAddress.ShouldBe("河南省郑州市金水区某某路 1 号");
        updated.IsLeftBehindChild.ShouldBeTrue();
    }

    /// <summary>
    /// 随迁子女 / 留守儿童**类型码**必须能写进去。
    ///
    /// 这条用例守的是一个很隐蔽的缺口：`StudentDto`（详情）与实体
    /// `StuStudent` 都有 `MigrantTypeCode` / `LeftBehindTypeCode`，
    /// 但 `CreateUpdateStudentDto` **漏了这两个字段** ——
    /// 于是新增/修改接口根本传不进来，字段恒为 null，
    /// 而"详情里字段存在、界面上也有这一栏"让人以为功能是好的。
    /// 只在导出/详情里回读，永远读不到值。
    /// </summary>
    [Fact]
    public async Task 随迁与留守儿童类型码能写入并回读()
    {
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id, "类型码");

        var updated = await _studentAppService.UpdateAsync(student.Id, new CreateUpdateStudentDto
        {
            Name = student.Name,
            GenderCode = student.GenderCode,
            BirthDate = student.BirthDate,
            IdCardTypeCode = "1",
            // 修改必须重新传明文身份证：详情接口只回脱敏值（加密存储）
            IdCardNumber = NewUniqueIdCard(),
            SchoolOrgId = school.Id,
            AcademicYear = Year,
            IsMigrantChild = true,
            MigrantTypeCode = "01",
            IsLeftBehindChild = true,
            LeftBehindTypeCode = "02"
        });

        updated.IsMigrantChild.ShouldBeTrue();
        updated.MigrantTypeCode.ShouldBe("01");
        updated.IsLeftBehindChild.ShouldBeTrue();
        updated.LeftBehindTypeCode.ShouldBe("02");

        // 回读一次，确认落库而不只是返回值上带了
        var reloaded = await _studentAppService.GetAsync(student.Id);
        reloaded.MigrantTypeCode.ShouldBe("01");
        reloaded.LeftBehindTypeCode.ShouldBe("02");
    }

    [Fact]
    public async Task 删除学生后可恢复的软删除语义()
    {
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id, "待删除");

        await _studentAppService.DeleteAsync(student.Id);

        // 软删除后查不到
        await Should.ThrowAsync<Exception>(async () => await _studentAppService.GetAsync(student.Id));

        var list = await _studentAppService.GetListAsync(new StudentQueryDto
        {
            SchoolOrgId = school.Id,
            MaxResultCount = 100
        });

        list.Items.ShouldNotContain(i => i.Id == student.Id);
    }

    [Fact]
    public async Task 非法的姓名被拒绝()
    {
        var school = await CreateSchoolAsync();

        await Should.ThrowAsync<BusinessException>(async () =>
            await CreateStudentAsync(school.Id, "张三123"));
    }
}
