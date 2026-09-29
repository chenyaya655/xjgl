using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Volo.Abp;
using Xjgl.Exporting;
using Xjgl.Organizations;
using Xjgl.Text;
using Xunit;

namespace Xjgl.Organizations;

/// <summary>
/// 组织（学校名录）导入导出的集成测试（ROADMAP 第 17 条）。
///
/// 【为什么这组用例值得写】
/// 导入是**部署前置条件**：上万所学校只能靠文件灌进去。
/// 而导入最典型的失败不是"报错"，是**静默建出一棵错的树** ——
/// 层级挂错、上级编码写错、行序颠倒。这些在界面上都看不出来，
/// 要等数据范围算错、学籍辅号生成失败的时候才暴露。
///
/// 因此本组用例重点不在"导入成功"，而在**失败时提示得对不对**：
/// 业务人员拿到的必须是一句"该填哪一级"的话，而不是一个错误码。
/// </summary>
public class OrganizationImportExportTests : XjglIntegrationTestBase
{
    private readonly IOrganizationImportExportAppService _import;
    private readonly IOrganizationCsvExportBuilder _export;
    private readonly IOrgAppService _orgs;

    public OrganizationImportExportTests()
    {
        _import = GetRequiredService<IOrganizationImportExportAppService>();
        _export = GetRequiredService<IOrganizationCsvExportBuilder>();
        _orgs = GetRequiredService<IOrgAppService>();
    }

    // ------------------------------------------------------------------
    // 脚手架
    // ------------------------------------------------------------------

    /// <summary>每个用例用一段独立的编码前缀，避免与累积的测试数据撞号。</summary>
    private static string Prefix() => "T" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    /// <summary>把若干行拼成 CSV（首行表头）。</summary>
    private static string Csv(params string[][] rows)
    {
        var all = new List<IEnumerable<string?>>
        {
            new[] { "组织类型", "组织编码", "组织名称", "上级组织编码", "简称", "排序", "是否启用" }
        };

        all.AddRange(rows.Select(r => r.AsEnumerable()));

        return Text.Csv.Write(all);
    }

    private async Task<ImportOrganizationsResultDto> ImportAsync(string content)
        => await _import.ImportAsync(new ImportOrganizationsDto { Content = content });

    /// <summary>
    /// 按"本次运行前缀 + 名称"取一个组织。
    ///
    /// 不用 `OrgQueryDto.Keyword` 查：测试库是长期累积的，而 Keyword 是模糊匹配，
    /// 上一轮同名（但编码不同）的组织也会被命中，
    /// 于是 `Single` 抛「Sequence contains more than one matching element」——
    /// 而那个报错看起来像"数据重复"，实际是**测试自己没把数据隔离开**。
    /// 这里取全量再按名称精确匹配，前缀保证只命中本次运行的数据。
    /// </summary>
    private async Task<OrgDto> FindAsync(string prefix, string name)
    {
        var all = await _orgs.GetListAsync(new OrgQueryDto { OnlyEnabled = false });

        return all.Where(o => o.Code.StartsWith(prefix, StringComparison.Ordinal) && o.Name == name).Single();
    }

    // ==================================================================
    // 模板
    // ==================================================================

    [Fact]
    public void 模板含表头与覆盖四个层级的示例()
    {
        var rows = Text.Csv.Parse(_import.GetImportTemplate());

        // 表头 + 4 行示例
        rows.Count.ShouldBe(5);

        var header = Text.Csv.BuildHeaderIndex(rows[0]);
        header.Keys.ShouldContain("组织类型");
        header.Keys.ShouldContain("组织编码");
        header.Keys.ShouldContain("组织名称");
        header.Keys.ShouldContain("上级组织编码");

        // 四行示例分别覆盖四个层级：只给一行的话，用户看不出"上级组织编码"该填什么
        var types = rows.Skip(1).Select(r => Text.Csv.Cell(r, header, "组织类型")).ToList();
        types.ShouldBe(new[] { "省厅", "地市", "县区", "学校" });

        // 列说明必须把"学校编码是 10 位学校标识码"讲清楚 ——
        // 学籍辅号的前 10 位就是它，填错会让建学生直接失败，而报错指向学号生成
        _import.GetColumns().Single(c => c.Name == "组织编码")
            .Remark!.ShouldContain("学校标识码");
    }

