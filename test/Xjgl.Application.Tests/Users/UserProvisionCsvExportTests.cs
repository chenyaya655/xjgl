using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xjgl.Data;
using Xjgl.Exporting;
using Xjgl.Organizations;
using Xjgl.Text;
using Xunit;

namespace Xjgl.Users;

/// <summary>
/// 预置名单导出的集成测试（ROADMAP 第 17 条）。
///
/// 【这组用例守的是什么】
///   预置名单是「谁还没激活」的名单，导出它只有两个用途：催办、以及留一份底。
///   而这份名单里最敏感的一列是身份证号 —— 本项目的身份证是**加密存储**的，
///   导出明文等于把加密白做一遍。所以这里的第一条断言不是"列对不对"，
///   而是"**文件里根本不存在完整身份证号**"。
///
/// 【为什么第一行是说明、表头在第 2 行】
///   掩码的代价是这份文件**不能回导**（激活校验要完整身份证号）。
///   那句话必须跟着文件走：文件被转发之后，界面上的提示就不跟着了。
///   所以断言里 `rows[0]` 是说明、`rows[1]` 才是表头 —— 这个位置是**约定**，改它要连着改用例。
/// </summary>
public class UserProvisionCsvExportTests : XjglIntegrationTestBase
{
    private readonly IUserProvisionCsvExportBuilder _export;
    private readonly IUserProvisionAppService _provision;
    private readonly IOrgAppService _orgs;

    public UserProvisionCsvExportTests()
    {
        _export = GetRequiredService<IUserProvisionCsvExportBuilder>();
        _provision = GetRequiredService<IUserProvisionAppService>();
        _orgs = GetRequiredService<IOrgAppService>();
    }

    // ------------------------------------------------------------------
    // 脚手架
    // ------------------------------------------------------------------

    /// <summary>每个用例一段唯一标识（测试库累积，组织编码等 ASCII 场合用它）。</summary>
    private static string Token() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 随机中文姓名后缀，**同时用作关键字**。
    ///
    /// ⚠ 不能拿 `Guid` 的十六进制串当姓名：预置接口按
    ///   `RareCharacterText.IsValidPersonName` 校验证件式姓名，**数字直接判非法**
    ///   （报错只说"姓名格式不对"，看起来像校验收紧了，实际是测试数据不合规）。
    ///   后缀取 8 个汉字（22^8 ≈ 5.5e10 种），是为了让"按关键字筛出来的只有我这一条"
    ///   这个前提在长期累积的测试库里也成立。
    /// </summary>
    private static string NewSuffix()
    {
        const string pool = "宏博涵宇轩晨浩然梓睿思远航宁泽楷欣怡嘉懿";

        var chars = new char[8];

        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = pool[Random.Shared.Next(pool.Length)];
        }

