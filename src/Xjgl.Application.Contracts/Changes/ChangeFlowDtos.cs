using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Xjgl.Changes;

/// <summary>提交异动申请。</summary>
public class SubmitChangeApplyDto
{
    [Required]
    public Guid ChangeTypeId { get; set; }

    [Required]
    public Guid StudentId { get; set; }

    /// <summary>目标学校（转入校）；与 <see cref="SourceSchoolOrgId"/> 相同表示校内异动。</summary>
    public Guid? TargetSchoolOrgId { get; set; }

    public Guid? TargetGradeId { get; set; }

    public Guid? TargetClassId { get; set; }

    [Required(ErrorMessage = "异动原因不能为空")]
    [StringLength(1024)]
    public string Reason { get; set; } = default!;

    [StringLength(1024)]
    public string? Remark { get; set; }
}

/// <summary>审核动作（通过 / 驳回 / 撤销审核）。</summary>
public class ChangeAuditActionDto
{
    [Required]
    public Guid ApplyId { get; set; }

    /// <summary>审核意见。</summary>
    [StringLength(1024)]
    public string? Opinion { get; set; }
}

/// <summary>异动申请列表项。</summary>
public class ChangeApplyListItemDto : EntityDto<Guid>
{
    public string ApplyNo { get; set; } = default!;
    public string ChangeTypeCode { get; set; } = default!;
    public string ChangeTypeName { get; set; } = default!;

    public Guid StudentId { get; set; }
    public string StudentName { get; set; } = default!;
    public string? StudentNo { get; set; }

    public DateTime ApplyDate { get; set; }
    public string Status { get; set; } = default!;
    public string StatusName { get; set; } = default!;

    public string? SourceSchoolName { get; set; }
    public string? TargetSchoolName { get; set; }

    public int? CurrentSeq { get; set; }
    public string? CurrentNodeName { get; set; }
    public string? CurrentOrgName { get; set; }

    public DateTime? FinalApprovedTime { get; set; }
}

/// <summary>异动申请详情（含流转轨迹）。</summary>
public class ChangeApplyDto : ChangeApplyListItemDto
{
    public Guid ChangeTypeId { get; set; }
    public Guid SourceSchoolOrgId { get; set; }
    public Guid? SourceGradeId { get; set; }
    public Guid? SourceClassId { get; set; }
    public string? SourceStatusCode { get; set; }
    public Guid? TargetSchoolOrgId { get; set; }
    public Guid? TargetGradeId { get; set; }
    public Guid? TargetClassId { get; set; }
    public string? TargetStatusCode { get; set; }
    public string? Reason { get; set; }
    public string? Remark { get; set; }
    public Guid FlowTemplateId { get; set; }
    public int FlowTemplateVersion { get; set; }

    public List<ChangeFlowTaskDto> Tasks { get; set; } = new();
    public List<ChangeFlowLogDto> Logs { get; set; } = new();
}

/// <summary>流程任务 DTO。</summary>
public class ChangeFlowTaskDto : EntityDto<Guid>
{
    public int Seq { get; set; }
    public string NodeName { get; set; } = default!;
    public Guid OrgId { get; set; }
    public string OrgName { get; set; } = default!;
    public string RoleCode { get; set; } = default!;
    public string ApproveLevel { get; set; } = default!;
    public string ApproveLevelName { get; set; } = default!;
    public string Status { get; set; } = default!;
    public string StatusName { get; set; } = default!;

    /// <summary>被合并进来的节点序号。</summary>
    public List<int> MergedFromSeqs { get; set; } = new();

    public string? HandledByName { get; set; }
    public DateTime? HandledTime { get; set; }
    public string? Opinion { get; set; }
    public DateTime? DueTime { get; set; }
}

/// <summary>流转日志 DTO。</summary>
public class ChangeFlowLogDto : EntityDto<Guid>
{
    public int? Seq { get; set; }
    public string Action { get; set; } = default!;
    public string? ActionName { get; set; }
    public string? OperatorName { get; set; }
    public string? Opinion { get; set; }
    public DateTime ActionTime { get; set; }
}

/// <summary>异动申请查询条件。</summary>
public class ChangeApplyQueryDto : PagedAndSortedResultRequestDto
{
    public Guid? ChangeTypeId { get; set; }
    public Guid? StudentId { get; set; }
    public string? Status { get; set; }

    /// <summary>按学校过滤（申请单的原学校或目标学校命中即可）。</summary>
    public Guid? SchoolOrgId { get; set; }

    public string? Keyword { get; set; }
}

/// <summary>待办查询条件。</summary>
public class ChangeTodoQueryDto : PagedAndSortedResultRequestDto
{
    /// <summary>只看某个组织的待办；为空表示全部。</summary>
    public Guid? OrgId { get; set; }

    /// <summary>只看某个角色编码的待办。</summary>
    public string? RoleCode { get; set; }
}

/// <summary>
/// 异动申请与审核。
///
/// 申请模块（添加/删除/查看/导出）与审核模块（审核/撤销审核/查看/导出）
/// 共用本服务，靠权限特性区分；前端按模块路径分别入口。
/// </summary>
public interface IChangeFlowAppService : IApplicationService
{
    // ---- 申请 ----

    /// <summary>
    /// 提交异动申请。
    /// 会依次校验：异动类型是否启用、是否在允许申请的时间窗口内、学生年级是否在允许范围、
    /// 是否允许跨校；然后按已发布的流程模板实例化出待办节点。
    /// </summary>
    Task<ChangeApplyDto> SubmitAsync(SubmitChangeApplyDto input);

    Task<PagedResultDto<ChangeApplyListItemDto>> GetAppliesAsync(ChangeApplyQueryDto input);

    Task<ChangeApplyDto> GetApplyAsync(Guid id);

    /// <summary>撤回申请（仅发起方、且首个节点尚未处理）。</summary>
    Task<ChangeApplyDto> WithdrawAsync(Guid id);

    /// <summary>删除申请（仅本人草稿/已撤回单据；已进入流程的单据不允许删除）。</summary>
    Task DeleteAsync(Guid id);

    // ---- 审核 ----

    /// <summary>我的待办。</summary>
    Task<PagedResultDto<ChangeApplyListItemDto>> GetTodoListAsync(ChangeTodoQueryDto input);

    /// <summary>审核通过当前节点；若是最后一个节点则完成整个流程并执行学籍变更。</summary>
    Task<ChangeApplyDto> ApproveAsync(ChangeAuditActionDto input);

    /// <summary>驳回当前节点（整个流程结束）。</summary>
    Task<ChangeApplyDto> RejectAsync(ChangeAuditActionDto input);

    /// <summary>
    /// 撤销审核：把当前（或最后已处理的）节点退回「待处理」。
    /// 限制：后续节点均未处理；若整个流程已通过，会同时**回滚学籍变更**。
    /// </summary>
    Task<ChangeApplyDto> RevokeApprovalAsync(ChangeAuditActionDto input);
}
