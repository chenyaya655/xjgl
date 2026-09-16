using Volo.Abp.Application.Services;

namespace Xjgl.Changes;

/// <summary>
/// 异动类型与流程模板管理。
///
/// 业务人员在这里完成三件事，全程不需要改代码、不需要重启：
///   1) 配置异动类型：审核终止级别、申请时间窗口、允许的年级范围；
///   2) 编排流程节点：用「组织解析器」描述每一步审的是哪一级；
///   3) 用流程预览确认合并结果（例如同县区转学会合并成几步）。
/// </summary>
public interface IChangeTypeAppService : IApplicationService
{
    // ---- 异动类型 ----

    Task<List<ChangeTypeDto>> GetTypesAsync(bool onlyEnabled = false);

    Task<ChangeTypeDto> GetTypeAsync(Guid id);

    Task<ChangeTypeDto> CreateTypeAsync(CreateUpdateChangeTypeDto input);

    Task<ChangeTypeDto> UpdateTypeAsync(Guid id, CreateUpdateChangeTypeDto input);

    /// <summary>删除异动类型（已有流程模板或申请单时拒绝）。</summary>
    Task DeleteTypeAsync(Guid id);

    // ---- 流程模板 ----

    /// <summary>取该异动类型当前启用的流程模板（含节点）；没有则返回 null。</summary>
    Task<ChangeFlowTemplateDto?> GetTemplateAsync(Guid changeTypeId);

    /// <summary>保存流程（整体覆盖节点）。未发布前为草稿。</summary>
    Task<ChangeFlowTemplateDto> SaveTemplateAsync(SaveFlowTemplateDto input);

    /// <summary>发布流程。发布后新单据按此版本执行，在途单据不受影响。</summary>
    Task<ChangeFlowTemplateDto> PublishTemplateAsync(Guid templateId);

    // ---- 流程预览 ----

    /// <summary>
    /// 流程预览：给定模拟的目标学校与原学校，返回**实际会执行**的节点序列
    /// （已按审核终止级别截断、并已按组织去重合并）。
    /// </summary>
    Task<PreviewFlowResultDto> PreviewFlowAsync(PreviewFlowDto input);
}