        return new string(chars);
    }

    private static string NewPhone() => "13" + Random.Shared.Next(100_000_000, 999_999_999);

    private async Task<OrgDto> CreateOrgAsync(string token)
    {
        return await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School,
            Code = "7" + Guid.NewGuid().ToString("N")[..8],
            Name = "预置导出测试小学" + token
        });
    }

    /// <summary>预置一条记录，返回 DTO 与**未脱敏**的身份证号（断言掩码时要用它）。</summary>
    private async Task<(PredefineUserDto Dto, string IdCard, string Phone)> PredefineAsync(
        Guid orgId,
        string suffix,
        string? roleNames = "Teacher",
        string? position = "学籍管理员")
    {
        var idCard = NewUniqueIdCard();
        var phone = NewPhone();

        var dto = await _provision.CreateAsync(new CreatePredefineUserDto
        {
            RealName = "预置导出" + suffix,
            PhoneNumber = phone,
            IdCardNumber = idCard,
            OrgId = orgId,
            RoleNames = roleNames,
            Position = position,
            ActivationValidDays = 30
        });

        return (dto, idCard, phone);
    }

    /// <summary>第 1 行是说明、第 2 行是表头，数据从第 3 行开始。</summary>
    private static (List<string[]> Rows, Dictionary<string, int> Header) Parse(string csv)
    {
        var rows = Csv.Parse(csv);

        rows.Count.ShouldBeGreaterThanOrEqualTo(2, "至少要有说明行与表头行");

        return (rows, Csv.BuildHeaderIndex(rows[1]));
    }

    // ==================================================================
    // 列
    // ==================================================================

    [Fact]
    public async Task 导出的列齐全且与预期一致()
    {
        var result = await _export.BuildAsync(new PredefineUserQueryDto());

        var (_, header) = Parse(result.Csv);

        header.Keys.ShouldBe(
            new[]
            {
                "姓名", "手机号", "身份证后4位", "组织编码", "组织名称",
                "角色", "岗位", "状态", "激活有效期"
            },
            ignoreOrder: false);
    }

    /// <summary>
    /// 第一行必须是"身份证已掩码、不能回导"的说明。
    ///
    /// 不写这一行的后果很具体：拿到文件的人以为照它填一遍就能批量预置，
    /// 然后在激活环节被"身份证号与预置名单不一致"拒掉 —— 而那时已经过去了几天。
    /// </summary>
    [Fact]
    public async Task 第一行是身份证掩码说明()
    {
        var result = await _export.BuildAsync(new PredefineUserQueryDto());

        var rows = Csv.Parse(result.Csv);

        rows[0][0].ShouldContain("身份证");
        rows[0][0].ShouldContain("掩码");
        rows[0][0].ShouldContain("不能用于回导");
    }

    // ==================================================================
    // 身份证掩码（这组用例的核心）
    // ==================================================================

    /// <summary>
    /// 身份证**只保留后 4 位**，其余一律 `*`；完整号码在文件里一个字都不能出现。
    ///
    /// 【为什么断言"整份文件里搜不到明文"而不是只比对那一格】
    ///   掩码只做一半（比如前 6 位照旧露着）同样能通过"这一格有星号"的断言 ——
    ///   而前 6 位是行政区划、中间 8 位是出生日期，两段加起来已经足够定位一个人。
    ///   所以这里直接在整个文件文本上搜明文，搜到即失败。
    /// </summary>
    [Fact]
    public async Task 身份证已掩码_只保留后4位且文件里没有明文()
    {
        var suffix = NewSuffix();
        var org = await CreateOrgAsync(Token());

        var (_, idCard, _) = await PredefineAsync(org.Id, suffix);

        var result = await _export.BuildAsync(new PredefineUserQueryDto { Keyword = suffix });

        result.Csv.Contains(idCard, StringComparison.Ordinal).ShouldBeFalse(
            "导出的文件里绝不能出现完整身份证号 —— 身份证是加密存储的，导出明文等于把加密白做一遍");

        var (rows, header) = Parse(result.Csv);
        var row = rows.Skip(2).Single(r => Csv.Cell(r, header, "姓名") == "预置导出" + suffix);

        Csv.Cell(row, header, "身份证后4位")
            .ShouldBe(new string('*', idCard.Length - 4) + idCard[^4..]);
    }

    /// <summary>没登记身份证的预置记录，那一格留空（填星号会被读成"登记过但看不清"）。</summary>
    [Fact]
    public async Task 未登记身份证时该列留空()
    {
        var suffix = NewSuffix();
        var org = await CreateOrgAsync(Token());

        await _provision.CreateAsync(new CreatePredefineUserDto
        {
            RealName = "预置导出" + suffix,
            PhoneNumber = NewPhone(),
            OrgId = org.Id
        });

        var result = await _export.BuildAsync(new PredefineUserQueryDto { Keyword = suffix });

        var (rows, header) = Parse(result.Csv);
        var row = rows.Skip(2).Single(r => Csv.Cell(r, header, "姓名") == "预置导出" + suffix);

        Csv.Cell(row, header, "身份证后4位").ShouldBeNullOrEmpty();
    }

    // ==================================================================
    // 关键字过滤
    // ==================================================================

    /// <summary>
    /// 关键字必须真的生效，而且**不带出无关的人**。
    ///
    /// 导出复用列表查询，就是为了让"页面上筛出来的"与"文件里导出的"是同一批；
    /// 万一哪天有人给导出另写一套 where，这条会红。
    /// </summary>
    [Fact]
    public async Task 关键字过滤生效_不含无关数据()
    {
        var mine = NewSuffix();
        var other = NewSuffix();

        var org = await CreateOrgAsync(Token());

        await PredefineAsync(org.Id, mine);
        await PredefineAsync(org.Id, other);

        var result = await _export.BuildAsync(new PredefineUserQueryDto { Keyword = mine });

        var (rows, header) = Parse(result.Csv);
        var names = rows.Skip(2).Select(r => Csv.Cell(r, header, "姓名")).ToList();

        names.ShouldContain("预置导出" + mine);
        names.ShouldNotContain("预置导出" + other);

        // 计数也要跟着条件走，否则截断提示会算出错误的"少了多少行"
        result.RowCount.ShouldBe(1);
        result.Total.ShouldBe(1);
    }

    /// <summary>状态过滤同样生效（"还有多少没激活"是按状态拉名单的典型用法）。</summary>
    [Fact]
    public async Task 状态过滤生效()
    {
        var suffix = NewSuffix();
        var org = await CreateOrgAsync(Token());

        await PredefineAsync(org.Id, suffix);

        var result = await _export.BuildAsync(new PredefineUserQueryDto
        {
            Keyword = suffix,
            Status = UserPredefineStatus.Activated
        });

        result.RowCount.ShouldBe(0, "这批预置记录都是待激活状态");
    }

    // ==================================================================
    // 其余列与任务种类接线
    // ==================================================================

    [Fact]
    public async Task 组织角色岗位状态与有效期都如实写出()
    {
        var suffix = NewSuffix();
        var org = await CreateOrgAsync(Token());

        var (dto, _, _) = await PredefineAsync(org.Id, suffix);

        var result = await _export.BuildAsync(new PredefineUserQueryDto { Keyword = suffix });

        var (rows, header) = Parse(result.Csv);
        var row = rows.Skip(2).Single(r => Csv.Cell(r, header, "姓名") == "预置导出" + suffix);

        Csv.Cell(row, header, "组织编码").ShouldBe(org.Code);
        Csv.Cell(row, header, "组织名称").ShouldBe(org.Name);
        Csv.Cell(row, header, "角色").ShouldBe("Teacher");
        Csv.Cell(row, header, "岗位").ShouldBe("学籍管理员");
        // 状态出中文名（导出的表是给人看的）
        Csv.Cell(row, header, "状态").ShouldBe(UserPredefineStatus.GetDisplayName(dto.Status));
        Csv.Cell(row, header, "激活有效期")
            .ShouldBe(dto.ActivationExpiresAt.ToString("yyyy-MM-dd"));
        // 手机号与页面上一致地脱敏（导出不比页面能看到的多）
        Csv.Cell(row, header, "手机号").ShouldContain("****");
    }

    [Fact]
    public void 预置名单导出种类已登记且同时要求导出与查看权限()
    {
        ExportTaskKinds.All.ShouldContain(ExportTaskKinds.UserProvisionCsv);
        ExportTaskKinds.GetDisplayName(ExportTaskKinds.UserProvisionCsv).ShouldBe("预置名单导出");

        ExportTaskPermissions.IsKnown(ExportTaskKinds.UserProvisionCsv).ShouldBeTrue();

        // 导出复用了列表查询（数据范围与脱敏只有一处实现），
        // 所以内部那次调用也要过 Query —— 少要一个就会在后台线程里炸 403，
        // 而用户只看到"任务失败"，看不出跟权限有关（与照片导出同一条理由）。
        ExportTaskPermissions.GetRequiredPermissions(ExportTaskKinds.UserProvisionCsv)
            .ShouldBe(new[] { "Sys.UserProvision.Export", "Sys.UserProvision.Query" });
    }
}
