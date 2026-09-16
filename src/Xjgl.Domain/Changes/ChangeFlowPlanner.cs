using Volo.Abp.DependencyInjection;

namespace Xjgl.Changes;

/// <summary>流程节点定义（来自流程模板，与具体组织无关）。</summary>
public sealed class FlowNodeDefinition
{
    /// <summary>节点序号，从 1 开始，决定先后顺序。</summary>
    public int Seq { get; init; }

    /// <summary>节点名称，如「转入县区审核」。</summary>
    public string Name { get; init; } = default!;

    /// <summary>组织解析器，取值见 <see cref="ChangeOrgResolvers"/>。</summary>
    public string OrgResolver { get; init; } = default!;

    /// <summary>审核角色编码（校级/县区级/地市级/省厅级学籍管理员）。</summary>
    public string RoleCode { get; init; } = default!;

    /// <summary>合并策略，取值见 <see cref="ChangeMergeStrategies"/>。</summary>
    public string MergeStrategy { get; init; } = ChangeMergeStrategies.MergeToLast;

    /// <summary>是否允许驳回。</summary>
    public bool AllowReject { get; init; } = true;

    /// <summary>超时天数（0 表示不限制）。</summary>
    public int TimeoutDays { get; init; }

    public bool IsEnabled { get; init; } = true;
}

/// <summary>解析后的真实组织。</summary>
public sealed class ResolvedOrg
{
    public Guid Id { get; init; }
    public string Code { get; init; } = default!;
    public string Name { get; init; } = default!;

    /// <summary>组织类型（Province/City/County/School）。</summary>
    public string OrgType { get; init; } = default!;

    /// <summary>物化路径。</summary>
    public string PathCode { get; init; } = default!;
}

/// <summary>计划出的流程任务（尚未落库）。</summary>
public sealed class PlannedFlowTask
{
    /// <summary>该任务在最终序列中的序号（等于存活节点的原始 Seq）。</summary>
    public int Seq { get; init; }

    public string Name { get; init; } = default!;
    public Guid OrgId { get; init; }
    public string OrgCode { get; init; } = default!;
    public string OrgName { get; init; } = default!;
    public string RoleCode { get; init; } = default!;

    /// <summary>该任务对应的审核级别（由组织的 OrgType 推出）。</summary>
    public string ApproveLevel { get; init; } = default!;

    public bool AllowReject { get; init; }
    public int TimeoutDays { get; init; }

    /// <summary>
    /// 被合并进来的节点序号。
    /// 例如「同县区转学」时县区节点（序号 5）的该字段为 [2]，
    /// 表示它同时代表了「转入县区」与「转出县区」两个审核职责。
    /// </summary>
    public List<int> MergedFromSeqs { get; init; } = new();
}

/// <summary>流程实例化结果。</summary>
public sealed class FlowPlanResult
{
    public List<PlannedFlowTask> Tasks { get; set; } = new();

