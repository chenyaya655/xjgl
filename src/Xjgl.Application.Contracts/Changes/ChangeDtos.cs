using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Dtos;

namespace Xjgl.Changes;

/// <summary>异动类型 DTO。</summary>
public class ChangeTypeDto : EntityDto<Guid>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? CategoryCode { get; set; }
    public string? BeforeStatusCode { get; set; }
    public string? AfterStatusCode { get; set; }

    public string MaxApproveLevel { get; set; } = default!;
    public string MaxApproveLevelName { get; set; } = default!;

    public bool NeedPeerSchoolConfirm { get; set; }
    public bool AffectGrade { get; set; }
    public bool AffectClass { get; set; }
    public bool AllowCrossSchool { get; set; }

    public string? ApplyWindowStart { get; set; }
    public string? ApplyWindowEnd { get; set; }
    public bool AllowCrossAcademicYear { get; set; }
    public int MaxApplyTimesPerYear { get; set; }
    public string? GradeScope { get; set; }

    /// <summary>
    /// 是否必须上传证明材料。
    ///
    /// 校验点在**批准前**（第一次批准），不是提交时——理由见
    /// <c>StuChangeType.RequireAttachment</c> 的说明。
    /// </summary>
    public bool RequireAttachment { get; set; }

    public Guid? FlowTemplateId { get; set; }

    /// <summary>当前是否有已发布的流程模板。</summary>
    public bool HasPublishedFlow { get; set; }

    public int Sort { get; set; }
    public bool IsEnabled { get; set; }
    public string? Remark { get; set; }
}

/// <summary>新增 / 修改异动类型。</summary>
public class CreateUpdateChangeTypeDto
{
    [Required(ErrorMessage = "异动代码不能为空")]
    [StringLength(32)]
    public string Code { get; set; } = default!;

    [Required(ErrorMessage = "异动名称不能为空")]
    [StringLength(64)]
    public string Name { get; set; } = default!;

    [StringLength(32)]
    public string? CategoryCode { get; set; }

    [StringLength(8)]
    public string? BeforeStatusCode { get; set; }

    [StringLength(8)]
    public string? AfterStatusCode { get; set; }

    /// <summary>审核终止级别：School / County / City / Province。</summary>
    [Required(ErrorMessage = "审核终止级别不能为空")]
    [StringLength(16)]
    public string MaxApproveLevel { get; set; } = ApproveLevels.City;

    public bool NeedPeerSchoolConfirm { get; set; }
    public bool AffectGrade { get; set; }
    public bool AffectClass { get; set; }
    public bool AllowCrossSchool { get; set; } = true;

    /// <summary>允许申请的起始日期，MM-dd（如 09-01）。</summary>
    [RegularExpression(@"^(\d{2}-\d{2})?$", ErrorMessage = "日期窗口格式应为 MM-dd，如 09-01")]
    public string? ApplyWindowStart { get; set; }

    /// <summary>允许申请的结束日期，MM-dd（如 12-31）。</summary>
    [RegularExpression(@"^(\d{2}-\d{2})?$", ErrorMessage = "日期窗口格式应为 MM-dd，如 12-31")]
    public string? ApplyWindowEnd { get; set; }

    public bool AllowCrossAcademicYear { get; set; }

    [Range(0, 99)]
    public int MaxApplyTimesPerYear { get; set; }

    /// <summary>允许申请的年级码白名单，逗号分隔。</summary>
    [StringLength(256)]
    public string? GradeScope { get; set; }

    // 是否必须上传证明材料：配置入口在这里，真正的拦截在第一次批准时
    // （见 StuChangeType.RequireAttachment 的说明）。
    public bool RequireAttachment { get; set; }

    public int Sort { get; set; }
    public bool IsEnabled { get; set; } = true;

    [StringLength(512)]
    public string? Remark { get; set; }
}

/// <summary>流程节点 DTO。</summary>
public class ChangeFlowNodeDto : EntityDto<Guid>
{
    public Guid TemplateId { get; set; }
    public int Seq { get; set; }
    public string NodeName { get; set; } = default!;
    public string OrgResolver { get; set; } = default!;
    public string OrgResolverName { get; set; } = default!;
    public string RoleCode { get; set; } = default!;
    public string MergeStrategy { get; set; } = default!;
    public bool AllowReject { get; set; }
    public int TimeoutDays { get; set; }
    public bool IsEnabled { get; set; }
    public string? Remark { get; set; }
}

