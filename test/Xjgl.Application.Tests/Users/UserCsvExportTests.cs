using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Volo.Abp.Identity;
using Volo.Abp.Uow;
using Xjgl.Data;
using Xjgl.Exporting;
using Xjgl.Organizations;
using Xjgl.Text;
using Xunit;

namespace Xjgl.Users;

/// <summary>
/// 账号清单导出的集成测试（ROADMAP 第 17 条）。
///
/// 【这组用例守的是什么】
///   「用户管理」页列出的是**全系统账号**（ABP 框架表，不按数据范围过滤），
///   所以导出必须与它对齐：能看到的都能导出来。
///   最容易出错的地方不是"导不出来"，而是**导出来的东西多了一层或少了一层**：
///     · 少了 → 用户以为系统丢了账号；
///     · 多了 → 越权把别人管的账号带走。
///   因此这里同时钉住"列齐全"与"关键字真的生效（不含无关账号）"。
///
/// 【为什么不去断言"总行数"】测试库是**长期累积**的（见 `OrganizationImportExportTests`
///   对同一问题的说明）：账号数量每轮都在变，任何绝对行数断言都会在下一次运行时红掉。
///   所以一律用"本次造出来的数据"做断言，其余行不影响。
/// </summary>
public class UserCsvExportTests : XjglIntegrationTestBase
{
    private const string ValidPassword = "Test@123456";

    private readonly IUserCsvExportBuilder _export;
    private readonly IdentityUserManager _userManager;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IOrgAppService _orgs;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public UserCsvExportTests()
    {
        _export = GetRequiredService<IUserCsvExportBuilder>();
        _userManager = GetRequiredService<IdentityUserManager>();
        _predefines = GetRequiredService<IXjglRepository<SysUserPredefine>>();
        _orgs = GetRequiredService<IOrgAppService>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();
    }

    // ------------------------------------------------------------------
    // 脚手架
    // ------------------------------------------------------------------

    /// <summary>每个用例一段唯一标识：账号表是长期累积的，固定用户名/手机号第二次就建不上。</summary>
    private static string Token() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>
    /// 随机中文姓名（**不能带数字**：本系统按 `RareCharacterText.IsValidPersonName`
    /// 校验证件式姓名，数字直接判非法 —— 报错只说"姓名格式不对"，很容易被误读成校验收紧了）。
    ///
    /// 名字取 8 个汉字是为了够随机，但它**不是**这一组用例的定位方式：
    /// 中文姓名不适合当关键字（`filter` 是模糊匹配，随机汉字串不保证唯一），
    /// 定位一律用 ASCII 的 `Token()`（它在用户名与邮箱里各出现一次）。
    /// </summary>
    private static string NewChineseSuffix()
    {
        const string pool = "宏博涵宇轩晨浩然梓睿思远航宁泽楷欣怡嘉懿";

        var chars = new char[8];

        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = pool[Random.Shared.Next(pool.Length)];
        }

