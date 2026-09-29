using Shouldly;
using Volo.Abp;
using Xjgl.Codes;
using Xjgl.Text;
using Xunit;

namespace Xjgl.Codes;

/// <summary>
/// 微代码（码表 / 码项）导入导出的集成测试（ROADMAP 第 17 条）。
///
/// 【重点在"搬运"这个用途上】
/// 码表导出→导入的真实用途是**测试环境搬到生产**。因此两条性质最关键：
///   1. 导出的列与导入模板一致（否则这个来回要手工对列名）；
///   2. 导入**不覆盖已有类别**（否则搬一次就把生产上调好的类别名改回测试环境的）。
/// </summary>
public class CodeImportExportTests : XjglIntegrationTestBase
{
    private readonly ICodeImportExportAppService _import;
    private readonly ICodeCsvExportBuilder _export;
    private readonly ICodeAppService _codes;

    public CodeImportExportTests()
    {
        _import = GetRequiredService<ICodeImportExportAppService>();
        _export = GetRequiredService<ICodeCsvExportBuilder>();
        _codes = GetRequiredService<ICodeAppService>();
    }

    private static string Prefix() => "T" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static string Csv(params string[][] rows)
    {
        var all = new List<IEnumerable<string?>>
        {
            new[] { "类别编码", "类别名称", "码值", "码名称", "排序", "是否启用" }
        };

        all.AddRange(rows.Select(r => r.AsEnumerable()));

        return Text.Csv.Write(all);
    }

    private Task<ImportCodesResultDto> ImportAsync(string content)
        => _import.ImportAsync(new ImportCodesDto { Content = content });

    // ==================================================================
    // 模板
    // ==================================================================

    [Fact]
    public void 模板含表头_且示例展示同类别多行()
    {
        var rows = Text.Csv.Parse(_import.GetImportTemplate());
        var header = Text.Csv.BuildHeaderIndex(rows[0]);

        header.Keys.ShouldBe(_import.GetColumns().Select(c => c.Name), ignoreOrder: false);

        // 至少两行示例且**类别编码相同**：这份文件唯一需要理解的约定就是
        // "同一个类别的多行要重复写类别编码"，只给一行示例用户看不出来
        rows.Count.ShouldBeGreaterThanOrEqualTo(3);
        Text.Csv.Cell(rows[1], header, "类别编码")
            .ShouldBe(Text.Csv.Cell(rows[2], header, "类别编码"));
    }

    // ==================================================================
    // 导入
    // ==================================================================

    [Fact]
    public async Task 导入新类别与多条码项_类别只建一次()
    {
        var c = Prefix() + "_CAT";

        var result = await ImportAsync(Csv(
            new[] { c, "导入测试类别", "01", "甲", "10", "是" },
            new[] { c, "导入测试类别", "02", "乙", "20", "是" },
            new[] { c, "导入测试类别", "03", "丙", "30", "否" }));

        result.Total.ShouldBe(3);
        result.Succeeded.ShouldBe(3);
        result.Failed.ShouldBe(0, string.Join("；", result.Errors.Select(e => $"第{e.RowIndex}行 {e.Message}")));
        result.CreatedCategories.ShouldBe(1, "同一个类别的三行只应建一次类别");

        var items = await _codes.GetItemsByCodeAsync(c, onlyEnabled: false);

        items.Count.ShouldBe(3);
        items.Single(i => i.Code == "03").Name.ShouldBe("丙");
    }

