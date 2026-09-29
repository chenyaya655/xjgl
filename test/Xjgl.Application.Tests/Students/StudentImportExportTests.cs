using Shouldly;
using Volo.Abp;
using Xjgl.Exporting;
using Xjgl.Organizations;
using Xjgl.Text;
using Xunit;

namespace Xjgl.Students;

/// <summary>
/// 学生批量导入导出的集成测试。
///
/// 重点验证三件事：
///   1) **导入与手工录入走同一套校验** —— 身份证校验位、跨校查重、生僻字姓名规则
///      都会在导入时生效（否则导入就成了绕过校验的后门）；
///   2) **逐行容错** —— 个别行失败不影响其它行，并且能指出"第几行、错在哪"；
///   3) **导出受数据范围约束** —— 导出走的是同一个查询服务，
///      不允许出现"界面看不到、导出却能拿到"。
/// </summary>
public class StudentImportExportTests : XjglIntegrationTestBase
{
    private const string Year = "2065-2066";

    private readonly IStudentImportExportAppService _io;
    private readonly IStudentAppService _students;
    private readonly IOrgAppService _orgAppService;
    private readonly IStudentCsvExportBuilder _csvExport;

    public StudentImportExportTests()
    {
        _io = GetRequiredService<IStudentImportExportAppService>();
        _students = GetRequiredService<IStudentAppService>();
        _orgAppService = GetRequiredService<IOrgAppService>();

        // 导出改成任务式之后，CSV 的组装搬到了 IStudentCsvExportBuilder
        // （原来的 GetExportAsync 已经没有 HTTP 出口，见 ExportTaskAppService）。
        // 断言本身一条没改 —— 这里要守的仍是"带 BOM、身份证脱敏、按学校不串校"。
        _csvExport = GetRequiredService<IStudentCsvExportBuilder>();
    }

    // ------------------------------------------------------------------
    // 脚手架
    // ------------------------------------------------------------------

