using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Timing;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Permissions;

namespace Xjgl.Changes;

/// <summary>
/// 异动类型与流程模板管理实现。
/// </summary>
[Authorize]
public class ChangeTypeAppService : XjglAppService, IChangeTypeAppService
{
    private readonly IXjglRepository<StuChangeType> _types;
    private readonly IXjglRepository<StuChangeFlowTemplate> _templates;
    private readonly IXjglRepository<StuChangeFlowNode> _nodes;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IChangeFlowPlanner _planner;
    private readonly IClock _clock;

    public ChangeTypeAppService(
        IXjglRepository<StuChangeType> types,
        IXjglRepository<StuChangeFlowTemplate> templates,
        IXjglRepository<StuChangeFlowNode> nodes,
        IXjglRepository<SysOrg> orgs,
        IChangeFlowPlanner planner,
        IClock clock)
    {
        _types = types;
        _templates = templates;
        _nodes = nodes;
        _orgs = orgs;
        _planner = planner;
        _clock = clock;
    }

    // ------------------------------------------------------------------
    // 异动类型
    // ------------------------------------------------------------------

    public async Task<List<ChangeTypeDto>> GetTypesAsync(bool onlyEnabled = false)
    {
        var types = await _types.GetSortedListAsync(
            onlyEnabled ? t => t.IsEnabled : null, "Sort ASC, Code ASC");

        return types.Select(ToTypeDto).ToList();
    }

    public async Task<ChangeTypeDto> GetTypeAsync(Guid id)
    {
        return ToTypeDto(await _types.GetAsync(id));
    }

    [Authorize(MetadataPermissions.Chg.Type.Create)]
    public async Task<ChangeTypeDto> CreateTypeAsync(CreateUpdateChangeTypeDto input)
    {
        var code = input.Code.Trim();

        if (await _types.AnyAsync(t => t.Code == code))
        {
            throw new BusinessException(XjglDomainErrorCodes.DuplicateData).WithData("code", code);
        }

        if (!ApproveLevels.IsValid(input.MaxApproveLevel))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", $"未知的审核级别「{input.MaxApproveLevel}」");
        }

        var entity = new StuChangeType(GuidGenerator.Create(), code, input.Name.Trim(), input.MaxApproveLevel);

        ApplyTypeInput(entity, input);

        await _types.InsertAsync(entity, autoSave: true);