/// <summary>新增 / 修改流程节点。</summary>
public class CreateUpdateFlowNodeDto
{
    [Range(1, 999)]
    public int Seq { get; set; }

    [Required(ErrorMessage = "节点名称不能为空")]
    [StringLength(64)]
    public string NodeName { get; set; } = default!;

    [Required(ErrorMessage = "组织解析器不能为空")]
    [StringLength(32)]
    public string OrgResolver { get; set; } = default!;

    [Required(ErrorMessage = "审核角色不能为空")]
    [StringLength(32)]
    public string RoleCode { get; set; } = default!;

    [StringLength(32)]
    public string MergeStrategy { get; set; } = ChangeMergeStrategies.MergeToLast;

    /// <summary>
    /// 该节点是否允许驳回（默认允许）。
    ///
    /// 关闭后，审核人在这个节点只能「通过」，驳回会被后端拒绝（`Xjgl:00608`）。
    /// 适用场景：只负责登记备案、不做出实质判断的节点。
    ///
    /// 注意：这里曾有一个 `AllowDelegate`（允许委托他人审核），
    /// 它既没有配置入口、也没有任何代码读它，而**权限委派功能本身尚未实现** ——
    /// 已随本轮清理删除。要实现委派时再加回来（见 ROADMAP「权限委派」）。
    /// </summary>
    public bool AllowReject { get; set; } = true;

    [Range(0, 365)]
    public int TimeoutDays { get; set; }
}

/// <summary>流程模板 DTO。</summary>
public class ChangeFlowTemplateDto : EntityDto<Guid>
{
    public Guid ChangeTypeId { get; set; }
    public string Name { get; set; } = default!;
    public int Version { get; set; }
    public string Status { get; set; } = default!;
    public string StatusName { get; set; } = default!;
    public DateTime? PublishedTime { get; set; }
    public string? Remark { get; set; }

    public List<ChangeFlowNodeDto> Nodes { get; set; } = new();
}

/// <summary>保存流程模板（整体覆盖节点）。</summary>
public class SaveFlowTemplateDto
{
    [Required]
    public Guid ChangeTypeId { get; set; }

    [Required(ErrorMessage = "模板名称不能为空")]
    [StringLength(64)]
    public string Name { get; set; } = default!;

    [StringLength(512)]
    public string? Remark { get; set; }

    [MinLength(1, ErrorMessage = "流程至少要有一个节点")]
    public List<CreateUpdateFlowNodeDto> Nodes { get; set; } = new();
}

/// <summary>流程预览请求：给一个模拟学生与目标学校，看实际会走几步。</summary>
public class PreviewFlowDto
{
    [Required]
    public Guid ChangeTypeId { get; set; }

    /// <summary>目标学校（转入校）；休学等无转入校时可与 <see cref="SourceSchoolOrgId"/> 相同。</summary>
    [Required]
    public Guid TargetSchoolOrgId { get; set; }

    /// <summary>原学校（转出校）。</summary>
    public Guid? SourceSchoolOrgId { get; set; }
}

/// <summary>预览出的单个节点。</summary>
public class PreviewFlowNodeDto
{
    public int Seq { get; set; }
    public string NodeName { get; set; } = default!;
    public Guid OrgId { get; set; }
    public string OrgName { get; set; } = default!;
    public string OrgResolver { get; set; } = default!;
    public string OrgResolverName { get; set; } = default!;
    public string RoleCode { get; set; } = default!;
    public string ApproveLevel { get; set; } = default!;
    public string ApproveLevelName { get; set; } = default!;

    /// <summary>被合并进来的节点序号。</summary>
    public List<int> MergedFromSeqs { get; set; } = new();

    /// <summary>合并说明，如「已与「转入县区审核」合并为一次审核」。</summary>
    public string? MergeNote { get; set; }
}

/// <summary>流程预览结果。</summary>
public class PreviewFlowResultDto
{
    public Guid ChangeTypeId { get; set; }
    public string ChangeTypeCode { get; set; } = default!;
    public string ChangeTypeName { get; set; } = default!;
    public string MaxApproveLevel { get; set; } = default!;
    public string MaxApproveLevelName { get; set; } = default!;
    public int TemplateVersion { get; set; }

    /// <summary>配置的节点总数（未截断、未合并）。</summary>
    public int ConfiguredNodeCount { get; set; }

    /// <summary>实际执行步数。</summary>
    public int ActualStepCount { get; set; }

    public List<PreviewFlowNodeDto> Nodes { get; set; } = new();

    public List<string> Warnings { get; set; } = new();
}