    // ==================================================================
    // 导入：正常路径
    // ==================================================================

    [Fact]
    public async Task 导入四层组织_层级与物化路径都正确()
    {
        var p = Prefix();

        // 名称带上本次运行的唯一前缀：测试库是**长期累积**的，
        // 用固定名称的话上一轮建的"导入测试小学"还在，按名字 Single 会命中多条。
        var result = await ImportAsync(Csv(
            new[] { "省厅", p + "00000000", "导入省厅" + p, "", "", "10", "是" },
            new[] { "地市", p + "01000000", "导入市局" + p, p + "00000000", "", "10", "是" },
            new[] { "县区", p + "01020000", "导入县局" + p, p + "01000000", "", "10", "是" },
            new[] { "学校", p + "01020001", "导入小学" + p, p + "01020000", "", "10", "是" }));

        result.Total.ShouldBe(4);
        result.Succeeded.ShouldBe(4);
        result.Failed.ShouldBe(0, string.Join("；", result.Errors.Select(e => $"第{e.RowIndex}行 {e.Message}")));

        var school = await FindAsync(p, "导入小学" + p);

        school.TreeLevel.ShouldBe(4);
        school.OrgType.ShouldBe(OrgTypes.School);
        school.PathCode.ShouldBe($"{p}00000000.{p}01000000.{p}01020000.{p}01020001");
    }

    /// <summary>
    /// 行序**不需要**用户保证：文件里"学校"写在"县区"前面也要能导入。
    ///
    /// 这是刻意做的（服务端按层级排序后再处理）。不这么做的话，
    /// 按学校标识码排序导出的文件（学校在前）会整批失败 ——
    /// 而用户完全看不出"顺序"是个要求。
    /// </summary>
    [Fact]
    public async Task 导入时行序颠倒_也能成功()
    {
        var p = Prefix();

        var result = await ImportAsync(Csv(
            new[] { "学校", p + "01020001", "倒序小学" + p, p + "01020000", "", "", "" },
            new[] { "县区", p + "01020000", "倒序县局" + p, p + "01000000", "", "", "" },
            new[] { "地市", p + "01000000", "倒序市局" + p, p + "00000000", "", "", "" },
            new[] { "省厅", p + "00000000", "倒序省厅" + p, "", "", "", "" }));

        result.Succeeded.ShouldBe(4);
        result.Failed.ShouldBe(0, string.Join("；", result.Errors.Select(e => $"第{e.RowIndex}行 {e.Message}")));

        (await FindAsync(p, "倒序小学" + p)).TreeLevel.ShouldBe(4);
        (await FindAsync(p, "倒序省厅" + p)).TreeLevel.ShouldBe(1);
    }

    /// <summary>
    /// 组织类型的**常见写法都要认**：业务人员写"省"而不是"省厅"是高频情况，
    /// 只认一种写法的结果是"899 行全报未知类型"，而原因只是两个字不一样。
    /// </summary>
    [Fact]
    public async Task 组织类型接受中文别名与英文码值()
    {
        var p = Prefix();

        var result = await ImportAsync(Csv(
            new[] { "省", p + "00000000", "别名省厅" + p, "", "", "", "" },
            new[] { "City", p + "01000000", "别名市局" + p, p + "00000000", "", "", "" },
            new[] { "县级", p + "01020000", "别名县局" + p, p + "01000000", "", "", "" },
            new[] { "中小学", p + "01020001", "别名小学" + p, p + "01020000", "", "", "" }));

        result.Succeeded.ShouldBe(4);
        result.Failed.ShouldBe(0, string.Join("；", result.Errors.Select(e => $"第{e.RowIndex}行 {e.Message}")));

        (await FindAsync(p, "别名省厅" + p)).OrgType.ShouldBe(OrgTypes.Province);
        (await FindAsync(p, "别名小学" + p)).OrgType.ShouldBe(OrgTypes.School);
    }

    // ==================================================================
    // 导入：失败路径（重点在**提示能不能照着改**）
    // ==================================================================

