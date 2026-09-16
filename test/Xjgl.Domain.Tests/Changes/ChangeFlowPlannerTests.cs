using Shouldly;
using Xunit;

namespace Xjgl.Changes;

/// <summary>
/// 异动流程实例化算法的单元测试。
///
/// 这是整个「可配置审批流」最核心的一组用例：把方案里对转学流程的三条推演钉死。
/// 算法被刻意做成纯函数，因此这里不需要数据库，跑得飞快且结论确定。
///
/// 关键认识：「同县区转学县区只审一次」**不是**针对该场景写的特例，
/// 而是「按解析出的真实组织去重合并」的自然结果。
/// 所以只要这三条推演成立，业务人员在界面上改流程（换解析器、调顺序、改合并策略）
/// 都不需要改代码。
/// </summary>
public class ChangeFlowPlannerTests
{
    private readonly ChangeFlowPlanner _planner = new();

    // ------------------------------------------------------------------
    // 测试用的组织数据
    //   410000 (省)
    //     ├─ 410100 (甲市)
    //     │    ├─ 410102 (甲市一县)
    //     │    │     └─ 学校 A1
    //     │    └─ 410103 (甲市二县)
    //     │          └─ 学校 A2
    //     └─ 410200 (乙市)
    //          └─ 410202 (乙市一县)
    //                └─ 学校 B1
    // ------------------------------------------------------------------

    private static readonly ResolvedOrg Province = Org(1, "410000", "省教育厅", "Province");
    private static readonly ResolvedOrg CityA = Org(2, "410100", "甲市教育局", "City");
    private static readonly ResolvedOrg CityB = Org(3, "410200", "乙市教育局", "City");
    private static readonly ResolvedOrg CountyA1 = Org(4, "410102", "甲市一县教育局", "County");
    private static readonly ResolvedOrg CountyA2 = Org(5, "410103", "甲市二县教育局", "County");
    private static readonly ResolvedOrg CountyB1 = Org(6, "410202", "乙市一县教育局", "County");
    private static readonly ResolvedOrg SchoolA1 = Org(7, "4101020001", "甲市一县第一小学", "School");
    private static readonly ResolvedOrg SchoolA1B = Org(10, "4101020002", "甲市一县第二小学", "School");
    private static readonly ResolvedOrg SchoolA2 = Org(8, "4101030001", "甲市二县第一小学", "School");
    private static readonly ResolvedOrg SchoolB1 = Org(9, "4102020001", "乙市一县第一小学", "School");

    private static ResolvedOrg Org(int seed, string code, string name, string type)
    {
        var id = new Guid(seed, 0, 0, new byte[8]);
        return new ResolvedOrg { Id = id, Code = code, Name = name, OrgType = type, PathCode = code };
    }

    /// <summary>组织解析函数：按解析器名返回对应组织。</summary>
    private static Func<string, ResolvedOrg?> Resolver(
        ResolvedOrg targetSchool,
        ResolvedOrg sourceSchool,
        ResolvedOrg targetCounty,
        ResolvedOrg targetCity,
        ResolvedOrg sourceCounty,
        ResolvedOrg sourceCity)
    {
        return key => key switch
        {
            ChangeOrgResolvers.ApplicantSchool => targetSchool,
            ChangeOrgResolvers.TargetSchool => targetSchool,
            ChangeOrgResolvers.StudentSchool => sourceSchool,
            ChangeOrgResolvers.SourceSchool => sourceSchool,
            ChangeOrgResolvers.TargetCounty => targetCounty,
            ChangeOrgResolvers.TargetCity => targetCity,
            ChangeOrgResolvers.SourceCounty => sourceCounty,
            ChangeOrgResolvers.SourceCity => sourceCity,
            ChangeOrgResolvers.Province => Province,
            _ => null
        };
    }