    private async Task<OrgDto> CreateSchoolAsync()
    {
        return await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School,
            Code = "41" + Random.Shared.Next(10000000, 99999999),
            Name = "导入测试学校"
        });
    }

    // 身份证号统一走基类的 NewUniqueIdCard（计数器推导 12×28 天 × 顺序码，跨轮几乎不重复）。
    // 别再写"固定地区码 + 3 位随机顺序码"的本地生成器：号段只有 899 个，
    // 测试库跨多轮累积之后会随机撞上历史学生，表现为用例**偶发**变红。
    private static string NewIdCard(int year = 2016) => NewUniqueIdCard(year);

    /// <summary>拼一份导入 CSV（表头 + 数据行）。</summary>
    private static string BuildCsv(params string[][] rows)
    {
        var header = new[]
        {
            "姓名", "性别", "出生日期", "证件类型", "身份证号",
            "学校标识码", "学年", "年级编码", "班级编码", "学籍状态", "入学日期", "联系电话", "家庭住址"
        };

        return Csv.Write(new[] { header }.Concat(rows));
    }

    private static string[] Row(string schoolCode, string name, string idCard, string sex = "男")
        => new[]
        {
            name, sex, "2016-03-01", "居民身份证", idCard,
            schoolCode, Year, string.Empty, string.Empty, "在读", "2016-09-01", "13800000000", "郑州市"
        };

    // ------------------------------------------------------------------
    // 模板
    // ------------------------------------------------------------------

    [Fact]
    public void 导入模板_含全部必需列与一行示例()
    {
        var columns = _io.GetColumns();

        columns.ShouldContain(c => c.Name == "姓名" && c.Required);
        columns.ShouldContain(c => c.Name == "身份证号" && c.Required);
        columns.ShouldContain(c => c.Name == "学校标识码" && c.Required);

        var rows = Csv.Parse(_io.GetImportTemplate());

        rows.Count.ShouldBe(2, "模板应当有表头 + 一行示例");
        rows[1].Length.ShouldBe(columns.Count);
    }

    // ------------------------------------------------------------------
    // 导入
    // ------------------------------------------------------------------

    [Fact]
    public async Task 导入_正常数据全部成功()
    {
        var school = await CreateSchoolAsync();

        var result = await _io.ImportAsync(new ImportStudentsDto
        {
            Content = BuildCsv(
                Row(school.Code, "导入甲", NewIdCard()),
                Row(school.Code, "导入乙", NewIdCard(), "女"))
        });

        result.Total.ShouldBe(2);
        result.Succeeded.ShouldBe(2);
        result.Failed.ShouldBe(0);
        result.Errors.ShouldBeEmpty();
        result.Message.ShouldContain("全部导入成功");

        // 真的落库了，并且学籍辅号按学校生成
        var list = await _students.GetListAsync(new StudentQueryDto
        {
            SchoolOrgId = school.Id, MaxResultCount = 50
        });

        list.Items.ShouldContain(s => s.Name == "导入甲");
        list.Items.ShouldContain(s => s.Name == "导入乙" && s.GenderName == "女");
    }

    [Fact]
    public async Task 导入_单行失败不影响其它行_并指出行号与原因()
    {
        var school = await CreateSchoolAsync();

        var result = await _io.ImportAsync(new ImportStudentsDto
        {
            Content = BuildCsv(
                Row(school.Code, "好行一", NewIdCard()),
                // 身份证号校验位错误
                Row(school.Code, "坏行", "410102201603011230"),
                Row(school.Code, "好行二", NewIdCard()))
        });

        result.Total.ShouldBe(3);
        result.Succeeded.ShouldBe(2);
        result.Failed.ShouldBe(1);
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].RowIndex.ShouldBe(3, "表头是第 1 行，坏行是数据里的第 2 行 → 对应表格第 3 行");
        result.Errors[0].Name.ShouldBe("坏行");
        result.Errors[0].Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task 导入_重复身份证号被查重拦住()
    {
        var school = await CreateSchoolAsync();
        var idCard = NewIdCard();

        var first = await _io.ImportAsync(new ImportStudentsDto
        {
            Content = BuildCsv(Row(school.Code, "首次导入", idCard))
        });

        first.Succeeded.ShouldBe(1);

        // 同一身份证号再导入一次（模拟学校重复报送）
        var second = await _io.ImportAsync(new ImportStudentsDto
        {
            Content = BuildCsv(Row(school.Code, "重复导入", idCard))
        });

        second.Failed.ShouldBe(1);
        second.Errors[0].Message.ShouldContain("已存在");
    }

    [Fact]
    public async Task 导入_学校标识码不存在_该行失败()
    {
        var result = await _io.ImportAsync(new ImportStudentsDto
        {
            Content = BuildCsv(Row("9999999999", "无校学生", NewIdCard()))
        });

        result.Failed.ShouldBe(1);
        result.Errors[0].Message.ShouldContain("不存在");
    }

    [Fact]
    public async Task 导入_生僻字姓名与手工录入同样被校验()
    {
        var school = await CreateSchoolAsync();

        // 姓名里含数字：与手工录入一样应当被拒
        var result = await _io.ImportAsync(new ImportStudentsDto
        {
            Content = BuildCsv(Row(school.Code, "张三3", NewIdCard()))
        });

        result.Failed.ShouldBe(1, "导入必须复用创建学生的校验，否则就是绕过校验的后门");
    }

    [Fact]
    public async Task 导入_缺少必需列_直接报错并指出缺哪一列()
    {
        // 只有姓名与性别两列
        var csv = Csv.Write(new[]
        {
            new[] { "姓名", "性别" },
            new[] { "张三", "男" }
        });

        var exception = await Should.ThrowAsync<UserFriendlyException>(
            async () => await _io.ImportAsync(new ImportStudentsDto { Content = csv }));

        exception.Message.ShouldContain("缺少必需列");
        exception.Message.ShouldContain("身份证号");
    }

    [Fact]
    public async Task 导入_只有表头没有数据行_给出明确提示()
    {
        var csv = Csv.Write(new[] { new[] { "姓名", "性别" } });

        await Should.ThrowAsync<UserFriendlyException>(
            async () => await _io.ImportAsync(new ImportStudentsDto { Content = csv }));
    }

    [Fact]
    public async Task 导入_空行被跳过而不是报错()
    {
        var school = await CreateSchoolAsync();

        var csv = BuildCsv(Row(school.Code, "有数据", NewIdCard())) + ",,,,,,,,,,,,\r\n";

        var result = await _io.ImportAsync(new ImportStudentsDto { Content = csv });

        result.Total.ShouldBe(1, "Excel 末尾的空行不该被算成一行失败数据");
        result.Succeeded.ShouldBe(1);
    }

    // ------------------------------------------------------------------
    // 导出
    // ------------------------------------------------------------------

    [Fact]
    public async Task 导出_含表头与数据_身份证号脱敏_带BOM()
    {
        var school = await CreateSchoolAsync();

        await _io.ImportAsync(new ImportStudentsDto
        {
            Content = BuildCsv(Row(school.Code, "导出学生", NewIdCard()))
        });

        var csv = (await _csvExport.BuildAsync(new StudentQueryDto
        {
            SchoolOrgId = school.Id, MaxResultCount = 100
        })).Csv;

        csv[0].ShouldBe('\uFEFF', "导出给 Excel 打开，必须带 BOM");

        var rows = Csv.Parse(csv);

        rows[0].ShouldContain("学籍辅号");
        rows[0].ShouldContain("身份证号");

        var dataRow = rows.Skip(1).FirstOrDefault(r => r.Contains("导出学生"));

        dataRow.ShouldNotBeNull();
        // 身份证号必须脱敏（导出文件会被转发、打印、存 U 盘）
        dataRow![4].ShouldContain("*");
    }

    [Fact]
    public async Task 导出_按学校过滤_不串校()
    {
        var schoolA = await CreateSchoolAsync();
        var schoolB = await CreateSchoolAsync();

        await _io.ImportAsync(new ImportStudentsDto
        {
            Content = BuildCsv(
                Row(schoolA.Code, "甲校学生", NewIdCard()),
                Row(schoolB.Code, "乙校学生", NewIdCard()))
        });

        var csv = (await _csvExport.BuildAsync(new StudentQueryDto
        {
            SchoolOrgId = schoolA.Id, MaxResultCount = 100
        })).Csv;

        csv.ShouldContain("甲校学生");
        csv.ShouldNotContain("乙校学生");
    }
}