    [Fact]
    public async Task 层级挂错_提示要说清该挂哪一级()
    {
        var p = Prefix();

        var result = await ImportAsync(Csv(
            new[] { "省厅", p + "00000000", "层级省厅", "", "", "", "" },
            // 学校直接挂在省厅下：四级树不允许跳级
            new[] { "学校", p + "01020001", "挂错的小学", p + "00000000", "", "", "" }));

        result.Succeeded.ShouldBe(1);
        result.Failed.ShouldBe(1);

        var error = result.Errors.Single();

        error.RowIndex.ShouldBe(3, "行号要按用户看到的 Excel 行号报（表头是第 1 行）");
        error.Code.ShouldBe(p + "01020001");
        error.Message.ShouldContain("县级教育行政部门", customMessage: "提示里必须说清该挂在哪一级，否则用户只能猜");
    }

    [Fact]
    public async Task 上级编码不存在_明确说找不到()
    {
        var p = Prefix();

        var result = await ImportAsync(Csv(
            new[] { "地市", p + "01000000", "孤儿市局", p + "99999999", "", "", "" }));

        result.Failed.ShouldBe(1);
        result.Errors.Single().Message.ShouldContain("找不到上级组织编码");
    }

    [Fact]
    public async Task 未知组织类型_提示可接受的写法()
    {
        var p = Prefix();

        var result = await ImportAsync(Csv(
            new[] { "教育局", p + "00000000", "类型不认", "", "", "", "" }));

        result.Failed.ShouldBe(1);

        var message = result.Errors.Single().Message;

        message.ShouldContain("教育局");
        message.ShouldContain("省厅", customMessage: "要告诉用户能填什么，而不是只说'不认识'");
    }

    /// <summary>
    /// 逐行容错：一行失败不能拖垮其它行。
    /// 这条最实际 —— 一份上万行的学校名录里错几行是常态，
    /// 因为几行就整批回滚，用户只能反复试。
    /// </summary>
    [Fact]
    public async Task 逐行容错_单行失败不影响其它行()
    {
        var p = Prefix();

        var result = await ImportAsync(Csv(
            new[] { "省厅", p + "00000000", "容错省厅", "", "", "", "" },
            new[] { "地市", p + "01000000", "容错市局", p + "00000000", "", "", "" },
            // 这一行编码重复
            new[] { "地市", p + "01000000", "重复编码市局", p + "00000000", "", "", "" },
            // 这一行类型不认
            new[] { "乡", p + "01030000", "类型不认乡", p + "01000000", "", "", "" },
            new[] { "县区", p + "01020000", "容错县局", p + "01000000", "", "", "" }));

        result.Total.ShouldBe(5);
        result.Succeeded.ShouldBe(3);
        result.Failed.ShouldBe(2);
        result.Errors.Select(e => e.RowIndex).ShouldBe(new[] { 4, 5 });

        // 消息要能直接展示给用户
        result.Message.ShouldContain("成功 3");
        result.Message.ShouldContain("失败 2");
    }

    [Fact]
    public async Task 缺少必需列_整体拒绝并说明缺哪列()
    {
        var content = Text.Csv.Write(new[]
        {
            new[] { "组织类型", "组织名称" },
            new[] { "省厅", "缺编码的省厅" }
        });

        var ex = await Should.ThrowAsync<UserFriendlyException>(async () => await ImportAsync(content));

        ex.Message.ShouldContain("组织编码");
    }

    // ==================================================================
    // 导出
    // ==================================================================