    /// <summary>简化：按「目标学校 / 原学校」自动推导其县区与地市。</summary>
    private static Func<string, ResolvedOrg?> ResolverFor(ResolvedOrg targetSchool, ResolvedOrg sourceSchool)
    {
        var targetCounty = CountyOf(targetSchool);
        var sourceCounty = CountyOf(sourceSchool);

        var targetCity = targetCounty == CountyB1 ? CityB : CityA;
        var sourceCity = sourceCounty == CountyB1 ? CityB : CityA;

        return Resolver(targetSchool, sourceSchool, targetCounty, targetCity, sourceCounty, sourceCity);
    }

    private static ResolvedOrg CountyOf(ResolvedOrg school) => school switch
    {
        _ when school == SchoolA1 || school == SchoolA1B => CountyA1,
        _ when school == SchoolA2 => CountyA2,
        _ => CountyB1
    };

    /// <summary>转学流程模板：6 个节点，与方案文档第 3.5 节一致。</summary>
    private static List<FlowNodeDefinition> TransferTemplate(string mergeStrategy = ChangeMergeStrategies.MergeToLast)
    {
        return new List<FlowNodeDefinition>
        {
            Node(1, "转入学校提交", ChangeOrgResolvers.TargetSchool, "SchoolAdmin", mergeStrategy),
            Node(2, "转入县区审核", ChangeOrgResolvers.TargetCounty, "CountyAdmin", mergeStrategy),
            Node(3, "转入地市审核", ChangeOrgResolvers.TargetCity, "CityAdmin", mergeStrategy),
            Node(4, "转出学校审核", ChangeOrgResolvers.SourceSchool, "SchoolAdmin", mergeStrategy),
            Node(5, "转出县区审核", ChangeOrgResolvers.SourceCounty, "CountyAdmin", mergeStrategy),
            Node(6, "转出地市审核", ChangeOrgResolvers.SourceCity, "CityAdmin", mergeStrategy)
        };
    }

    private static FlowNodeDefinition Node(int seq, string name, string resolver, string role, string strategy)
    {
        return new FlowNodeDefinition
        {
            Seq = seq,
            Name = name,
            OrgResolver = resolver,
            RoleCode = role,
            MergeStrategy = strategy
        };
    }

    /// <summary>修学流程模板：3 个节点，到地市结束。</summary>
    private static List<FlowNodeDefinition> SuspendTemplate()
    {
        return new List<FlowNodeDefinition>
        {
            Node(1, "学校提交", ChangeOrgResolvers.StudentSchool, "SchoolAdmin", ChangeMergeStrategies.MergeToLast),
            Node(2, "县区审核", ChangeOrgResolvers.SourceCounty, "CountyAdmin", ChangeMergeStrategies.MergeToLast),
            Node(3, "地市审核", ChangeOrgResolvers.SourceCity, "CityAdmin", ChangeMergeStrategies.MergeToLast)
        };
    }

    // ==================================================================
    // 三条核心推演
    // ==================================================================

    [Fact]
    public void 跨市转学_六个节点全部保留_按顺序执行()
    {
        // 目标学校在甲市一县，原学校在乙市一县 → 六个组织互不相同
        var plan = _planner.Plan(
            TransferTemplate(),
            ResolverFor(SchoolA1, SchoolB1),
            ApproveLevels.City);

        plan.Warnings.ShouldBeEmpty();

        plan.Tasks.Select(t => t.Seq).ShouldBe(new[] { 1, 2, 3, 4, 5, 6 });
        plan.Tasks.Select(t => t.OrgName).ShouldBe(new[]
        {
            "甲市一县第一小学", "甲市一县教育局", "甲市教育局",
            "乙市一县第一小学", "乙市一县教育局", "乙市教育局"
        });

        // 没有任何合并且完整
        plan.Tasks.ShouldAllBe(t => t.MergedFromSeqs.Count == 0);
    }

