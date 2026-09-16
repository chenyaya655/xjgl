using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Changes;

/// <summary>异动申请状态。</summary>
public static class ChangeApplyStatus
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string InProgress = "InProgress";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Withdrawn = "Withdrawn";
    public const string Archived = "Archived";

    public static string GetDisplayName(string status) => status switch
    {
        Draft => "草稿",
        Submitted => "待审核",
        InProgress => "审核中",
        Approved => "已通过",
        Rejected => "已驳回",
        Withdrawn => "已撤回",
        Archived => "已归档",
        _ => status
    };
}

/// <summary>流程任务状态。</summary>
public static class ChangeTaskStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Skipped = "Skipped";
    public const string Revoked = "Revoked";

    public static string GetDisplayName(string status) => status switch
    {
        Pending => "待处理",
        Approved => "已通过",
        Rejected => "已驳回",
        Skipped => "已跳过",
        Revoked => "已撤销",
        _ => status
    };
}

/// <summary>流转动作。</summary>
public static class ChangeFlowActions
{
    public const string Submit = "Submit";
    public const string Approve = "Approve";
    public const string Reject = "Reject";
    public const string Revoke = "Revoke";
    public const string Withdraw = "Withdraw";
    public const string Archive = "Archive";
}

/// <summary>
/// 学生异动申请单。
///
/// 单据上保存了「变动前 / 变动后」的完整快照，用途有二：
///   1) 审核通过后据此执行学籍变更；
///   2) **撤销审核**时据此回滚（把学生恢复到变动前的状态）。
///
/// 表名：stu_change_apply
/// </summary>
public class StuChangeApply : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>申请单号。</summary>
    public string ApplyNo { get; set; } = default!;

    public Guid ChangeTypeId { get; set; }

    /// <summary>冗余异动类型编码与名称，列表展示用，避免联表。</summary>
    public string ChangeTypeCode { get; set; } = default!;

    public string ChangeTypeName { get; set; } = default!;

    public Guid StudentId { get; set; }
    public string StudentName { get; set; } = default!;
    public string? StudentNo { get; set; }

    public DateTime ApplyDate { get; set; }

    // ---- 变动前 ----
    public Guid SourceSchoolOrgId { get; set; }
    public Guid? SourceGradeId { get; set; }
    public Guid? SourceClassId { get; set; }
    public string? SourceStatusCode { get; set; }

    // ---- 变动后 ----
    public Guid? TargetSchoolOrgId { get; set; }
    public Guid? TargetGradeId { get; set; }
    public Guid? TargetClassId { get; set; }
    public string? TargetStatusCode { get; set; }

    /// <summary>异动原因。</summary>
    public string? Reason { get; set; }

    public string Status { get; set; } = ChangeApplyStatus.Submitted;

    /// <summary>当前待处理节点序号。</summary>
    public int? CurrentSeq { get; set; }

    public Guid FlowTemplateId { get; set; }

    public int FlowTemplateVersion { get; set; }

    public DateTime? FinalApprovedTime { get; set; }

    public string? Remark { get; set; }

    public StuChangeApply()
    {
    }

    public StuChangeApply(Guid id, string applyNo, Guid changeTypeId, Guid studentId, string studentName) : base(id)
    {
        ApplyNo = applyNo;
        ChangeTypeId = changeTypeId;
        StudentId = studentId;
        StudentName = studentName;
    }

    /// <summary>是否处于可继续审核的状态。</summary>
    public bool IsAuditable()
    {
        return Status is ChangeApplyStatus.Submitted or ChangeApplyStatus.InProgress;
    }
}

/// <summary>
/// 流程任务（= 某个节点在某个组织上的一次待办）。
///
/// 实例化时会把节点定义**快照**到任务上（节点名、审批级别等），
/// 因此流程模板改版只影响新单据，在途单据不受影响。
///
/// 表名：stu_change_flow_task
/// </summary>
public class StuChangeFlowTask : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    public Guid ApplyId { get; set; }

    public int Seq { get; set; }

    public string NodeName { get; set; } = default!;

    public Guid OrgId { get; set; }

    public string OrgName { get; set; } = default!;

    public string RoleCode { get; set; } = default!;

    public string ApproveLevel { get; set; } = default!;

    /// <summary>被合并进来的节点序号，逗号分隔（如 "2,5"）。</summary>
    public string? MergedFromSeqs { get; set; }

    public string Status { get; set; } = ChangeTaskStatus.Pending;

    public Guid? HandledByUserId { get; set; }

    public string? HandledByName { get; set; }

    public DateTime? HandledTime { get; set; }

    public string? Opinion { get; set; }

    public DateTime? DueTime { get; set; }

    public StuChangeFlowTask()
    {
    }

    public StuChangeFlowTask(
        Guid id,
        Guid applyId,
        int seq,
        string nodeName,
        Guid orgId,
        string orgName,
        string roleCode,
        string approveLevel) : base(id)
    {
        ApplyId = applyId;
        Seq = seq;
        NodeName = nodeName;
        OrgId = orgId;
        OrgName = orgName;
        RoleCode = roleCode;
        ApproveLevel = approveLevel;
    }

    public int[] GetMergedFromSeqs()
    {
        return string.IsNullOrWhiteSpace(MergedFromSeqs)
            ? Array.Empty<int>()
            : MergedFromSeqs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var v) ? v : 0)
                .Where(v => v > 0)
                .ToArray();
    }
}

/// <summary>
/// 流转日志（不可变更的流水，供时间轴展示与审计）。
///
/// 表名：stu_change_flow_log
/// </summary>
public class StuChangeFlowLog : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    public Guid ApplyId { get; set; }

    public Guid? TaskId { get; set; }

    public int? Seq { get; set; }

    /// <summary>动作，取值见 <see cref="ChangeFlowActions"/>。</summary>
    public string Action { get; set; } = default!;

    public Guid? OperatorUserId { get; set; }

    public string? OperatorName { get; set; }

    public Guid? OperatorOrgId { get; set; }

    public string? Opinion { get; set; }

    public DateTime ActionTime { get; set; }

    public string? ClientIp { get; set; }

    public StuChangeFlowLog()
    {
    }

    public StuChangeFlowLog(Guid id, Guid applyId, string action, DateTime actionTime) : base(id)
    {
        ApplyId = applyId;
        Action = action;
        ActionTime = actionTime;
    }
}