        return new string(chars);
    }

    private async Task<OrgDto> CreateOrgAsync(string token)
    {
        return await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School,
            Code = "8" + Guid.NewGuid().ToString("N")[..8],
            Name = "账号导出测试小学" + token
        });
    }

    /// <summary>
    /// 建一个真实账号（框架表），可选地挂角色与预置记录。
    ///
    /// ⚠ 用户名/邮箱都带随机段：`AbpUsers` 对用户名有唯一索引，
    ///   而测试库是累积的 —— 固定值第二轮就抛"用户名已存在"。
    /// </summary>
    private async Task<IdentityUser> CreateUserAsync(
        string token,
        string? roleName = null,
        OrgDto? org = null,
        bool isActive = true)
    {
        var user = new IdentityUser(Guid.NewGuid(), $"t_{token}", $"{token}@xjgl.local")
        {
            Name = "账号导出" + NewChineseSuffix()
        };

        (await _userManager.CreateAsync(user, ValidPassword)).Succeeded.ShouldBeTrue();
        await _userManager.SetPhoneNumberAsync(user, "13" + Random.Shared.Next(100_000_000, 999_999_999));

        if (!isActive)
        {
            user.SetIsActive(false);
            await _userManager.UpdateAsync(user);
        }

        if (roleName != null)
        {
            // ⚠ 加角色必须在一个**显式工作单元**里，而且要在该工作单元内**重新取一次实体**：
            //   `AddToRoleAsync` 会 `EnsureCollectionLoadedAsync(user.Roles)`（EF Core 的集合加载，
            //   没有环境工作单元时直接抛 `A DbContext can only be created inside a unit of work!`）；
            //   而直接传外面那个实例又会在 `UpdateAsync` 时撞上
            //   "another instance with the same key value is already being tracked" ——
            //   两种报错看起来都像基础设施坏了，实际只是测试的工作单元边界没写对
            //   （与 `ModuleMetadataTests.我的上下文返回身份与角色` 同一处坑、同一处写法）。
            using var uow = _unitOfWorkManager.Begin(requiresNew: true);

            var tracked = await _userManager.FindByIdAsync(user.Id.ToString());
            tracked.ShouldNotBeNull();

            await _userManager.AddToRoleAsync(tracked, roleName);
            await uow.CompleteAsync();
        }

        if (org != null)
        {
            // 组织归属只在预置记录上 —— AbpUsers 没有组织字段（见 UserCsvExportBuilder 的说明）
            var predefine = new SysUserPredefine(
                Guid.NewGuid(),
                user.Name!,
                "13" + Random.Shared.Next(100_000_000, 999_999_999),
                org.Id,
                "CODE" + token,
                DateTime.Now.AddDays(30))
            {
                OrgPathCode = org.PathCode,
                ActivatedUserId = user.Id,
                ActivatedAt = DateTime.Now,
                Status = UserPredefineStatus.Activated
            };

            await _predefines.InsertAsync(predefine, autoSave: true);
        }

        return user;
    }

    /// <summary>从导出的 CSV 里取出属于本次运行的那一行。</summary>
    private static (List<string[]> Rows, Dictionary<string, int> Header) Parse(string csv)
    {
        var rows = Csv.Parse(csv);

        return (rows, Csv.BuildHeaderIndex(rows[0]));
    }

    // ==================================================================
    // 列
    // ==================================================================

    /// <summary>
    /// 列必须**齐全且顺序一致**：列名对不上时，拿到文件的人只会看到
    /// 一个"标题不对"的表，然后手工去猜哪一列是什么 —— 这类问题不会报错。
    /// </summary>
    [Fact]
    public async Task 导出的列齐全且与预期一致()
    {
        var (rows, header) = Parse((await _export.BuildAsync(new UserExportQueryDto())).Csv);

        header.Keys.ShouldBe(
            new[]
            {
                "用户名", "姓名", "手机号", "邮箱", "是否激活", "角色",
                "所属组织编码", "所属组织名称", "创建时间"
            },
            ignoreOrder: false);

        // 一行都没有时也必须带上表头（空文件在 Excel 里是一片空白，用户会以为导出坏了）
        rows.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    // ==================================================================
    // 关键字过滤
    // ==================================================================

    /// <summary>
    /// 关键字必须真的生效，而且**不能带出无关账号**。
    ///
    /// 这条是这个导出最要紧的断言：过滤失效时文件照样生成成功、照样"共 N 行"，
    /// 只是里面装着全系统的账号 —— 而使用者会以为这就是他要的那批人。
    /// </summary>
    [Fact]
    public async Task 关键字过滤生效_不含无关账号()
    {
        var mine = Token();
        var other = Token();

        await CreateUserAsync(mine);
        await CreateUserAsync(other);

        var result = await _export.BuildAsync(new UserExportQueryDto { Filter = mine });

        var (rows, header) = Parse(result.Csv);
        var userNames = rows.Skip(1).Select(r => Csv.Cell(r, header, "用户名")).ToList();

        userNames.ShouldContain($"t_{mine}");
        userNames.ShouldNotContain($"t_{other}");

        // 计数也要跟着过滤条件走：Total 与文件内容不一致时，
        // 截断提示会算出错误的"少了多少行"
        result.RowCount.ShouldBe(1);
    }

    // ==================================================================
    // 角色与所属组织（两条 JOIN 各自都有坑）
    // ==================================================================

    /// <summary>
    /// 角色来自框架表（`AbpUserRoles`），所属组织来自**预置记录反查**
    /// （`AbpUsers` 上没有组织字段）。
    ///
    /// 这两列是这一份导出存在的理由：只有这一份文件能同时回答
    /// "这个人是谁"与"他属于哪个学校、拿了什么角色"。
    /// </summary>
    [Fact]
    public async Task 角色与所属组织分别来自框架表与预置记录()
    {
        var token = Token();
        var org = await CreateOrgAsync(token);

        var user = await CreateUserAsync(token, roleName: "admin", org: org);

        var result = await _export.BuildAsync(new UserExportQueryDto { Filter = token });

        var (rows, header) = Parse(result.Csv);
        var row = rows.Skip(1).Single(r => Csv.Cell(r, header, "用户名") == $"t_{token}");

        Csv.Cell(row, header, "姓名").ShouldBe(user.Name);
        Csv.Cell(row, header, "是否激活").ShouldBe("是");
        Csv.Cell(row, header, "角色").ShouldContain("admin");

        // 组织列反查自预置记录；**不是**从 PathCode 里截出来的
        Csv.Cell(row, header, "所属组织编码").ShouldBe(org.Code);
        Csv.Cell(row, header, "所属组织名称").ShouldBe(org.Name);
        Csv.Cell(row, header, "创建时间").ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// 没有预置记录的账号（admin、演示账号、历史上直接建的账号）组织列留空。
    ///
    /// 【为什么断言"留空"而不是"随便给一个"】组织归属决定数据范围与统计口径，
    /// 猜一个出来会被当成事实使用 —— 而 `MyProfileAppService` 在查不到预置记录时
    /// 也是"什么都不填"，两处口径必须一致（否则同一个人在两个界面上属于两个学校）。
    /// </summary>
    [Fact]
    public async Task 没有预置记录的账号_组织列留空而不是猜一个()
    {
        var token = Token();

        await CreateUserAsync(token);

        var result = await _export.BuildAsync(new UserExportQueryDto { Filter = token });

        var (rows, header) = Parse(result.Csv);
        var row = rows.Skip(1).Single(r => Csv.Cell(r, header, "用户名") == $"t_{token}");

        Csv.Cell(row, header, "所属组织编码").ShouldBeNullOrEmpty();
        Csv.Cell(row, header, "所属组织名称").ShouldBeNullOrEmpty();
        Csv.Cell(row, header, "角色").ShouldBeNullOrEmpty();
    }

    /// <summary>停用的账号在"是否激活"列出「否」—— 这一列是核对"哪些账号被停过"的唯一入口。</summary>
    [Fact]
    public async Task 停用的账号在是否激活列出否()
    {
        var token = Token();

        await CreateUserAsync(token, isActive: false);

        var result = await _export.BuildAsync(new UserExportQueryDto { Filter = token });

        var (rows, header) = Parse(result.Csv);
        var row = rows.Skip(1).Single(r => Csv.Cell(r, header, "用户名") == $"t_{token}");

        Csv.Cell(row, header, "是否激活").ShouldBe("否");
    }

    // ==================================================================
    // 任务种类接线
    // ==================================================================

    /// <summary>
    /// 新种类必须**在权限映射与已知种类里都登记**，否则前端点了导出会得到
    /// "未知的任务种类"或 403 —— 而这两件事在编译期都不会报错。
    /// </summary>
    [Fact]
    public void 账号导出种类已登记且映射到用户导出权限()
    {
        ExportTaskKinds.All.ShouldContain(ExportTaskKinds.UserCsv);
        ExportTaskKinds.GetDisplayName(ExportTaskKinds.UserCsv).ShouldBe("账号清单导出");

        ExportTaskPermissions.IsKnown(ExportTaskKinds.UserCsv).ShouldBeTrue();
        ExportTaskPermissions.GetRequiredPermissions(ExportTaskKinds.UserCsv)
            .ShouldBe(new[] { "Sys.User.Export" });

        // 刻意**没有** Sys.User.Import：账号导入不做（理由见 MetadataPermissions.Sys.User）。
        // 这条断言是防"好心人"顺手把它加回来 —— 声明了却不实现，
        // 权限管理页就会出现"勾了却不影响任何行为"的项。
        typeof(Permissions.MetadataPermissions.Sys.User)
            .GetField("Import").ShouldBeNull();
    }
}
