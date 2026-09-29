using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xjgl.Organizations;
using Xjgl.Students;
using Xjgl.Text;
using Xunit;

namespace Xjgl.Changes;

/// <summary>
/// 异动申请 / 审核流水导出的集成测试（ROADMAP 第 17 条）。
///
/// 【两份产物分别在守什么】
///   - 申请单导出：**一个人一行** —— "这段时间谁办了转学、办到哪一步"。
///   - 审核流水导出：**一个审批节点一行** —— "谁在什么时候审的、意见是什么"。
///     后者是复核/纪检最常要的东西，而它在申请单清单里表达不出来
///     （一份申请有多个节点）。
///
/// 【数据范围怎么验】
/// 范围过滤本身由 `IChangeFlowAppService.GetAppliesAsync` 负责（已有 `DataScopeTests` 覆盖）。
/// 这里验证的是**导出确实走的是它** —— 方法很直接：把导出的申请单号集合与
/// 同一身份调 `GetAppliesAsync` 得到的集合对齐。谁要是给导出另写一套查询，这条就会红。
/// </summary>
public class ChangeCsvExportTests : XjglIntegrationTestBase
{
    private const string Year = "2072-2073";

    private readonly IChangeCsvExportBuilder _export;
    private readonly IChangeFlowAppService _flow;
    private readonly IChangeTypeAppService _changeType;
    private readonly IOrgAppService _orgs;
    private readonly IStudentAppService _students;
    private readonly FakeCurrentPrincipalAccessor _principal;

    public ChangeCsvExportTests()
    {
        _export = GetRequiredService<IChangeCsvExportBuilder>();
        _flow = GetRequiredService<IChangeFlowAppService>();
        _changeType = GetRequiredService<IChangeTypeAppService>();
        _orgs = GetRequiredService<IOrgAppService>();
        _students = GetRequiredService<IStudentAppService>();
        _principal = GetRequiredService<FakeCurrentPrincipalAccessor>();
    }

    // ------------------------------------------------------------------
    // 脚手架
    // ------------------------------------------------------------------

    /// <summary>以 admin 身份跑：本类只验证"导出内容对不对"，与"谁有权看"无关。</summary>
    private void SignIn() => _principal.SetUser(Guid.NewGuid(), "change_export_tester", "admin");

    private static string Code() => "41" + Guid.NewGuid().ToString("N")[..8];