    /// <summary>
    /// 导出的列必须与导入模板**完全一致**，否则"导出 → 改一改 → 导入"
    /// 这个来回就得手工对列名，而列名对错只会表现为"缺少必需列"。
    /// </summary>
    [Fact]
    public async Task 导出的列与导入模板一致_且类型列能被重新解析()
    {
        var p = Prefix();

        await ImportAsync(Csv(
            new[] { "省厅", p + "00000000", "导出省厅" + p, "", "", "1", "是" },
            new[] { "地市", p + "01000000", "导出市局" + p, p + "00000000", "", "2", "否" }));

        var result = await _export.BuildAsync(new OrgQueryDto { Keyword = p, OnlyEnabled = false });

        var rows = Text.Csv.Parse(result.Csv);
        var header = Text.Csv.BuildHeaderIndex(rows[0]);

        // 与导入服务声明的列一一对应
        var importColumns = _import.GetColumns().Select(c => c.Name).ToList();
        header.Keys.ShouldBe(importColumns, ignoreOrder: false);

        var cityRow = rows.Skip(1).Single(r => Text.Csv.Cell(r, header, "组织名称") == "导出市局" + p);

        // 导出的"组织类型"必须能被导入解析器认出来（否则导出的文件自己导不回去）
        OrgTypes.TryParse(Text.Csv.Cell(cityRow, header, "组织类型"), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(OrgTypes.City);

        // 上级编码、启用状态都要如实写出
        Text.Csv.Cell(cityRow, header, "上级组织编码").ShouldBe(p + "00000000");
        Text.Csv.Cell(cityRow, header, "是否启用").ShouldBe("否");
    }

    /// <summary>
    /// admin（不受限）导出时，**每个非空的"上级组织编码"都必须能在文件里找到**。
    ///
    /// 这条断言的是"导出的文件是自洽的、可以整体搬去重建"——
    /// 上级指向文件外的组织时，那份文件导入后那棵树就散了
    /// （被当成根节点，层级全错，而且不报错）。
    /// </summary>
    [Fact]
    public async Task 全量导出时_上级编码都能在文件内找到()
    {
        var p = Prefix();

        await ImportAsync(Csv(
            new[] { "省厅", p + "00000000", "自洽省厅", "", "", "", "" },
            new[] { "地市", p + "01000000", "自洽市局", p + "00000000", "", "", "" },
            new[] { "县区", p + "01020000", "自洽县局", p + "01000000", "", "", "" },
            new[] { "学校", p + "01020001", "自洽小学", p + "01020000", "", "", "" }));

        var result = await _export.BuildAsync(new OrgQueryDto { OnlyEnabled = false });

        var rows = Text.Csv.Parse(result.Csv);
        var header = Text.Csv.BuildHeaderIndex(rows[0]);

        var codes = rows.Skip(1)
            .Select(r => Text.Csv.Cell(r, header, "组织编码"))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToHashSet(StringComparer.Ordinal);

        // 只看本用例造的那几行（测试库里还有别的数据，这里只验证这一段链条自洽）
        foreach (var row in rows.Skip(1))
        {
            var code = Text.Csv.Cell(row, header, "组织编码");

            if (code == null || !code.StartsWith(p, StringComparison.Ordinal))
            {
                continue;
            }

            var parentCode = Text.Csv.Cell(row, header, "上级组织编码");

            if (string.IsNullOrWhiteSpace(parentCode))
            {
                continue;
            }

            codes.ShouldContain(parentCode, $"「{code}」的上级「{parentCode}」必须也在导出文件里，否则导入后会变成根节点");
        }

        result.RowCount.ShouldBeGreaterThanOrEqualTo(4);
    }

    /// <summary>
    /// 按类型导出只出该类型 —— 顺带钉住"筛选真的生效"（不生效的话
    /// 用户会以为导出的就是全县学校，实际里面混着教育局）。
    /// </summary>
    [Fact]
    public async Task 只导出学校类型()
    {
        var p = Prefix();

        await ImportAsync(Csv(
            new[] { "省厅", p + "00000000", "筛选省厅", "", "", "", "" },
            new[] { "地市", p + "01000000", "筛选市局", p + "00000000", "", "", "" },
            new[] { "县区", p + "01020000", "筛选县局", p + "01000000", "", "", "" },
            new[] { "学校", p + "01020001", "筛选小学", p + "01020000", "", "", "" }));

        var result = await _export.BuildAsync(new OrgQueryDto
        {
            OrgType = OrgTypes.School,
            Keyword = "筛选",
            OnlyEnabled = false
        });

        var rows = Text.Csv.Parse(result.Csv);
        var header = Text.Csv.BuildHeaderIndex(rows[0]);

        var names = rows.Skip(1).Select(r => Text.Csv.Cell(r, header, "组织名称")).ToList();

        names.ShouldContain("筛选小学");
        names.ShouldNotContain("筛选县局");
        names.ShouldNotContain("筛选市局");
    }
}
