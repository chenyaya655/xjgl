using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Changes;

/// <summary>流程模板状态。</summary>
public static class ChangeFlowTemplateStatus
{
    public const string Draft = "Draft";
    public const string Published = "Published";
    public const string Disabled = "Disabled";

    public static string GetDisplayName(string status) => status switch
    {
        Draft => "草稿",
        Published => "已发布",
        Disabled => "已停用",
        _ => status
    };
}

/// <summary>
/// 异动类型。
///
/// ★ 这是「业务人员不改代码就能调整异动规则」的入口，承载三类控制：
///   1) **审核级别控制** <see cref="MaxApproveLevel"/>：审到哪一级就算结束（校/县/市/省）；
///      高于该级别的流程节点会被实例化器截断。
///   2) **异动时间控制** <see cref="ApplyWindowStart"/> / <see cref="ApplyWindowEnd"/>：
///      允许申请的日期区间（MM-dd），例如转学只允许 09-01 ~ 12-31。
///   3) **年级控制** <see cref="GradeScope"/>：允许申请的年级码白名单，例如禁止毕业年级转学。
///
/// 表名：stu_change_type
/// </summary>
public class StuChangeType : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>异动代码，如 TRANSFER / SUSPEND / RESUME。</summary>
    public string Code { get; set; } = default!;

    /// <summary>异动名称，如「转学」「休学」。</summary>
    public string Name { get; set; } = default!;

    /// <summary>异动分类码（转学类 / 休复类 / 退学类 / 毕业类）。</summary>
    public string? CategoryCode { get; set; }

    /// <summary>异动前的学籍状态码（微代码 STUDENT_STATUS）。</summary>
    public string? BeforeStatusCode { get; set; }

    /// <summary>异动后的学籍状态码。</summary>
    public string? AfterStatusCode { get; set; }

    /// <summary>审核终止级别，取值见 <see cref="ApproveLevels"/>。</summary>
    public string MaxApproveLevel { get; set; } = ApproveLevels.City;

    /// <summary>是否需要对方学校确认（跨校转学为 true）。</summary>
    public bool NeedPeerSchoolConfirm { get; set; }

    /// <summary>是否影响年级（如升学、跳级）。</summary>
    public bool AffectGrade { get; set; }

    /// <summary>是否影响班级。</summary>
    public bool AffectClass { get; set; }

    /// <summary>是否允许跨校。</summary>
    public bool AllowCrossSchool { get; set; } = true;

    /// <summary>允许申请的起始日期（MM-dd）；为空表示不限制。</summary>
    public string? ApplyWindowStart { get; set; }

    /// <summary>允许申请的结束日期（MM-dd）；为空表示不限制。</summary>
    public string? ApplyWindowEnd { get; set; }

    /// <summary>是否允许跨学年申请。</summary>
    public bool AllowCrossAcademicYear { get; set; }

    /// <summary>每学年可申请次数上限；0 表示不限制。</summary>
    public int MaxApplyTimesPerYear { get; set; }

    /// <summary>允许申请的年级码白名单，逗号分隔；为空表示不限。</summary>
    public string? GradeScope { get; set; }

    /// <summary>是否必须上传证明材料。</summary>
    public bool RequireAttachment { get; set; }

    /// <summary>当前启用的流程模板（冗余，便于列表展示）。</summary>
    public Guid? FlowTemplateId { get; set; }

    public int Sort { get; set; }

    public bool IsEnabled { get; set; } = true;

    public string? Remark { get; set; }

    public StuChangeType()
    {
    }

    public StuChangeType(Guid id, string code, string name, string maxApproveLevel) : base(id)
    {
        Code = code;
        Name = name;
        MaxApproveLevel = maxApproveLevel;
    }

    public string[] GetAllowedGradeCodes()
    {
        return string.IsNullOrWhiteSpace(GradeScope)
            ? Array.Empty<string>()
            : GradeScope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// 判断某个日期是否落在允许申请的时间窗口内。
    /// 窗口以 MM-dd 表示、按年循环，因此能直接比较「月日」；跨年窗口（如 09-01 ~ 次年 01-31）也支持。
    /// </summary>
    public bool IsInApplyWindow(DateTime date)
    {
        if (ApplyWindowStart.IsNullOrWhiteSpace() && ApplyWindowEnd.IsNullOrWhiteSpace())
        {
            return true;
        }

        if (!TryParseMonthDay(ApplyWindowStart, out var start) ||
            !TryParseMonthDay(ApplyWindowEnd, out var end))
        {
            return true;
        }

        var current = date.Month * 100 + date.Day;
        var startValue = start.Month * 100 + start.Day;
        var endValue = end.Month * 100 + end.Day;

        return startValue <= endValue
            ? current >= startValue && current <= endValue
            : current >= startValue || current <= endValue; // 跨年窗口
    }

    /// <summary>判断某个年级码是否允许申请。</summary>
    public bool IsGradeAllowed(string? gradeCode)
    {
        var allowed = GetAllowedGradeCodes();

        if (allowed.Length == 0)
        {
            return true;
        }

        if (gradeCode.IsNullOrWhiteSpace())
        {
            // 未指定年级时从严：有白名单就必须能匹配上
            return false;
        }

        return allowed.Contains(gradeCode, StringComparer.Ordinal);
    }

    private static bool TryParseMonthDay(string? value, out (int Month, int Day) result)
    {
        result = default;

        if (value.IsNullOrWhiteSpace())
        {
            return false;
        }

        var parts = value!.Split('-');

        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var month) ||
            !int.TryParse(parts[1], out var day) ||
            month is < 1 or > 12 || day is < 1 or > 31)
        {
            return false;
        }

        result = (month, day);
        return true;
    }
}