        return ToTypeDto(entity);
    }

    [Authorize(MetadataPermissions.Chg.Type.Update)]
    public async Task<ChangeTypeDto> UpdateTypeAsync(Guid id, CreateUpdateChangeTypeDto input)
    {
        var entity = await _types.GetAsync(id);

        if (!ApproveLevels.IsValid(input.MaxApproveLevel))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", $"未知的审核级别「{input.MaxApproveLevel}」");
        }

        entity.Name = input.Name.Trim();
        entity.MaxApproveLevel = input.MaxApproveLevel;

        ApplyTypeInput(entity, input, keepCode: true);

        await _types.UpdateAsync(entity, autoSave: true);

        return ToTypeDto(entity);
    }

    [Authorize(MetadataPermissions.Chg.Type.Delete)]
    public async Task DeleteTypeAsync(Guid id)
    {
        var entity = await _types.GetAsync(id);

        if (await _templates.AnyAsync(t => t.ChangeTypeId == id))
        {
            throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                .WithData("reason", "该异动类型下已有流程模板，请先删除流程");
        }

        // 说明：异动申请模块（下一步）完成后，这里要补上「已有申请单则拒绝删除」
        await _types.DeleteAsync(entity, autoSave: true);
    }

    // ------------------------------------------------------------------
    // 流程模板
    // ------------------------------------------------------------------

    public async Task<ChangeFlowTemplateDto?> GetTemplateAsync(Guid changeTypeId)
    {
        var template = await _templates.FindAsync(t =>
            t.ChangeTypeId == changeTypeId && t.Status == ChangeFlowTemplateStatus.Published);

        template ??= await _templates.FindAsync(t => t.ChangeTypeId == changeTypeId);

        if (template == null)
        {
            return null;
        }

        return await ToTemplateDtoAsync(template);
    }

    [Authorize(MetadataPermissions.Chg.Type.Create)]
    public async Task<ChangeFlowTemplateDto> SaveTemplateAsync(SaveFlowTemplateDto input)
    {
        var changeType = await _types.GetAsync(input.ChangeTypeId);

        await ValidateNodesAsync(input.Nodes);

        // 一个异动类型只保留一个草稿模板；已发布的不动
        var draft = await _templates.FindAsync(t =>
            t.ChangeTypeId == input.ChangeTypeId && t.Status == ChangeFlowTemplateStatus.Draft);

        if (draft == null)
        {
            var maxVersion = (await _templates.GetListAsync(t => t.ChangeTypeId == input.ChangeTypeId))
                .Select(t => t.Version)
                .DefaultIfEmpty(0)
                .Max();

            draft = new StuChangeFlowTemplate(
                GuidGenerator.Create(), input.ChangeTypeId, input.Name.Trim(), maxVersion + 1);

            await _templates.InsertAsync(draft, autoSave: true);
        }

        draft.Name = input.Name.Trim();
        draft.Remark = input.Remark;

        await _templates.UpdateAsync(draft, autoSave: true);

        // 整体覆盖节点
        var existingNodes = await _nodes.GetListAsync(n => n.TemplateId == draft.Id);
        if (existingNodes.Count > 0)
        {
            await _nodes.DeleteManyAsync(existingNodes.Select(n => n.Id), autoSave: true);
        }

        var order = 0;
        foreach (var nodeInput in input.Nodes.OrderBy(n => n.Seq))
        {
            order++;

            var node = new StuChangeFlowNode(
                GuidGenerator.Create(),
                draft.Id,
                nodeInput.Seq,
                nodeInput.NodeName.Trim(),
                nodeInput.OrgResolver,
                nodeInput.RoleCode,
                nodeInput.MergeStrategy)
            {
                AllowReject = nodeInput.AllowReject,
                AllowDelegate = nodeInput.AllowDelegate,
                TimeoutDays = nodeInput.TimeoutDays
            };

            await _nodes.InsertAsync(node, autoSave: true);
        }

        // 便于列表展示
        changeType.FlowTemplateId = draft.Id;
        await _types.UpdateAsync(changeType, autoSave: true);

        return await ToTemplateDtoAsync(draft);
    }

    [Authorize(MetadataPermissions.Chg.Type.Update)]
    public async Task<ChangeFlowTemplateDto> PublishTemplateAsync(Guid templateId)
    {
        var template = await _templates.GetAsync(templateId);

        var nodeCount = await _nodes.GetCountAsync(n => n.TemplateId == templateId);
        if (nodeCount == 0)
        {
            throw new BusinessException(XjglDomainErrorCodes.ChangeFlowNotConfigured)
                .WithData("reason", "流程模板没有任何节点，不能发布");
        }

        // 同类型的旧发布版本停用
        var published = await _templates.GetListAsync(t =>
            t.ChangeTypeId == template.ChangeTypeId
            && t.Status == ChangeFlowTemplateStatus.Published
            && t.Id != templateId);

        foreach (var old in published)
        {
            old.Status = ChangeFlowTemplateStatus.Disabled;
        }

        if (published.Count > 0)
        {
            await _templates.UpdateManyAsync(published, autoSave: true);
        }

        template.Status = ChangeFlowTemplateStatus.Published;
        template.PublishedTime = _clock.Now;

        await _templates.UpdateAsync(template, autoSave: true);

        return await ToTemplateDtoAsync(template);
    }

    // ------------------------------------------------------------------
    // 流程预览（★ 让业务人员一眼看到合并结果）
    // ------------------------------------------------------------------

    public async Task<PreviewFlowResultDto> PreviewFlowAsync(PreviewFlowDto input)
    {
        var changeType = await _types.GetAsync(input.ChangeTypeId);

        var template = await _templates.FindAsync(t =>
            t.ChangeTypeId == input.ChangeTypeId && t.Status == ChangeFlowTemplateStatus.Published);

        if (template == null)
        {
            throw new BusinessException(XjglDomainErrorCodes.ChangeFlowNotConfigured)
                .WithData("reason", "该异动类型还没有已发布的流程模板");
        }

        var nodes = await _nodes.GetSortedListAsync(
            n => n.TemplateId == template.Id, "Seq ASC");

        var resolver = await BuildOrgResolverAsync(input.TargetSchoolOrgId, input.SourceSchoolOrgId);

        var plan = _planner.Plan(nodes.Select(n => n.ToDefinition()), resolver, changeType.MaxApproveLevel);

        var nodeBySeq = nodes.ToDictionary(n => n.Seq);

        var previewNodes = plan.Tasks.Select(task =>
        {
            var node = nodeBySeq[task.Seq];

            var mergeNote = task.MergedFromSeqs.Count == 0
                ? null
                : "已与 " + string.Join("、", task.MergedFromSeqs
                    .Where(nodeBySeq.ContainsKey)
                    .Select(seq => $"「{nodeBySeq[seq].NodeName}」")) + " 合并为一次审核";

            return new PreviewFlowNodeDto
            {
                Seq = task.Seq,
                NodeName = task.Name,
                OrgId = task.OrgId,
                OrgName = task.OrgName,
                OrgResolver = node.OrgResolver,
                OrgResolverName = ChangeOrgResolvers.GetDisplayName(node.OrgResolver),
                RoleCode = task.RoleCode,
                ApproveLevel = task.ApproveLevel,
                ApproveLevelName = ApproveLevels.GetDisplayName(task.ApproveLevel),
                MergedFromSeqs = task.MergedFromSeqs,
                MergeNote = mergeNote
            };
        }).ToList();

        return new PreviewFlowResultDto
        {
            ChangeTypeId = changeType.Id,
            ChangeTypeCode = changeType.Code,
            ChangeTypeName = changeType.Name,
            MaxApproveLevel = changeType.MaxApproveLevel,
            MaxApproveLevelName = ApproveLevels.GetDisplayName(changeType.MaxApproveLevel),
            TemplateVersion = template.Version,
            ConfiguredNodeCount = nodes.Count,
            ActualStepCount = previewNodes.Count,
            Nodes = previewNodes,
            Warnings = plan.Warnings
        };
    }

    // ------------------------------------------------------------------
    // 内部实现
    // ------------------------------------------------------------------

    /// <summary>
    /// 构造组织解析函数。
    ///
    /// 做法：取目标学校与原学校的物化路径，逐级截出省 / 市 / 县的前缀，
    /// 一次查询把这些上级组织都取回来，再按解析器名映射。
    /// 这样不必把整棵组织树（一个省可能有几万个节点）加载进内存。
    /// </summary>
    private async Task<Func<string, ResolvedOrg?>> BuildOrgResolverAsync(
        Guid targetSchoolId,
        Guid? sourceSchoolId)
    {
        var target = await _orgs.GetAsync(targetSchoolId);

        var source = sourceSchoolId.HasValue && sourceSchoolId.Value != targetSchoolId
            ? await _orgs.GetAsync(sourceSchoolId.Value)
            : target;

        var neededPaths = new List<string>();
        neededPaths.AddRange(AncestorPrefixes(target.PathCode));

        if (!ReferenceEquals(source, target))
        {
            neededPaths.AddRange(AncestorPrefixes(source.PathCode));
        }

        neededPaths = neededPaths.Distinct(StringComparer.Ordinal).ToList();

        var ancestors = neededPaths.Count == 0
            ? new List<SysOrg>()
            : await _orgs.GetListAsync(o => neededPaths.Contains(o.PathCode));

        var byPath = ancestors.ToDictionary(o => o.PathCode, o => o, StringComparer.Ordinal);

        ResolvedOrg? ResolveAncestor(SysOrg school, int segmentIndex)
        {
            var segments = school.PathCode.Split('.');
            if (segments.Length <= segmentIndex)
            {
                return null;
            }

            var prefix = string.Join('.', segments.Take(segmentIndex + 1));

            return byPath.TryGetValue(prefix, out var org) ? ToResolved(org) : null;
        }

        return key => key switch
        {
            ChangeOrgResolvers.TargetSchool => ToResolved(target),
            ChangeOrgResolvers.ApplicantSchool => ToResolved(target),
            ChangeOrgResolvers.SourceSchool => ToResolved(source),
            ChangeOrgResolvers.StudentSchool => ToResolved(source),
            ChangeOrgResolvers.TargetCounty => ResolveAncestor(target, 2),
            ChangeOrgResolvers.TargetCity => ResolveAncestor(target, 1),
            ChangeOrgResolvers.Province => ResolveAncestor(target, 0),
            ChangeOrgResolvers.SourceCounty => ResolveAncestor(source, 2),
            ChangeOrgResolvers.SourceCity => ResolveAncestor(source, 1),
            _ => null
        };
    }

    /// <summary>物化路径的上级前缀：410000.410100.410102.4101020001 → 前三段。</summary>
    private static IEnumerable<string> AncestorPrefixes(string pathCode)
    {
        var segments = pathCode.Split('.');

        for (var i = 1; i < segments.Length; i++)
        {
            yield return string.Join('.', segments.Take(i));
        }
    }

    private static ResolvedOrg ToResolved(SysOrg org) => new()
    {
        Id = org.Id,
        Code = org.Code,
        Name = org.Name,
        OrgType = org.OrgType,
        PathCode = org.PathCode
    };

    private async Task ValidateNodesAsync(List<CreateUpdateFlowNodeDto> nodes)
    {
        var duplicatedSeq = nodes.GroupBy(n => n.Seq).Any(g => g.Count() > 1);
        if (duplicatedSeq)
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", "流程节点序号不能重复");
        }

        foreach (var node in nodes)
        {
            if (!ChangeOrgResolvers.All.Contains(node.OrgResolver))
            {
                throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                    .WithData("reason", $"未知的组织解析器「{node.OrgResolver}」");
            }

            if (!ChangeMergeStrategies.All.Contains(node.MergeStrategy))
            {
                throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                    .WithData("reason", $"未知的合并策略「{node.MergeStrategy}」");
            }
        }

        await Task.CompletedTask;
    }

    private static void ApplyTypeInput(
        StuChangeType entity,
        CreateUpdateChangeTypeDto input,
        bool keepCode = false)
    {
        if (!keepCode)
        {
            entity.Code = input.Code.Trim();
        }

        entity.CategoryCode = input.CategoryCode;
        entity.BeforeStatusCode = input.BeforeStatusCode;
        entity.AfterStatusCode = input.AfterStatusCode;
        entity.NeedPeerSchoolConfirm = input.NeedPeerSchoolConfirm;
        entity.AffectGrade = input.AffectGrade;
        entity.AffectClass = input.AffectClass;
        entity.AllowCrossSchool = input.AllowCrossSchool;
        entity.ApplyWindowStart = input.ApplyWindowStart;
        entity.ApplyWindowEnd = input.ApplyWindowEnd;
        entity.AllowCrossAcademicYear = input.AllowCrossAcademicYear;
        entity.MaxApplyTimesPerYear = input.MaxApplyTimesPerYear;
        entity.GradeScope = input.GradeScope;
        entity.RequireAttachment = input.RequireAttachment;
        entity.Sort = input.Sort;
        entity.IsEnabled = input.IsEnabled;
        entity.Remark = input.Remark;
    }

    private async Task<ChangeFlowTemplateDto> ToTemplateDtoAsync(StuChangeFlowTemplate template)
    {
        var nodes = await _nodes.GetSortedListAsync(n => n.TemplateId == template.Id, "Seq ASC");

        return new ChangeFlowTemplateDto
        {
            Id = template.Id,
            ChangeTypeId = template.ChangeTypeId,
            Name = template.Name,
            Version = template.Version,
            Status = template.Status,
            StatusName = ChangeFlowTemplateStatus.GetDisplayName(template.Status),
            PublishedTime = template.PublishedTime,
            Remark = template.Remark,
            Nodes = nodes.Select(n => new ChangeFlowNodeDto
            {
                Id = n.Id,
                TemplateId = n.TemplateId,
                Seq = n.Seq,
                NodeName = n.NodeName,
                OrgResolver = n.OrgResolver,
                OrgResolverName = ChangeOrgResolvers.GetDisplayName(n.OrgResolver),
                RoleCode = n.RoleCode,
                MergeStrategy = n.MergeStrategy,
                AllowReject = n.AllowReject,
                AllowDelegate = n.AllowDelegate,
                TimeoutDays = n.TimeoutDays,
                IsEnabled = n.IsEnabled,
                Remark = n.Remark
            }).ToList()
        };
    }

    private static ChangeTypeDto ToTypeDto(StuChangeType t) => new()
    {
        Id = t.Id,
        Code = t.Code,
        Name = t.Name,
        CategoryCode = t.CategoryCode,
        BeforeStatusCode = t.BeforeStatusCode,
        AfterStatusCode = t.AfterStatusCode,
        MaxApproveLevel = t.MaxApproveLevel,
        MaxApproveLevelName = ApproveLevels.GetDisplayName(t.MaxApproveLevel),
        NeedPeerSchoolConfirm = t.NeedPeerSchoolConfirm,
        AffectGrade = t.AffectGrade,
        AffectClass = t.AffectClass,
        AllowCrossSchool = t.AllowCrossSchool,
        ApplyWindowStart = t.ApplyWindowStart,
        ApplyWindowEnd = t.ApplyWindowEnd,
        AllowCrossAcademicYear = t.AllowCrossAcademicYear,
        MaxApplyTimesPerYear = t.MaxApplyTimesPerYear,
        GradeScope = t.GradeScope,
        RequireAttachment = t.RequireAttachment,
        FlowTemplateId = t.FlowTemplateId,
        HasPublishedFlow = t.FlowTemplateId.HasValue,
        Sort = t.Sort,
        IsEnabled = t.IsEnabled,
        Remark = t.Remark
    };
}