    [Fact]
    public void 同市跨县转学_市级只审一次_合并到靠后的节点()
    {
        // 目标学校在甲市一县，原学校在甲市二县 → 县区不同，地市相同
        var plan = _planner.Plan(
            TransferTemplate(),
            ResolverFor(SchoolA1, SchoolA2),
            ApproveLevels.City);

        // 序号 3（转入地市）与 6（转出地市）解析到同一组织 → 合并到 6
        plan.Tasks.Select(t => t.Seq).ShouldBe(new[] { 1, 2, 4, 5, 6 });
        plan.Tasks.Select(t => t.OrgName).ShouldBe(new[]
        {
            "甲市一县第一小学", "甲市一县教育局", "甲市二县第一小学", "甲市二县教育局", "甲市教育局"
        });

        var cityTask = plan.Tasks.Single(t => t.Seq == 6);
        cityTask.MergedFromSeqs.ShouldBe(new[] { 3 });
        cityTask.ApproveLevel.ShouldBe(ApproveLevels.City);
    }

    [Fact]
    public void 同县区转学_县区与地市各只审一次()
    {
        // 目标学校与原学校都在甲市一县，但是**两所不同的学校**
        var plan = _planner.Plan(
            TransferTemplate(),
            ResolverFor(SchoolA1, SchoolA1B),
            ApproveLevels.City);

        // 2/5 同县合并到 5；3/6 同市合并到 6；学校不同所以 1 与 4 都保留
        plan.Tasks.Select(t => t.Seq).ShouldBe(new[] { 1, 4, 5, 6 });
        plan.Tasks.Select(t => t.OrgName).ShouldBe(new[]
        {
            "甲市一县第一小学", "甲市一县第二小学", "甲市一县教育局", "甲市教育局"
        });

        plan.Tasks.Single(t => t.Seq == 5).MergedFromSeqs.ShouldBe(new[] { 2 });
        plan.Tasks.Single(t => t.Seq == 6).MergedFromSeqs.ShouldBe(new[] { 3 });
    }

    [Fact]
    public void 本校内部异动_学校节点也会合并_学校只审一次()
    {
        // 转入校与转出校是同一所 → 学校节点（1 与 4）同样去重
        var plan = _planner.Plan(
            TransferTemplate(),
            ResolverFor(SchoolA1, SchoolA1),
            ApproveLevels.City);

        plan.Tasks.Select(t => t.Seq).ShouldBe(new[] { 4, 5, 6 });
        plan.Tasks.Single(t => t.Seq == 4).MergedFromSeqs.ShouldBe(new[] { 1 });
    }

    // ==================================================================
    // 截断（异动类型里配置的审核终止级别）
    // ==================================================================

    [Fact]
    public void 终止级别为县_地市节点被截断()
    {
        var plan = _planner.Plan(
            TransferTemplate(),
            ResolverFor(SchoolA1, SchoolB1),
            ApproveLevels.County);

        plan.Tasks.Select(t => t.Seq).ShouldBe(new[] { 1, 2, 4, 5 });
        plan.Tasks.ShouldAllBe(t => t.ApproveLevel != ApproveLevels.City);

        plan.Warnings.Count.ShouldBe(2);
        plan.Warnings.ShouldAllBe(w => w.Contains("截断"));
    }

    [Fact]
    public void 终止级别为省_可追加省级节点()
    {
        var nodes = TransferTemplate();
        nodes.Add(Node(7, "省厅审核", ChangeOrgResolvers.Province, "ProvinceAdmin",
            ChangeMergeStrategies.MergeToLast));

        var plan = _planner.Plan(nodes, ResolverFor(SchoolA1, SchoolB1), ApproveLevels.Province);

        plan.Tasks.Count.ShouldBe(7);
        plan.Tasks.Last().OrgName.ShouldBe("省教育厅");
        plan.Tasks.Last().ApproveLevel.ShouldBe(ApproveLevels.Province);
    }

    [Fact]
    public void 终止级别为省但模板没有省级节点_不报错_按现有节点结束()
    {
        var plan = _planner.Plan(
            TransferTemplate(),
            ResolverFor(SchoolA1, SchoolB1),
            ApproveLevels.Province);

        plan.Tasks.Count.ShouldBe(6);
    }