/// <summary>
/// 异动流程模板。
///
/// 版本化：修改流程后发布新版本，只对新单据生效；
/// 在途单据通过流程任务上的节点快照保证不受影响。
///
/// 表名：stu_change_flow_template
/// </summary>
public class StuChangeFlowTemplate : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    public Guid ChangeTypeId { get; set; }

    public string Name { get; set; } = default!;

    /// <summary>版本号，同一异动类型下递增。</summary>
    public int Version { get; set; } = 1;

    /// <summary>状态，取值见 <see cref="ChangeFlowTemplateStatus"/>。</summary>
    public string Status { get; set; } = ChangeFlowTemplateStatus.Draft;

    public DateTime? PublishedTime { get; set; }

    public string? Remark { get; set; }

    public StuChangeFlowTemplate()
    {
    }

    public StuChangeFlowTemplate(Guid id, Guid changeTypeId, string name, int version) : base(id)
    {
        ChangeTypeId = changeTypeId;
        Name = name;
        Version = version;
    }
}

/// <summary>
/// 异动流程节点定义。
///
/// ★ 节点不写死具体组织，而是用 <see cref="OrgResolver"/> 做「相对描述」
///   （转入学校所在县区、转出学校所在地市…），实例化时再解析成真实组织，
///   从而自动实现「同一教育局不重复审核」。
///
/// 表名：stu_change_flow_node
/// </summary>
public class StuChangeFlowNode : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    public Guid TemplateId { get; set; }

    /// <summary>节点序号，从 1 开始，决定先后顺序。</summary>
    public int Seq { get; set; }

    /// <summary>节点名称，如「转入县区审核」。</summary>
    public string NodeName { get; set; } = default!;

    /// <summary>组织解析器，取值见 <see cref="ChangeOrgResolvers"/>。</summary>
    public string OrgResolver { get; set; } = default!;

    /// <summary>审核角色编码（如 SchoolAdmin / CountyAdmin / CityAdmin / ProvinceAdmin）。</summary>
    public string RoleCode { get; set; } = default!;

    /// <summary>合并策略，取值见 <see cref="ChangeMergeStrategies"/>。</summary>
    public string MergeStrategy { get; set; } = ChangeMergeStrategies.MergeToLast;

    public bool AllowReject { get; set; } = true;

    public bool AllowDelegate { get; set; }

    /// <summary>超时天数；0 表示不限制。</summary>
    public int TimeoutDays { get; set; }

    public bool IsEnabled { get; set; } = true;

    public string? Remark { get; set; }

    public StuChangeFlowNode()
    {
    }

    public StuChangeFlowNode(
        Guid id,
        Guid templateId,
        int seq,
        string nodeName,
        string orgResolver,
        string roleCode,
        string mergeStrategy = ChangeMergeStrategies.MergeToLast) : base(id)
    {
        TemplateId = templateId;
        Seq = seq;
        NodeName = nodeName;
        OrgResolver = orgResolver;
        RoleCode = roleCode;
        MergeStrategy = mergeStrategy;
    }

    /// <summary>转换为纯算法所需的节点定义。</summary>
    public FlowNodeDefinition ToDefinition()
    {
        return new FlowNodeDefinition
        {
            Seq = Seq,
            Name = NodeName,
            OrgResolver = OrgResolver,
            RoleCode = RoleCode,
            MergeStrategy = MergeStrategy,
            AllowReject = AllowReject,
            TimeoutDays = TimeoutDays,
            IsEnabled = IsEnabled
        };
    }
}
