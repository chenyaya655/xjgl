using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Authorization;
using Volo.Abp.Timing;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Permissions;
using Xjgl.Sequences;
using Xjgl.Students;
using Xjgl.Users;

namespace Xjgl.Changes;

/// <summary>
/// 异动申请与审核实现。
/// </summary>
[Authorize]
public class ChangeFlowAppService : XjglAppService, IChangeFlowAppService
{
    private readonly IXjglRepository<StuChangeApply> _applies;
    private readonly IXjglRepository<StuChangeFlowTask> _tasks;
    private readonly IXjglRepository<StuChangeFlowLog> _logs;
    private readonly IXjglRepository<StuChangeType> _types;
    private readonly IXjglRepository<StuChangeFlowTemplate> _templates;
    private readonly IXjglRepository<StuChangeFlowNode> _nodes;
    private readonly IXjglRepository<StuStudent> _students;
    private readonly IXjglRepository<StuStudent> _studentsForUpdate;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IXjglRepository<SysGrade> _grades;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IChangeFlowPlanner _planner;
    private readonly ISequenceService _sequenceService;
    private readonly IClock _clock;
    private readonly ILogger<ChangeFlowAppService> _logger;

    public ChangeFlowAppService(
        IXjglRepository<StuChangeApply> applies,
        IXjglRepository<StuChangeFlowTask> tasks,
        IXjglRepository<StuChangeFlowLog> logs,
        IXjglRepository<StuChangeType> types,
        IXjglRepository<StuChangeFlowTemplate> templates,
        IXjglRepository<StuChangeFlowNode> nodes,
        IXjglRepository<StuStudent> students,
        IXjglRepository<SysOrg> orgs,
        IXjglRepository<SysGrade> grades,
        IXjglRepository<SysUserPredefine> predefines,
        IChangeFlowPlanner planner,
        ISequenceService sequenceService,
        IClock clock,
        ILogger<ChangeFlowAppService> logger)
    {
        _applies = applies;
        _tasks = tasks;
        _logs = logs;
        _types = types;
        _templates = templates;
        _nodes = nodes;
        _students = students;
        _studentsForUpdate = students;
        _orgs = orgs;
        _grades = grades;
        _predefines = predefines;
        _planner = planner;
        _sequenceService = sequenceService;
        _clock = clock;
        _logger = logger;
    }

    // ==================================================================
    // 申请
    // ==================================================================

    [Authorize(MetadataPermissions.Chg.Apply.Create)]
    public async Task<ChangeApplyDto> SubmitAsync(SubmitChangeApplyDto input)
    {
        var changeType = await _types.GetAsync(input.ChangeTypeId);

        if (!changeType.IsEnabled)
        {
            throw new BusinessException(XjglDomainErrorCodes.ChangeTypeNotFound)
                .WithData("reason", $"异动类型「{changeType.Name}」已停用");
        }

        var student = await _students.GetAsync(input.StudentId);

        // ---- 1) 异动时间控制 ----
        var now = _clock.Now;

        if (!changeType.IsInApplyWindow(now))
        {
            throw new BusinessException(XjglDomainErrorCodes.OutOfApplyTimeWindow)
                .WithData("reason",
                    $"当前不在「{changeType.Name}」允许申请的时间窗口内" +
                    $"（{changeType.ApplyWindowStart} ~ {changeType.ApplyWindowEnd}）");
        }

        // ---- 2) 年级控制 ----
        string? gradeCode = null;
        if (student.GradeId.HasValue)
        {
            var grade = await _grades.FindAsync(student.GradeId.Value);
            gradeCode = grade?.GradeCode;
        }

        if (!changeType.IsGradeAllowed(gradeCode))
        {
            throw new BusinessException(XjglDomainErrorCodes.GradeNotAllowedForChange)
                .WithData("reason",
                    $"当前年级不在「{changeType.Name}」允许申请的年级范围内（{changeType.GradeScope}）");
        }

        // ---- 3) 跨校控制 ----
        var targetSchoolId = input.TargetSchoolOrgId ?? student.SchoolOrgId;

        if (targetSchoolId != student.SchoolOrgId && !changeType.AllowCrossSchool)
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", $"「{changeType.Name}」不允许跨校办理");
        }