    /// <summary>
    /// 已有类别**不重建、也不改名**。
    ///
    /// 这条是"搬运"场景的安全阀：文件里带的类别名可能是旧环境的名字，
    /// 导入时若顺手改名，生产上引用它的界面文案会一起变 —— 而这是没人预期的副作用。
    ///
    /// ⚠ 用**自己建的**类别来验，不要拿内置类别（如 SEX）当靶子：
    /// 内置类别的码项条数是其它用例的断言前提（`CodeAppServiceTests` 断言 SEX 有 3 条），
    /// 往里塞一条会把那些用例弄红 —— 而且是在**测试库累积**的前提下永久弄红。
    /// （本轮真的这么踩过一次。）
    /// </summary>
    [Fact]
    public async Task 已存在的类别不会被改名()
    {
        var c = Prefix() + "_KEEP";

        // 第一次导入：建类别 + 一条码项
        var first = await ImportAsync(Csv(
            new[] { c, "原始名称", "01", "甲", "", "是" }));

        first.CreatedCategories.ShouldBe(1);

        var created = (await _codes.GetCategoriesAsync(onlyEnabled: false)).Single(x => x.Code == c);

        // 第二次导入：类别已存在，文件里换了个名字，且新增一条码项
        var second = await ImportAsync(Csv(
            new[] { c, "文件里乱写的名字", "02", "乙", "", "是" }));

        second.Succeeded.ShouldBe(1);
        second.CreatedCategories.ShouldBe(0, "类别已存在，不该新建");

        var after = (await _codes.GetCategoriesAsync(onlyEnabled: false)).Single(x => x.Code == c);

        after.Name.ShouldBe("原始名称", "已存在类别的名称不能被导入文件改掉");
        after.Id.ShouldBe(created.Id, "类别没有被重建（重建会让引用它的地方全部失联）");

        (await _codes.GetItemsByCodeAsync(c, onlyEnabled: false)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task 逐行容错_同类别内码值重复只失败那一行()
    {
        var c = Prefix() + "_CAT";

        var result = await ImportAsync(Csv(
            new[] { c, "容错类别", "01", "甲", "", "" },
            new[] { c, "容错类别", "01", "重复的甲", "", "" },
            new[] { c, "容错类别", "02", "乙", "", "" }));

        result.Total.ShouldBe(3);
        result.Succeeded.ShouldBe(2);
        result.Failed.ShouldBe(1);
        result.Errors.Single().RowIndex.ShouldBe(3, "行号按 Excel 行号报（表头第 1 行）");
    }

    [Fact]
    public async Task 缺少必需列_整体拒绝并说明缺哪列()
    {
        var content = Text.Csv.Write(new[]
        {
            new[] { "类别编码", "码值" },
            new[] { "X", "01" }
        });

        var ex = await Should.ThrowAsync<UserFriendlyException>(async () => await ImportAsync(content));

        ex.Message.ShouldContain("类别名称");
        ex.Message.ShouldContain("码名称");
    }

    [Fact]
    public async Task 空码值或空名称_该行失败并给出可照着改的提示()
    {
        var c = Prefix() + "_CAT";

        var result = await ImportAsync(Csv(
            new[] { c, "空值类别", "", "没有码值", "", "" }));

        result.Failed.ShouldBe(1);
        result.Errors.Single().Message.ShouldContain("码值与码名称都不能为空");
    }

    // ==================================================================
    // 导出
    // ==================================================================

    [Fact]
    public async Task 导出的列与导入模板一致_且值可被重新解析()
    {
        var c = Prefix() + "_CAT";

        await ImportAsync(Csv(
            new[] { c, "导出测试类别", "01", "甲", "5", "是" }));

        var result = await _export.BuildAsync(new CodeExportQueryDto { CategoryCode = c });

        var rows = Text.Csv.Parse(result.Csv);
        var header = Text.Csv.BuildHeaderIndex(rows[0]);

        header.Keys.ShouldBe(_import.GetColumns().Select(x => x.Name), ignoreOrder: false);

        var row = rows.Skip(1).Single();

        Text.Csv.Cell(row, header, "类别编码").ShouldBe(c);
        Text.Csv.Cell(row, header, "码值").ShouldBe("01");
        Text.Csv.Cell(row, header, "排序").ShouldBe("5");
        Text.Csv.Cell(row, header, "是否启用").ShouldBe("是");
        result.ItemCount.ShouldBe(1);
        result.CategoryCount.ShouldBe(1);
    }

    /// <summary>
    /// 关键字命中**类别**时，该类别下的全部码项都要带上。
    ///
    /// 这条是回归用例：最初的实现把"类别命中"与"码项命中"写成了 AND，
    /// 于是"搜一个类别名 → 出一个类别、零条码项" ——
    /// 而用户搜类别名的意思恰恰是"我要这个类别里的东西"。
    /// </summary>
    [Fact]
    public async Task 按类别名搜索_带上该类别全部码项()
    {
        var c = Prefix() + "_CAT";
        var uniqueName = "独特类别名" + Guid.NewGuid().ToString("N")[..6];

        await ImportAsync(Csv(
            new[] { c, uniqueName, "01", "甲", "", "" },
            new[] { c, uniqueName, "02", "乙", "", "" }));

        var result = await _export.BuildAsync(new CodeExportQueryDto { Keyword = uniqueName });

        var rows = Text.Csv.Parse(result.Csv);

        // 表头 + 该类别下的两条（码项自身的码值/名称都与关键字无关，也必须被带上）
        rows.Count.ShouldBe(3);
        result.ItemCount.ShouldBe(2);
    }

    /// <summary>按码值搜索时，只有命中的那一行（用户找的是"某条码"）。</summary>
    [Fact]
    public async Task 按码值搜索_只出命中的那一行()
    {
        var c = Prefix() + "_CAT";
        var uniqueCode = "U" + Guid.NewGuid().ToString("N")[..5];

        await ImportAsync(Csv(
            new[] { c, "码值搜索类别", "01", "甲", "", "" },
            new[] { c, "码值搜索类别", uniqueCode, "独特码值", "", "" }));

        var result = await _export.BuildAsync(new CodeExportQueryDto { CategoryCode = c, Keyword = uniqueCode });

        var rows = Text.Csv.Parse(result.Csv);
        var header = Text.Csv.BuildHeaderIndex(rows[0]);

        rows.Count.ShouldBe(2);
        Text.Csv.Cell(rows[1], header, "码值").ShouldBe(uniqueCode);
    }

    /// <summary>导出默认带上停用的码项：搬运/备份时漏掉它们只会表现为"某处选不到某个选项"。</summary>
    [Fact]
    public async Task 导出默认包含停用的码项()
    {
        var c = Prefix() + "_CAT";

        await ImportAsync(Csv(
            new[] { c, "含停用类别", "01", "启用项", "", "是" },
            new[] { c, "含停用类别", "02", "停用项", "", "否" }));

        var all = await _export.BuildAsync(new CodeExportQueryDto { CategoryCode = c });
        all.ItemCount.ShouldBe(2);

        var onlyEnabled = await _export.BuildAsync(new CodeExportQueryDto { CategoryCode = c, OnlyEnabled = true });
        onlyEnabled.ItemCount.ShouldBe(1);
    }
}
