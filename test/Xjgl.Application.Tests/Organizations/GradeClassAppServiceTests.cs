using Shouldly;
using Volo.Abp;
using Xunit;

namespace Xjgl.Organizations;

/// <summary>
/// 年级班级管理的集成测试。
///
/// 重点验证「学年维度建模」与「批量生成」这两件事：
///   - 年级班级挂在 学校 + 学年 下，不进组织树，因此升级只是数据操作；
///   - 批量生成必须幂等（重复点击不会产生重复班级），班号要补零、名称要能按模板渲染。
/// </summary>
public class GradeClassAppServiceTests : XjglIntegrationTestBase
{
    private const string Year = "2031-2032";

    private readonly IGradeClassAppService _gradeClassAppService;
    private readonly IOrgAppService _orgAppService;

    public GradeClassAppServiceTests()
    {
        _gradeClassAppService = GetRequiredService<IGradeClassAppService>();
        _orgAppService = GetRequiredService<IOrgAppService>();
    }

    private async Task<OrgDto> CreateSchoolAsync()
    {
        return await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School,
            Code = "9" + Guid.NewGuid().ToString("N")[..8],
            Name = "年级班级测试学校"
        });
    }

    private async Task<OrgDto> CreateProvinceAsync()
    {
        return await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.Province,
            Code = "9" + Guid.NewGuid().ToString("N")[..8],
            Name = "年级班级测试省厅"
        });
    }

    // ------------------------------------------------------------------
    // 年级
    // ------------------------------------------------------------------

    [Fact]
    public async Task 新增年级_校验学校类型()
    {
        var school = await CreateSchoolAsync();

        var grade = await _gradeClassAppService.CreateGradeAsync(new CreateUpdateGradeDto
        {
            SchoolOrgId = school.Id,
            AcademicYear = Year,
            GradeCode = "01",
            GradeName = "一年级"
        });

        grade.SchoolOrgId.ShouldBe(school.Id);
        grade.AcademicYear.ShouldBe(Year);
        grade.ClassCount.ShouldBe(0);
    }

    [Fact]
    public async Task 年级只能挂在学校的组织下()
    {
        var province = await CreateProvinceAsync();

        await Should.ThrowAsync<BusinessException>(async () =>
            await _gradeClassAppService.CreateGradeAsync(new CreateUpdateGradeDto
            {
                SchoolOrgId = province.Id,
                AcademicYear = Year,
                GradeCode = "01",
                GradeName = "一年级"
            }));
    }

    [Fact]
    public async Task 同学年同年级码重复被拒绝()
    {
        var school = await CreateSchoolAsync();

        await _gradeClassAppService.CreateGradeAsync(new CreateUpdateGradeDto
        {
            SchoolOrgId = school.Id, AcademicYear = Year, GradeCode = "01", GradeName = "一年级"
        });

        await Should.ThrowAsync<BusinessException>(async () =>
            await _gradeClassAppService.CreateGradeAsync(new CreateUpdateGradeDto
            {
                SchoolOrgId = school.Id, AcademicYear = Year, GradeCode = "01", GradeName = "重复的一年级"
            }));
    }

    // ------------------------------------------------------------------
    // 批量生成年级
    // ------------------------------------------------------------------

    [Fact]
    public async Task 按学校类型推断默认年级范围_小学生成六个年级()
    {
        var school = await CreateSchoolAsync();
        var year = "2032-2033";

        var result = await _gradeClassAppService.BatchGenerateGradesAsync(new BatchGenerateGradesDto
        {
            SchoolOrgId = school.Id,
            AcademicYear = year,
            SchoolTypeCode = "1" // 小学
        });

        result.Created.ShouldBe(6);
        result.Items.Select(i => i.GradeCode).ShouldBe(new[] { "01", "02", "03", "04", "05", "06" });

        // 年级名称取自微代码 GRADE
        result.Items.First().GradeName.ShouldBe("一年级");
    }

    [Fact]
    public async Task 按学校类型推断默认年级范围_普通高中生成三个年级()
    {
        var school = await CreateSchoolAsync();

        var result = await _gradeClassAppService.BatchGenerateGradesAsync(new BatchGenerateGradesDto
        {
            SchoolOrgId = school.Id,
            AcademicYear = "2033-2034",
            SchoolTypeCode = "5" // 普通高中
        });

        result.Created.ShouldBe(3);
        result.Items.Select(i => i.GradeCode).ShouldBe(new[] { "10", "11", "12" });
    }

    [Fact]
    public async Task 批量生成年级是幂等的()
    {
        var school = await CreateSchoolAsync();
        var year = "2034-2035";

        var first = await _gradeClassAppService.BatchGenerateGradesAsync(new BatchGenerateGradesDto
        {
            SchoolOrgId = school.Id, AcademicYear = year, SchoolTypeCode = "2"
        });

        first.Created.ShouldBe(3);

        var second = await _gradeClassAppService.BatchGenerateGradesAsync(new BatchGenerateGradesDto
        {
            SchoolOrgId = school.Id, AcademicYear = year, SchoolTypeCode = "2"
        });

        second.Created.ShouldBe(0);
        second.Skipped.ShouldBe(3);

        var grades = await _gradeClassAppService.GetGradesAsync(school.Id, year);
        grades.Count.ShouldBe(3);
    }

    [Fact]
    public async Task 只传起始年时自动补全学年()
    {
        var school = await CreateSchoolAsync();

        var grade = await _gradeClassAppService.CreateGradeAsync(new CreateUpdateGradeDto
        {
            SchoolOrgId = school.Id,
            AcademicYear = "2035",
            GradeCode = "01",
            GradeName = "一年级"
        });

        grade.AcademicYear.ShouldBe("2035-2036");
    }

    // ------------------------------------------------------------------
    // 班级
    // ------------------------------------------------------------------

    [Fact]
    public async Task 批量生成班级_班号补零_名称按模板渲染()
    {
        var school = await CreateSchoolAsync();
        var year = "2036-2037";

        var grade = (await _gradeClassAppService.BatchGenerateGradesAsync(new BatchGenerateGradesDto
        {
            SchoolOrgId = school.Id, AcademicYear = year, GradeCodes = new List<string> { "01" }
        })).Items.Single();

        var result = await _gradeClassAppService.BatchGenerateClassesAsync(new BatchGenerateClassesDto
        {
            GradeId = grade.Id,
            ClassCount = 5
        });

        result.Created.ShouldBe(5);
        result.Items.Select(c => c.ClassNo).ShouldBe(new[] { "01", "02", "03", "04", "05" });
        result.Items.First().ClassName.ShouldBe("一年级1班");
        result.Items.Last().ClassName.ShouldBe("一年级5班");
        result.Items.ShouldAllBe(c => c.AcademicYear == year);
        result.Items.ShouldAllBe(c => c.SchoolOrgId == school.Id);
    }

    [Fact]
    public async Task 批量生成班级_支持自定义名称模板与起始班号()
    {
        var school = await CreateSchoolAsync();

        var grade = (await _gradeClassAppService.BatchGenerateGradesAsync(new BatchGenerateGradesDto
        {
            SchoolOrgId = school.Id, AcademicYear = "2037-2038", GradeCodes = new List<string> { "07" }
        })).Items.Single();

        var result = await _gradeClassAppService.BatchGenerateClassesAsync(new BatchGenerateClassesDto
        {
            GradeId = grade.Id,
            ClassCount = 2,
            StartNo = 3,
            NamePattern = "{grade}({no})班"
        });

        result.Items.Select(c => c.ClassNo).ShouldBe(new[] { "03", "04" });
        result.Items.First().ClassName.ShouldBe("七年级(3)班");
    }

    [Fact]
    public async Task 批量生成班级是幂等的_不会产生重复班号()
    {
        var school = await CreateSchoolAsync();

        var grade = (await _gradeClassAppService.BatchGenerateGradesAsync(new BatchGenerateGradesDto
        {
            SchoolOrgId = school.Id, AcademicYear = "2038-2039", GradeCodes = new List<string> { "01" }
        })).Items.Single();

        var first = await _gradeClassAppService.BatchGenerateClassesAsync(new BatchGenerateClassesDto
        {
            GradeId = grade.Id, ClassCount = 3
        });
        first.Created.ShouldBe(3);

        // 再次生成 5 个：前 3 个应被跳过，后 2 个新建
        var second = await _gradeClassAppService.BatchGenerateClassesAsync(new BatchGenerateClassesDto
        {
            GradeId = grade.Id, ClassCount = 5
        });

        second.Created.ShouldBe(2);
        second.Skipped.ShouldBe(3);

        var classes = await _gradeClassAppService.GetClassesAsync(school.Id, "2038-2039", grade.Id);
        classes.Count.ShouldBe(5);
    }

    [Fact]
    public async Task 年级下还有班级时不允许删除()
    {
        var school = await CreateSchoolAsync();

        var grade = (await _gradeClassAppService.BatchGenerateGradesAsync(new BatchGenerateGradesDto
        {
            SchoolOrgId = school.Id, AcademicYear = "2039-2040", GradeCodes = new List<string> { "01" }
        })).Items.Single();

        await _gradeClassAppService.BatchGenerateClassesAsync(new BatchGenerateClassesDto
        {
            GradeId = grade.Id, ClassCount = 2
        });

        await Should.ThrowAsync<BusinessException>(async () =>
            await _gradeClassAppService.DeleteGradeAsync(grade.Id));
    }

    [Fact]
    public async Task 已有班级的年级不允许改学年或年级码()
    {
        var school = await CreateSchoolAsync();

        var grade = (await _gradeClassAppService.BatchGenerateGradesAsync(new BatchGenerateGradesDto
        {
            SchoolOrgId = school.Id, AcademicYear = "2040-2041", GradeCodes = new List<string> { "01" }
        })).Items.Single();

        await _gradeClassAppService.BatchGenerateClassesAsync(new BatchGenerateClassesDto
        {
            GradeId = grade.Id, ClassCount = 1
        });

        await Should.ThrowAsync<BusinessException>(async () =>
            await _gradeClassAppService.UpdateGradeAsync(grade.Id, new CreateUpdateGradeDto
            {
                SchoolOrgId = school.Id,
                AcademicYear = "2041-2042",
                GradeCode = "02",
                GradeName = "二年级"
            }));
    }

    [Fact]
    public async Task 按学年与年级查询班级()
    {
        var school = await CreateSchoolAsync();
        const string year = "2042-2043";

        var grades = await _gradeClassAppService.BatchGenerateGradesAsync(new BatchGenerateGradesDto
        {
            SchoolOrgId = school.Id, AcademicYear = year, GradeCodes = new List<string> { "01", "02" }
        });

        foreach (var grade in grades.Items)
        {
            await _gradeClassAppService.BatchGenerateClassesAsync(new BatchGenerateClassesDto
            {
                GradeId = grade.Id, ClassCount = 2
            });
        }

        var allOfYear = await _gradeClassAppService.GetClassesAsync(school.Id, year);
        allOfYear.Count.ShouldBe(4);

        var onlyGrade1 = await _gradeClassAppService.GetClassesAsync(school.Id, year, grades.Items[0].Id);
        onlyGrade1.Count.ShouldBe(2);
        onlyGrade1.ShouldAllBe(c => c.GradeId == grades.Items[0].Id);
        onlyGrade1.ShouldAllBe(c => !string.IsNullOrWhiteSpace(c.GradeName));
    }

    [Fact]
    public async Task 年级的班级数统计正确()
    {
        var school = await CreateSchoolAsync();
        const string year = "2043-2044";

        var grade = (await _gradeClassAppService.BatchGenerateGradesAsync(new BatchGenerateGradesDto
        {
            SchoolOrgId = school.Id, AcademicYear = year, GradeCodes = new List<string> { "01" }
        })).Items.Single();

        await _gradeClassAppService.BatchGenerateClassesAsync(new BatchGenerateClassesDto
        {
            GradeId = grade.Id, ClassCount = 4
        });

        var grades = await _gradeClassAppService.GetGradesAsync(school.Id, year);
        grades.Single().ClassCount.ShouldBe(4);
    }
}