        // ---- 4) 按已发布的流程模板实例化 ----
        var plan = await PlanFlowAsync(changeType, targetSchoolId, student.SchoolOrgId);

        if (plan.Tasks.Count == 0)
        {
            throw new BusinessException(XjglDomainErrorCodes.ChangeFlowNotConfigured)
                .WithData("reason", "流程实例化后没有任何可执行的节点，请检查流程配置");
        }

        // ---- 5) 建档 ----
        var apply = new StuChangeApply(
            GuidGenerator.Create(),
            await GenerateApplyNoAsync(),
            changeType.Id,
            student.Id,
            student.Name)
        {
            ChangeTypeCode = changeType.Code,
            ChangeTypeName = changeType.Name,
            StudentNo = student.StudentNo,
            ApplyDate = now,
            SourceSchoolOrgId = student.SchoolOrgId,
            SourceGradeId = student.GradeId,
            SourceClassId = student.ClassId,
            SourceStatusCode = student.StudentStatusCode,
            TargetSchoolOrgId = input.TargetSchoolOrgId ?? student.SchoolOrgId,
            TargetGradeId = input.TargetGradeId ?? student.GradeId,
            TargetClassId = input.TargetClassId ?? student.ClassId,
            TargetStatusCode = changeType.AfterStatusCode ?? student.StudentStatusCode,
            Reason = input.Reason,
            Remark = input.Remark,
            Status = ChangeApplyStatus.Submitted,
            FlowTemplateId = plan.TemplateId,
            FlowTemplateVersion = plan.TemplateVersion,
            CurrentSeq = plan.Tasks[0].Seq
        };

        await _applies.InsertAsync(apply, autoSave: true);

        // ---- 6) 生成待办（节点定义快照进任务） ----
        var tasks = plan.Tasks.Select(t => new StuChangeFlowTask(
            GuidGenerator.Create(),
            apply.Id,
            t.Seq,
            t.Name,
            t.OrgId,
            t.OrgName,
            t.RoleCode,
            t.ApproveLevel)
        {
            MergedFromSeqs = t.MergedFromSeqs.Count == 0
                ? null
                : string.Join(",", t.MergedFromSeqs),
            Status = ChangeTaskStatus.Pending,
            DueTime = t.TimeoutDays > 0 ? now.AddDays(t.TimeoutDays) : null
        }).ToList();

        await _tasks.InsertManyAsync(tasks, autoSave: true);

        await WriteLogAsync(apply.Id, null, null, ChangeFlowActions.Submit,
            $"提交申请，共 {tasks.Count} 个审核节点");