    [Fact]
    public void 休学流程_三个节点顺序执行()
    {
        var plan = _planner.Plan(
            SuspendTemplate(),
            ResolverFor(SchoolA1, SchoolA1),
            ApproveLevels.City);

        plan.Tasks.Select(t => t.Seq).ShouldBe(new[] { 1, 2, 3 });
        plan.Tasks.Select(t => t.OrgName).ShouldBe(new[]
        {
            "甲市一县第一小学", "甲市一县教育局", "甲市教育局"
        });
    }

    // ==================================================================
    // 合并策略与健壮性
    // ==================================================================

    [Fact]
    public void 合并到靠前的节点_县级审核提前到转入环节()
    {
        var plan = _planner.Plan(
            TransferTemplate(ChangeMergeStrategies.MergeToFirst),
            ResolverFor(SchoolA1, SchoolA1B),
            ApproveLevels.City);

        // 县区合并到 2（较早），地市合并到 3（较早）
        plan.Tasks.Select(t => t.Seq).ShouldBe(new[] { 1, 2, 3, 4 });
        plan.Tasks.Single(t => t.Seq == 2).MergedFromSeqs.ShouldBe(new[] { 5 });
        plan.Tasks.Single(t => t.Seq == 3).MergedFromSeqs.ShouldBe(new[] { 6 });
    }

    [Fact]
    public void 不合并策略_同一组织出现多个待办()
    {
        var plan = _planner.Plan(
            TransferTemplate(ChangeMergeStrategies.NoMerge),
            ResolverFor(SchoolA1, SchoolA1B),
            ApproveLevels.City);

        plan.Tasks.Count.ShouldBe(6);
        plan.Tasks.ShouldAllBe(t => t.MergedFromSeqs.Count == 0);

        // 县区组织会出现两次（转入县区、转出县区各一个待办）
        plan.Tasks.Count(t => t.OrgName == "甲市一县教育局").ShouldBe(2);
    }

    [Fact]
    public void 组织解析失败时跳过该节点并给出告警()
    {
        // 解析器全部返回 null
        var plan = _planner.Plan(TransferTemplate(), _ => null, ApproveLevels.City);

        plan.Tasks.ShouldBeEmpty();
        plan.Warnings.Count.ShouldBe(6);
        plan.Warnings.ShouldAllBe(w => w.Contains("无法解析组织"));
    }

    [Fact]
    public void 终止级别不合法时按校级处理并告警()
    {
        var plan = _planner.Plan(
            TransferTemplate(),
            ResolverFor(SchoolA1, SchoolB1),
            "NotALevel");

        plan.Warnings.ShouldContain(w => w.Contains("不合法"));

        // 只剩校级节点
        plan.Tasks.ShouldAllBe(t => t.ApproveLevel == ApproveLevels.School);
        plan.Tasks.Select(t => t.Seq).ShouldBe(new[] { 1, 4 });
    }

    [Fact]
    public void 停用的节点不参与实例化()
    {
        var nodes = TransferTemplate();
        var disabled = new FlowNodeDefinition
        {
            Seq = nodes[1].Seq,
            Name = nodes[1].Name,
            OrgResolver = nodes[1].OrgResolver,
            RoleCode = nodes[1].RoleCode,
            IsEnabled = false
        };

        nodes[1] = disabled;

        var plan = _planner.Plan(nodes, ResolverFor(SchoolA1, SchoolB1), ApproveLevels.City);

        plan.Tasks.Select(t => t.Seq).ShouldBe(new[] { 1, 3, 4, 5, 6 });
    }

    [Fact]
    public void 输出顺序始终按节点序号_与合并策略无关()
    {
        foreach (var strategy in ChangeMergeStrategies.All)
        {
            var plan = _planner.Plan(
                TransferTemplate(strategy),
                ResolverFor(SchoolA1, SchoolA2),
                ApproveLevels.City);

            plan.Tasks.Select(t => t.Seq).ShouldBe(plan.Tasks.Select(t => t.Seq).OrderBy(s => s));
        }
    }
}