    private async Task<(OrgDto SchoolA, OrgDto SchoolB, StudentDto Student)> BuildAsync()
    {
        var province = await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.Province, Code = Code(), Name = "导出测试省厅"
        });

        var city = await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.City, Code = Code(), Name = "导出测试市局", ParentId = province.Id
        });

        var county = await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.County, Code = Code(), Name = "导出测试县局", ParentId = city.Id
        });

        var schoolA = await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School,
            // 学校标识码必须精确 10 位（学籍辅号的 {SchoolCode:10} 段依赖它）
            Code = county.Code[..6] + Guid.NewGuid().ToString("N")[..4],
            Name = "导出测试甲小",
            ParentId = county.Id
        });

        var schoolB = await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School,
            Code = county.Code[..6] + Guid.NewGuid().ToString("N")[..4],
            Name = "导出测试乙小",
            ParentId = county.Id
        });

        var student = await _students.CreateAsync(new CreateUpdateStudentDto
        {
            Name = "导出测试学生",
            GenderCode = "1",
            BirthDate = new DateTime(2014, 3, 1),
            IdCardTypeCode = "1",
            // 用基类的生成器：身份证**校验位**是被后端校验的，
            // 自己拼一个"看着像"的号会被直接拒掉（本类第一版就踩了这个）
            IdCardNumber = NewUniqueIdCard(),
            SchoolOrgId = schoolA.Id,
            AcademicYear = Year,
            StudentStatusCode = StudentStatuses.Studying
        });

        return (schoolA, schoolB, student);
    }

    /// <summary>建一个已发布的转学流程（两级：转入校 → 转入县区）。</summary>
    private async Task<ChangeTypeDto> CreateTypeAsync()
    {
        var type = await _changeType.CreateTypeAsync(new CreateUpdateChangeTypeDto
        {
            Code = "EXP_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Name = "导出测试转学",
            MaxApproveLevel = ApproveLevels.City,
            AfterStatusCode = StudentStatuses.TransferredIn
        });

        var template = await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
        {
            ChangeTypeId = type.Id,
            Name = "导出测试流程",
            Nodes = new List<CreateUpdateFlowNodeDto>
            {
                new()
                {
                    Seq = 1, NodeName = "转入学校审核",
                    OrgResolver = ChangeOrgResolvers.TargetSchool, RoleCode = "SchoolAdmin",
                    MergeStrategy = ChangeMergeStrategies.MergeToLast
                },
                new()
                {
                    Seq = 2, NodeName = "转入县区审核",
                    OrgResolver = ChangeOrgResolvers.TargetCounty, RoleCode = "CountyAdmin",
                    MergeStrategy = ChangeMergeStrategies.MergeToLast
                }
            }
        });

        await _changeType.PublishTemplateAsync(template.Id);

        return type;
    }

    // ==================================================================
    // 申请单导出
    // ==================================================================

    [Fact]
    public async Task 申请单导出_列齐全且含刚提交的申请()
    {
        SignIn();

        var (_, schoolB, student) = await BuildAsync();
        var type = await CreateTypeAsync();

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "家庭搬迁（导出测试）"
        });

        var result = await _export.BuildAppliesAsync(new ChangeApplyQueryDto
        {
            ChangeTypeId = type.Id,
            MaxResultCount = 100
        });

        var rows = Text.Csv.Parse(result.Csv);
        var header = Text.Csv.BuildHeaderIndex(rows[0]);

        header.Keys.ShouldBe(
            new[]
            {
                "申请单号", "异动类型", "学生姓名", "学籍辅号", "申请日期", "状态",
                "转出学校", "转入学校", "当前节点", "当前审核单位", "通过时间"
            },
            ignoreOrder: false);

        var mine = rows.Skip(1).Single(r => Text.Csv.Cell(r, header, "申请单号") == apply.ApplyNo);

        Text.Csv.Cell(mine, header, "学生姓名").ShouldBe("导出测试学生");
        Text.Csv.Cell(mine, header, "异动类型").ShouldBe("导出测试转学");
        // 状态与节点要出**中文名**（导出的表是给人看的，不是给程序读的）
        Text.Csv.Cell(mine, header, "状态").ShouldBe(apply.StatusName);
        // ⚠ 这条断言顺手守住了一个真实缺陷：`GetAppliesAsync` 原来只填 CurrentSeq、
        // 不填节点名，于是异动申请页与审核页"全部申请"页签的"当前节点"列**永远是空的**
        // （只有待办页签有值，因为那条查询另外填了名字）。
        Text.Csv.Cell(mine, header, "当前节点").ShouldBe("转入学校审核");
        Text.Csv.Cell(mine, header, "当前审核单位").ShouldBe("导出测试乙小");
        Text.Csv.Cell(mine, header, "转出学校").ShouldBe("导出测试甲小");
        Text.Csv.Cell(mine, header, "转入学校").ShouldBe("导出测试乙小");
    }

    /// <summary>
    /// 导出的申请单集合必须与**同一身份调列表接口**得到的集合一致。
    ///
    /// 这是"导出不比界面能看到的多"的可验证形式：两条路都走 `GetAppliesAsync`，
    /// 因此集合必须相等。
    /// </summary>
    [Fact]
    public async Task 申请单导出_与列表接口的可见集合一致()
    {
        SignIn();

        var (_, schoolB, student) = await BuildAsync();
        var type = await CreateTypeAsync();

        await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "一致性核对"
        });

        var query = new ChangeApplyQueryDto { ChangeTypeId = type.Id, MaxResultCount = 100 };

        var list = await _flow.GetAppliesAsync(query);
        var result = await _export.BuildAppliesAsync(query);

        var rows = Text.Csv.Parse(result.Csv);
        var header = Text.Csv.BuildHeaderIndex(rows[0]);

        var exported = rows.Skip(1)
            .Select(r => Text.Csv.Cell(r, header, "申请单号"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);

        exported.ShouldBe(list.Items.Select(i => i.ApplyNo).ToHashSet(StringComparer.Ordinal));
    }

    // ==================================================================
    // 审核流水导出
    // ==================================================================

    /// <summary>
    /// 一个审批节点一行，且**处理人 / 时间 / 意见**都要在 ——
    /// 复核场景问的正是"这一步是谁、在什么时候、以什么理由过的"。
    /// </summary>
    [Fact]
    public async Task 审核流水导出_一个节点一行且含处理人与意见()
    {
        SignIn();

        var (_, schoolB, student) = await BuildAsync();
        var type = await CreateTypeAsync();

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "流水导出测试"
        });

        // 审掉第一步，让流水里出现一条"已通过 + 处理人 + 意见"
        await _flow.ApproveAsync(new ChangeAuditActionDto
        {
            ApplyId = apply.Id,
            Opinion = "材料齐全，同意"
        });

        var result = await _export.BuildAuditAsync(new ChangeApplyQueryDto
        {
            ChangeTypeId = type.Id,
            MaxResultCount = 100
        });

        var rows = Text.Csv.Parse(result.Csv);
        var header = Text.Csv.BuildHeaderIndex(rows[0]);

        header.Keys.ShouldBe(
            new[]
            {
                "申请单号", "学生姓名", "学籍辅号", "异动类型", "序号", "审核节点",
                "审核单位", "审批级别", "状态", "处理人", "处理时间", "审核意见", "合并来源序号"
            },
            ignoreOrder: false);

        var mine = rows.Skip(1)
            .Where(r => Text.Csv.Cell(r, header, "申请单号") == apply.ApplyNo)
            .ToList();

        // 该申请单有两个节点 → 两行（这正是"一个节点一行"的意思）
        mine.Count.ShouldBe(2);

        var first = mine.Single(r => Text.Csv.Cell(r, header, "序号") == "1");

        Text.Csv.Cell(first, header, "审核节点").ShouldBe("转入学校审核");
        Text.Csv.Cell(first, header, "状态").ShouldBe("已通过");
        Text.Csv.Cell(first, header, "审核意见").ShouldBe("材料齐全，同意");
        Text.Csv.Cell(first, header, "处理人").ShouldNotBeNullOrWhiteSpace();
        Text.Csv.Cell(first, header, "处理时间").ShouldNotBeNullOrWhiteSpace();

        // 还没审到的那一步：状态是待处理、没有处理人
        var second = mine.Single(r => Text.Csv.Cell(r, header, "序号") == "2");

        Text.Csv.Cell(second, header, "状态").ShouldBe("待处理");
        Text.Csv.Cell(second, header, "处理人").ShouldBeNullOrEmpty();
    }

    /// <summary>没有任何申请单时也要出一份**带表头**的文件（而不是空文件）——
    /// 空的 CSV 在 Excel 里打开是一片空白，用户会以为导出坏了。</summary>
    [Fact]
    public async Task 空的审核流水导出_仍有表头()
    {
        SignIn();

        var result = await _export.BuildAuditAsync(new ChangeApplyQueryDto
        {
            // 一个不存在的异动类型 → 必然查不到申请单
            ChangeTypeId = Guid.NewGuid(),
            MaxResultCount = 100
        });

        var rows = Text.Csv.Parse(result.Csv);

        rows.Count.ShouldBe(1, "只有表头");
        result.RowCount.ShouldBe(0);
        Text.Csv.BuildHeaderIndex(rows[0]).Keys.ShouldContain("审核意见");
    }

    /// <summary>分页累积：跨页时不能只导出第一页。</summary>
    [Fact]
    public async Task 申请单导出_跨页累积()
    {
        SignIn();

        var (_, schoolB, student) = await BuildAsync();
        var type = await CreateTypeAsync();

        // 造 3 份申请（同一学生、同一类型，允许重复提交：本用例只关心行数）
        for (var i = 0; i < 3; i++)
        {
            await _flow.SubmitAsync(new SubmitChangeApplyDto
            {
                ChangeTypeId = type.Id,
                StudentId = student.Id,
                TargetSchoolOrgId = schoolB.Id,
                Reason = $"分页测试 {i}"
            });
        }

        // 列表接口的总数（一个内部页只能拿 2 条时，导出仍应拿到全部）
        var total = (await _flow.GetAppliesAsync(new ChangeApplyQueryDto
        {
            ChangeTypeId = type.Id,
            MaxResultCount = 1
        })).TotalCount;

        total.ShouldBeGreaterThanOrEqualTo(3);

        var result = await _export.BuildAppliesAsync(new ChangeApplyQueryDto
        {
            ChangeTypeId = type.Id,
            MaxResultCount = 100
        });

        var rows = Text.Csv.Parse(result.Csv);

        (rows.Count - 1).ShouldBe((int)result.RowCount);
        result.RowCount.ShouldBe((int)total, "导出应当拿到该条件下的全部申请单，而不是只有第一页");
        result.IsTruncated.ShouldBeFalse();
    }
}