        return await GetApplyAsync(apply.Id);
    }

    [Authorize(MetadataPermissions.Chg.Apply.Query)]
    public async Task<PagedResultDto<ChangeApplyListItemDto>> GetAppliesAsync(ChangeApplyQueryDto input)
    {
        var predicate = BuildQueryPredicate(input);

        var applies = await _applies.GetPagedListAsync(
            predicate, input.Sorting ?? "ApplyDate DESC", input.SkipCount, input.MaxResultCount);

        var total = await _applies.GetCountAsync(predicate);

        return new PagedResultDto<ChangeApplyListItemDto>(total, await ToListItemsAsync(applies));
    }

    [Authorize(MetadataPermissions.Chg.Apply.Query)]
    public async Task<ChangeApplyDto> GetApplyAsync(Guid id)
    {
        var apply = await _applies.GetAsync(id);
        var item = (await ToListItemsAsync(new List<StuChangeApply> { apply })).Single();

        var tasks = await _tasks.GetSortedListAsync(t => t.ApplyId == id, "Seq ASC");
        var logs = await _logs.GetSortedListAsync(l => l.ApplyId == id, "ActionTime ASC, CreationTime ASC");

        var dto = new ChangeApplyDto();
        CopyListFields(item, dto);

        dto.ChangeTypeId = apply.ChangeTypeId;
        dto.SourceSchoolOrgId = apply.SourceSchoolOrgId;
        dto.SourceGradeId = apply.SourceGradeId;
        dto.SourceClassId = apply.SourceClassId;
        dto.SourceStatusCode = apply.SourceStatusCode;
        dto.TargetSchoolOrgId = apply.TargetSchoolOrgId;
        dto.TargetGradeId = apply.TargetGradeId;
        dto.TargetClassId = apply.TargetClassId;
        dto.TargetStatusCode = apply.TargetStatusCode;
        dto.Reason = apply.Reason;
        dto.Remark = apply.Remark;
        dto.FlowTemplateId = apply.FlowTemplateId;
        dto.FlowTemplateVersion = apply.FlowTemplateVersion;

        dto.Tasks = tasks.Select(t => new ChangeFlowTaskDto
        {
            Id = t.Id,
            Seq = t.Seq,
            NodeName = t.NodeName,
            OrgId = t.OrgId,
            OrgName = t.OrgName,
            RoleCode = t.RoleCode,
            ApproveLevel = t.ApproveLevel,
            ApproveLevelName = ApproveLevels.GetDisplayName(t.ApproveLevel),
            Status = t.Status,
            StatusName = ChangeTaskStatus.GetDisplayName(t.Status),
            MergedFromSeqs = t.GetMergedFromSeqs().ToList(),
            HandledByName = t.HandledByName,
            HandledTime = t.HandledTime,
            Opinion = t.Opinion,
            DueTime = t.DueTime
        }).ToList();

        dto.Logs = logs.Select(l => new ChangeFlowLogDto
        {
            Id = l.Id,
            Seq = l.Seq,
            Action = l.Action,
            ActionName = GetActionName(l.Action),
            OperatorName = l.OperatorName,
            Opinion = l.Opinion,
            ActionTime = l.ActionTime
        }).ToList();

        return dto;
    }

    [Authorize(MetadataPermissions.Chg.Apply.Update)]
    public async Task<ChangeApplyDto> WithdrawAsync(Guid id)
    {
        var apply = await _applies.GetAsync(id);

        if (!apply.IsAuditable())
        {
            throw new BusinessException(XjglDomainErrorCodes.RevokeNotAllowed)
                .WithData("reason", "当前状态不允许撤回");
        }

        var tasks = await _tasks.GetListAsync(t => t.ApplyId == id);

        if (tasks.Any(t => t.Status != ChangeTaskStatus.Pending))
        {
            throw new BusinessException(XjglDomainErrorCodes.RevokeNotAllowed)
                .WithData("reason", "已有节点完成审核，不能撤回申请");
        }

        apply.Status = ChangeApplyStatus.Withdrawn;
        apply.CurrentSeq = null;
        await _applies.UpdateAsync(apply, autoSave: true);

        await WriteLogAsync(apply.Id, null, null, ChangeFlowActions.Withdraw, "撤回申请");

        return await GetApplyAsync(apply.Id);
    }

    [Authorize(MetadataPermissions.Chg.Apply.Delete)]
    public async Task DeleteAsync(Guid id)
    {
        var apply = await _applies.GetAsync(id);

        if (apply.Status is not (ChangeApplyStatus.Draft or ChangeApplyStatus.Withdrawn
            or ChangeApplyStatus.Rejected))
        {
            throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                .WithData("reason", "只有草稿、已撤回或已驳回的申请单可以删除");
        }

        await _applies.DeleteAsync(apply, autoSave: true);

        var tasks = await _tasks.GetListAsync(t => t.ApplyId == id);
        if (tasks.Count > 0)
        {
            await _tasks.DeleteManyAsync(tasks.Select(t => t.Id), autoSave: true);
        }
    }

    // ==================================================================
    // 审核
    // ==================================================================

    [Authorize(MetadataPermissions.Chg.Audit.Query)]
    public async Task<PagedResultDto<ChangeApplyListItemDto>> GetTodoListAsync(ChangeTodoQueryDto input)
    {
        var currentOrgId = await GetCurrentUserOrgIdAsync();

        // 可办理的组织：明确指定 > 当前用户所属组织 > 全部
        var orgId = input.OrgId ?? currentOrgId;

        var hasOrg = orgId.HasValue;
        var filterOrgId = orgId.GetValueOrDefault();
        var hasRole = !input.RoleCode.IsNullOrWhiteSpace();
        var roleCode = input.RoleCode;

        var tasks = await _tasks.GetListAsync(t =>
            t.Status == ChangeTaskStatus.Pending
            && (!hasOrg || t.OrgId == filterOrgId)
            && (!hasRole || t.RoleCode == roleCode));

        var total = tasks.Count;

        // 按创建时间**降序**：新待办排在前面。
        // 这既符合「待办列表把最新的放前面」的使用习惯，也避免分页时
        // 最新单据因为排在末尾而被 Take 截掉（测试库里会累积历史待办）。
        var paged = tasks
            .OrderByDescending(t => t.CreationTime)
            .ThenByDescending(t => t.Seq)
            .Skip(input.SkipCount)
            .Take(input.MaxResultCount)
            .ToList();

        if (paged.Count == 0)
        {
            return new PagedResultDto<ChangeApplyListItemDto>(total, new List<ChangeApplyListItemDto>());
        }

        // 只取「当前节点正好是这条待办」的申请单，避免把已推进的单据也列出来
        var applyIds = paged.Select(t => t.ApplyId).Distinct().ToList();
        var applies = await _applies.GetListAsync(a => applyIds.Contains(a.Id));

        var result = new List<ChangeApplyListItemDto>();

        foreach (var task in paged)
        {
            var apply = applies.FirstOrDefault(a => a.Id == task.ApplyId);

            if (apply == null || apply.CurrentSeq != task.Seq || !apply.IsAuditable())
            {
                continue;
            }

            var item = (await ToListItemsAsync(new List<StuChangeApply> { apply })).Single();
            item.CurrentNodeName = task.NodeName;
            item.CurrentOrgName = task.OrgName;
            result.Add(item);
        }

        return new PagedResultDto<ChangeApplyListItemDto>(total, result);
    }

    [Authorize(MetadataPermissions.Chg.Audit.Approve)]
    public async Task<ChangeApplyDto> ApproveAsync(ChangeAuditActionDto input)
    {
        var (apply, current, tasks) = await LoadCurrentTaskAsync(input.ApplyId);

        var now = _clock.Now;

        current.Status = ChangeTaskStatus.Approved;
        current.HandledByUserId = CurrentUser.Id;
        current.HandledByName = CurrentUser.UserName;
        current.HandledTime = now;
        current.Opinion = input.Opinion;

        await _tasks.UpdateAsync(current, autoSave: true);

        await WriteLogAsync(apply.Id, current.Id, current.Seq, ChangeFlowActions.Approve,
            input.Opinion ?? "审核通过");

        var next = tasks
            .Where(t => t.Seq > current.Seq && t.Status == ChangeTaskStatus.Pending)
            .OrderBy(t => t.Seq)
            .FirstOrDefault();

        if (next == null)
        {
            // 最后一个节点 → 流程完成
            apply.Status = ChangeApplyStatus.Approved;
            apply.CurrentSeq = null;
            apply.FinalApprovedTime = now;

            await _applies.UpdateAsync(apply, autoSave: true);

            // 业务联动：把「变动后」的学籍状态、学校、年级、班级落到学生档案
            await ApplyStudentChangesAsync(apply);

            await WriteLogAsync(apply.Id, null, null, ChangeFlowActions.Archive,
                "全部节点审核通过，学籍变更已生效");
        }
        else
        {
            apply.Status = ChangeApplyStatus.InProgress;
            apply.CurrentSeq = next.Seq;

            await _applies.UpdateAsync(apply, autoSave: true);
        }

        return await GetApplyAsync(apply.Id);
    }

    [Authorize(MetadataPermissions.Chg.Audit.Approve)]
    public async Task<ChangeApplyDto> RejectAsync(ChangeAuditActionDto input)
    {
        var (apply, current, _) = await LoadCurrentTaskAsync(input.ApplyId);

        current.Status = ChangeTaskStatus.Rejected;
        current.HandledByUserId = CurrentUser.Id;
        current.HandledByName = CurrentUser.UserName;
        current.HandledTime = _clock.Now;
        current.Opinion = input.Opinion;

        await _tasks.UpdateAsync(current, autoSave: true);

        apply.Status = ChangeApplyStatus.Rejected;
        apply.CurrentSeq = null;

        await _applies.UpdateAsync(apply, autoSave: true);

        await WriteLogAsync(apply.Id, current.Id, current.Seq, ChangeFlowActions.Reject,
            input.Opinion ?? "审核驳回");

        return await GetApplyAsync(apply.Id);
    }

    [Authorize(MetadataPermissions.Chg.Audit.RevokeApprove)]
    public async Task<ChangeApplyDto> RevokeApprovalAsync(ChangeAuditActionDto input)
    {
        var apply = await _applies.GetAsync(input.ApplyId);
        var tasks = await _tasks.GetListAsync(t => t.ApplyId == apply.Id);

        // 找最后已处理的节点
        var lastHandled = tasks
            .Where(t => t.Status == ChangeTaskStatus.Approved)
            .OrderByDescending(t => t.Seq)
            .FirstOrDefault();

        if (lastHandled == null)
        {
            throw new BusinessException(XjglDomainErrorCodes.RevokeNotAllowed)
                .WithData("reason", "该申请单还没有任何已完成的审核节点");
        }

        // 后续节点必须都未处理
        if (tasks.Any(t => t.Seq > lastHandled.Seq && t.Status != ChangeTaskStatus.Pending))
        {
            throw new BusinessException(XjglDomainErrorCodes.RevokeNotAllowed)
                .WithData("reason", "后续节点已处理，不能撤销审核");
        }

        var wasFullyApproved = apply.Status == ChangeApplyStatus.Approved;

        await EnsureCanHandleAsync(lastHandled);

        // 回退节点
        lastHandled.Status = ChangeTaskStatus.Pending;
        lastHandled.HandledByUserId = null;
        lastHandled.HandledByName = null;
        lastHandled.HandledTime = null;
        lastHandled.Opinion = null;

        await _tasks.UpdateAsync(lastHandled, autoSave: true);

        apply.Status = ChangeApplyStatus.InProgress;
        apply.CurrentSeq = lastHandled.Seq;
        apply.FinalApprovedTime = null;

        await _applies.UpdateAsync(apply, autoSave: true);

        // 若整个流程已通过，撤销时必须把学籍变更一起回滚
        if (wasFullyApproved)
        {
            await RevertStudentChangesAsync(apply);
        }

        await WriteLogAsync(apply.Id, lastHandled.Id, lastHandled.Seq, ChangeFlowActions.Revoke,
            input.Opinion ?? (wasFullyApproved ? "撤销审核并回滚学籍变更" : "撤销审核"));

        return await GetApplyAsync(apply.Id);
    }

    // ==================================================================
    // 业务联动
    // ==================================================================

    /// <summary>
    /// 把申请单上的「变动后」状态落到学生档案。
    /// 先改学生、再改单据状态，保证即使中间失败也不会出现「单据已通过但学生没变」。
    /// </summary>
    private async Task ApplyStudentChangesAsync(StuChangeApply apply)
    {
        var student = await _studentsForUpdate.GetAsync(apply.StudentId);

        if (!apply.TargetStatusCode.IsNullOrWhiteSpace())
        {
            student.StudentStatusCode = apply.TargetStatusCode!;
        }

        if (apply.TargetSchoolOrgId.HasValue && apply.TargetSchoolOrgId.Value != student.SchoolOrgId)
        {
            var school = await _orgs.FindAsync(apply.TargetSchoolOrgId.Value);
            if (school != null)
            {
                student.SchoolOrgId = school.Id;
                student.SchoolPathCode = school.PathCode;
            }
        }

        if (apply.TargetGradeId.HasValue)
        {
            student.GradeId = apply.TargetGradeId;
        }

        if (apply.TargetClassId.HasValue)
        {
            student.ClassId = apply.TargetClassId;
        }

        await _studentsForUpdate.UpdateAsync(student, autoSave: true);

        _logger.LogInformation(
            "异动申请 {ApplyNo} 审核通过，学生 {StudentName} 学籍状态更新为 {Status}。",
            apply.ApplyNo, apply.StudentName, student.StudentStatusCode);
    }

    /// <summary>撤销已通过审核时，把学生恢复到申请单记录的「变动前」状态。</summary>
    private async Task RevertStudentChangesAsync(StuChangeApply apply)
    {
        var student = await _studentsForUpdate.GetAsync(apply.StudentId);

        student.StudentStatusCode = apply.SourceStatusCode ?? student.StudentStatusCode;
        student.GradeId = apply.SourceGradeId;
        student.ClassId = apply.SourceClassId;

        if (apply.SourceSchoolOrgId != student.SchoolOrgId)
        {
            var school = await _orgs.FindAsync(apply.SourceSchoolOrgId);
            if (school != null)
            {
                student.SchoolOrgId = school.Id;
                student.SchoolPathCode = school.PathCode;
            }
        }

        await _studentsForUpdate.UpdateAsync(student, autoSave: true);

        _logger.LogWarning(
            "异动申请 {ApplyNo} 撤销审核，学生 {StudentName} 的学籍变更已回滚。",
            apply.ApplyNo, apply.StudentName);
    }

    // ==================================================================
    // 内部实现
    // ==================================================================

    private async Task<FlowPlanContext> PlanFlowAsync(
        StuChangeType changeType,
        Guid targetSchoolId,
        Guid sourceSchoolId)
    {
        var template = await _templates.FindAsync(t =>
            t.ChangeTypeId == changeType.Id && t.Status == ChangeFlowTemplateStatus.Published);

        if (template == null)
        {
            throw new BusinessException(XjglDomainErrorCodes.ChangeFlowNotConfigured)
                .WithData("reason", $"异动类型「{changeType.Name}」还没有已发布的流程模板");
        }

        var nodes = await _nodes.GetSortedListAsync(n => n.TemplateId == template.Id, "Seq ASC");

        var resolver = await BuildOrgResolverAsync(targetSchoolId, sourceSchoolId);

        var plan = _planner.Plan(nodes.Select(n => n.ToDefinition()), resolver, changeType.MaxApproveLevel);

        return new FlowPlanContext
        {
            TemplateId = template.Id,
            TemplateVersion = template.Version,
            Tasks = plan.Tasks
        };
    }

    /// <summary>
    /// 构造组织解析函数：取学校物化路径，逐级截出省 / 市 / 县前缀后一次性精确查询，
    /// 避免把整棵组织树加载进内存。
    /// </summary>
    private async Task<Func<string, ResolvedOrg?>> BuildOrgResolverAsync(Guid targetSchoolId, Guid sourceSchoolId)
    {
        var target = await _orgs.GetAsync(targetSchoolId);
        var source = targetSchoolId == sourceSchoolId ? target : await _orgs.GetAsync(sourceSchoolId);

        var neededPaths = AncestorPrefixes(target.PathCode).ToList();

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

    private async Task<(StuChangeApply Apply, StuChangeFlowTask Current, List<StuChangeFlowTask> Tasks)>
        LoadCurrentTaskAsync(Guid applyId)
    {
        var apply = await _applies.GetAsync(applyId);

        if (!apply.IsAuditable())
        {
            throw new BusinessException(XjglDomainErrorCodes.ChangeApplyNotFound)
                .WithData("reason", $"申请单当前状态为「{ChangeApplyStatus.GetDisplayName(apply.Status)}」，不能审核");
        }

        if (apply.CurrentSeq == null)
        {
            throw new BusinessException(XjglDomainErrorCodes.ChangeApplyNotFound)
                .WithData("reason", "申请单没有待处理节点");
        }

        var tasks = await _tasks.GetListAsync(t => t.ApplyId == applyId);

        var current = tasks.FirstOrDefault(t =>
            t.Seq == apply.CurrentSeq.Value && t.Status == ChangeTaskStatus.Pending);

        if (current == null)
        {
            throw new BusinessException(XjglDomainErrorCodes.ChangeApplyNotFound)
                .WithData("reason", "当前节点不存在或已被处理");
        }

        await EnsureCanHandleAsync(current);

        return (apply, current, tasks);
    }

    /// <summary>
    /// 校验当前用户是否有权办理该待办。
    ///
    /// 从预置记录反查用户所属组织；若用户没有组织绑定（例如内置 admin），
    /// 则不做组织校验，交由权限特性兜底。
    /// </summary>
    private async Task EnsureCanHandleAsync(StuChangeFlowTask task)
    {
        if (CurrentUser.Id == null)
        {
            throw new AbpAuthorizationException("当前请求未通过身份认证。");
        }

        var predefine = await _predefines.FindAsync(p => p.ActivatedUserId == CurrentUser.Id.Value);

        if (predefine == null)
        {
            return;
        }

        if (predefine.OrgId != task.OrgId)
        {
            throw new BusinessException(XjglDomainErrorCodes.TaskNotAssignedToYou)
                .WithData("reason", $"该待办属于「{task.OrgName}」，不在您的办理范围内");
        }
    }

    private async Task<Guid?> GetCurrentUserOrgIdAsync()
    {
        if (CurrentUser.Id == null)
        {
            return null;
        }

        var predefine = await _predefines.FindAsync(p => p.ActivatedUserId == CurrentUser.Id.Value);

        return predefine?.OrgId;
    }

    private async Task<string> GenerateApplyNoAsync()
    {
        var sequence = _sequenceService.NextValue($"CHG_APPLY:{_clock.Now:yyyyMM}", length: 6);

        return $"YC{_clock.Now:yyyyMM}{sequence:D6}";
    }

    private async Task WriteLogAsync(
        Guid applyId,
        Guid? taskId,
        int? seq,
        string action,
        string? opinion)
    {
        var log = new StuChangeFlowLog(GuidGenerator.Create(), applyId, action, _clock.Now)
        {
            TaskId = taskId,
            Seq = seq,
            OperatorUserId = CurrentUser.Id,
            OperatorName = CurrentUser.UserName,
            Opinion = opinion
        };

        await _logs.InsertAsync(log, autoSave: true);
    }

    private static string? GetActionName(string action) => action switch
    {
        ChangeFlowActions.Submit => "提交申请",
        ChangeFlowActions.Approve => "审核通过",
        ChangeFlowActions.Reject => "审核驳回",
        ChangeFlowActions.Revoke => "撤销审核",
        ChangeFlowActions.Withdraw => "撤回申请",
        ChangeFlowActions.Archive => "流程归档",
        _ => action
    };

    private static System.Linq.Expressions.Expression<Func<StuChangeApply, bool>>? BuildQueryPredicate(
        ChangeApplyQueryDto input)
    {
        var hasType = input.ChangeTypeId.HasValue;
        var typeId = input.ChangeTypeId.GetValueOrDefault();
        var hasStudent = input.StudentId.HasValue;
        var studentId = input.StudentId.GetValueOrDefault();
        var hasStatus = !input.Status.IsNullOrWhiteSpace();
        var status = input.Status;
        var hasSchool = input.SchoolOrgId.HasValue;
        var schoolId = input.SchoolOrgId.GetValueOrDefault();
        var hasKeyword = !input.Keyword.IsNullOrWhiteSpace();
        var keyword = input.Keyword?.Trim();

        if (!hasType && !hasStudent && !hasStatus && !hasSchool && !hasKeyword)
        {
            return null;
        }

        return a =>
            (!hasType || a.ChangeTypeId == typeId) &&
            (!hasStudent || a.StudentId == studentId) &&
            (!hasStatus || a.Status == status) &&
            (!hasSchool || a.SourceSchoolOrgId == schoolId || a.TargetSchoolOrgId == schoolId) &&
            (!hasKeyword || a.ApplyNo.Contains(keyword!) || a.StudentName.Contains(keyword!));
    }

    private async Task<List<ChangeApplyListItemDto>> ToListItemsAsync(List<StuChangeApply> applies)
    {
        if (applies.Count == 0)
        {
            return new List<ChangeApplyListItemDto>();
        }

        var schoolIds = applies
            .SelectMany(a => new[] { a.SourceSchoolOrgId, a.TargetSchoolOrgId ?? a.SourceSchoolOrgId })
            .Distinct()
            .ToList();

        var schools = await _orgs.GetListAsync(o => schoolIds.Contains(o.Id));
        var schoolMap = schools.ToDictionary(o => o.Id, o => o.Name);

        return applies.Select(a => new ChangeApplyListItemDto
        {
            Id = a.Id,
            ApplyNo = a.ApplyNo,
            ChangeTypeCode = a.ChangeTypeCode,
            ChangeTypeName = a.ChangeTypeName,
            StudentId = a.StudentId,
            StudentName = a.StudentName,
            StudentNo = a.StudentNo,
            ApplyDate = a.ApplyDate,
            Status = a.Status,
            StatusName = ChangeApplyStatus.GetDisplayName(a.Status),
            SourceSchoolName = schoolMap.TryGetValue(a.SourceSchoolOrgId, out var sn) ? sn : null,
            TargetSchoolName = a.TargetSchoolOrgId.HasValue
                && schoolMap.TryGetValue(a.TargetSchoolOrgId.Value, out var tn) ? tn : null,
            CurrentSeq = a.CurrentSeq,
            FinalApprovedTime = a.FinalApprovedTime
        }).ToList();
    }

    private static void CopyListFields(ChangeApplyListItemDto source, ChangeApplyDto target)
    {
        target.Id = source.Id;
        target.ApplyNo = source.ApplyNo;
        target.ChangeTypeCode = source.ChangeTypeCode;
        target.ChangeTypeName = source.ChangeTypeName;
        target.StudentId = source.StudentId;
        target.StudentName = source.StudentName;
        target.StudentNo = source.StudentNo;
        target.ApplyDate = source.ApplyDate;
        target.Status = source.Status;
        target.StatusName = source.StatusName;
        target.SourceSchoolName = source.SourceSchoolName;
        target.TargetSchoolName = source.TargetSchoolName;
        target.CurrentSeq = source.CurrentSeq;
        target.CurrentNodeName = source.CurrentNodeName;
        target.CurrentOrgName = source.CurrentOrgName;
        target.FinalApprovedTime = source.FinalApprovedTime;
    }

    private sealed class FlowPlanContext
    {
        public Guid TemplateId { get; init; }
        public int TemplateVersion { get; init; }
        public List<PlannedFlowTask> Tasks { get; init; } = new();
    }
}