    /// <summary>实例化过程中的告警（如组织解析失败、被截断的节点）。</summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// 异动流程实例化器。
///
/// ★ 这是整套「可配置审批流」的核心算法，刻意做成**纯函数**：
///   输入节点定义 + 组织解析函数 + 审核终止级别，输出实际要执行的任务序列。
///   不依赖数据库、不依赖 ABP，因此可以用单元测试把各种合并场景钉死。
///
/// 三步：
///   1) 解析：把每个节点抽象的组织描述解析成真实组织；
///   2) 截断：丢弃审核级别高于「终止级别」的节点；
///   3) 合并：解析到**同一组织**的节点合并成一个待办，从而自动实现
///      「同县区转学县区只审一次」「同市转学市只审一次」——
///      这不是针对某个场景写的特例，而是去重的自然结果。
/// </summary>
public interface IChangeFlowPlanner
{
    FlowPlanResult Plan(
        IEnumerable<FlowNodeDefinition> nodes,
        Func<string, ResolvedOrg?> resolveOrg,
        string maxApproveLevel);
}

/// <inheritdoc />
public class ChangeFlowPlanner : IChangeFlowPlanner, ITransientDependency
{
    public FlowPlanResult Plan(
        IEnumerable<FlowNodeDefinition> nodes,
        Func<string, ResolvedOrg?> resolveOrg,
        string maxApproveLevel)
    {
        var result = new FlowPlanResult();

        var maxLevelOrder = ApproveLevels.GetOrder(maxApproveLevel);

        if (maxLevelOrder <= 0)
        {
            result.Warnings.Add($"审核终止级别「{maxApproveLevel}」不合法，已按校级处理。");
            maxLevelOrder = ApproveLevels.GetOrder(ApproveLevels.School);
        }

        // ---- 1) 解析 ----
        var resolved = new List<(FlowNodeDefinition Node, ResolvedOrg Org, string Level)>();

        foreach (var node in nodes.Where(n => n.IsEnabled).OrderBy(n => n.Seq))
        {
            var org = resolveOrg(node.OrgResolver);

            if (org == null)
            {
                result.Warnings.Add($"节点「{node.Name}」（序号 {node.Seq}）无法解析组织，已跳过。");
                continue;
            }

            var level = ToApproveLevel(org.OrgType);

            // ---- 2) 截断 ----
            if (ApproveLevels.GetOrder(level) > maxLevelOrder)
            {
                result.Warnings.Add(
                    $"节点「{node.Name}」（{ChangeOrgResolvers.GetDisplayName(node.OrgResolver)}）" +
                    $"超出审核终止级别「{ApproveLevels.GetDisplayName(maxApproveLevel)}」，已截断。");
                continue;
            }

            resolved.Add((node, org, level));
        }

        // ---- 3) 合并 ----
        // 按解析出的组织 Id 分组；同一组织只保留一个待办
        var groups = resolved.GroupBy(x => x.Org.Id).ToList();
        var tasks = new List<PlannedFlowTask>();

        foreach (var group in groups)
        {
            var members = group.OrderBy(x => x.Node.Seq).ToList();

            // 组内策略取「首个节点」的配置；同组节点应配置一致
            var strategy = members[0].Node.MergeStrategy;

            if (strategy == ChangeMergeStrategies.NoMerge)
            {
                tasks.AddRange(members.Select(m => ToTask(m.Node, m.Org, m.Level)));
                continue;
            }

            // MergeToLast（默认）：保留序号最大的节点，上级成为终审
            // MergeToFirst：保留序号最小的节点
            var survivor = strategy == ChangeMergeStrategies.MergeToFirst
                ? members.First()
                : members.Last();

            var mergedFrom = members
                .Where(m => m.Node.Seq != survivor.Node.Seq)
                .Select(m => m.Node.Seq)
                .ToList();

            tasks.Add(ToTask(survivor.Node, survivor.Org, survivor.Level, mergedFrom));
        }

        // ---- 输出按序号排序 ----
        result.Tasks = tasks.OrderBy(t => t.Seq).ToList();

        return result;
    }

    private static PlannedFlowTask ToTask(
        FlowNodeDefinition node,
        ResolvedOrg org,
        string level,
        List<int>? mergedFrom = null)
    {
        return new PlannedFlowTask
        {
            Seq = node.Seq,
            Name = node.Name,
            OrgId = org.Id,
            OrgCode = org.Code,
            OrgName = org.Name,
            RoleCode = node.RoleCode,
            ApproveLevel = level,
            AllowReject = node.AllowReject,
            TimeoutDays = node.TimeoutDays,
            MergedFromSeqs = mergedFrom ?? new List<int>()
        };
    }

    /// <summary>组织类型 → 审核级别。</summary>
    public static string ToApproveLevel(string orgType) => orgType switch
    {
        "Province" => ApproveLevels.Province,
        "City" => ApproveLevels.City,
        "County" => ApproveLevels.County,
        "School" => ApproveLevels.School,
        _ => ApproveLevels.School
    };
}
