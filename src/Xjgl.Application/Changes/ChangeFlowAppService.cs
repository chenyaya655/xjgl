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
    private readonly IXjglRepository<StuChangeAttachment> _attachments;
    private readonly IXjglRepository<StuStudent> _students;
    private readonly IXjglRepository<StuStudent> _studentsForUpdate;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IXjglRepository<SysGrade> _grades;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IChangeFlowPlanner _planner;
    private readonly ISequenceService _sequenceService;
    private readonly IClock _clock;
    private readonly ILogger<ChangeFlowAppService> _logger;

    /// <summary>申请单的数据范围守卫（附件是独立路由，不会自动继承主档的过滤条件）。</summary>
    private readonly IChangeApplyAccessGuard _applyGuard;

    /// <summary>删除申请单时要把它名下的附件文件一起清掉（否则共享卷上会留下永久孤儿）。</summary>
    private readonly IChangeAttachmentStore _attachmentStore;

    /// <summary>多步写的事务范围（债 1：审批通过要"改学生档案 + 写审批状态 + 写流水"原子生效）。</summary>
    private readonly IXjglTransactionScope _tx;

    /// <summary>学籍变更留痕（审批改学籍主档时写流水）。</summary>
    private readonly IStudentChangeLogger _changeLogger;

    public ChangeFlowAppService(
        IXjglRepository<StuChangeApply> applies,
        IXjglRepository<StuChangeFlowTask> tasks,
        IXjglRepository<StuChangeFlowLog> logs,
        IXjglRepository<StuChangeType> types,
        IXjglRepository<StuChangeFlowTemplate> templates,
        IXjglRepository<StuChangeFlowNode> nodes,
        IXjglRepository<StuChangeAttachment> attachments,
        IXjglRepository<StuStudent> students,
        IXjglRepository<SysOrg> orgs,
        IXjglRepository<SysGrade> grades,
        IXjglRepository<SysUserPredefine> predefines,
        IChangeFlowPlanner planner,
        ISequenceService sequenceService,
        IStudentChangeLogger changeLogger,
        IChangeApplyAccessGuard applyGuard,
        IChangeAttachmentStore attachmentStore,
        IXjglTransactionScope tx,
        IClock clock,
        ILogger<ChangeFlowAppService> logger)
    {
        _applies = applies;
        _tasks = tasks;
        _logs = logs;
        _types = types;
        _templates = templates;
        _nodes = nodes;
        _attachments = attachments;
        _students = students;
        _studentsForUpdate = students;
        _orgs = orgs;
        _grades = grades;
        _predefines = predefines;
        _planner = planner;
        _sequenceService = sequenceService;
        _changeLogger = changeLogger;
        _applyGuard = applyGuard;
        _attachmentStore = attachmentStore;
        _tx = tx;
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

        return await BuildApplyDtoAsync(apply);
    }

    [Authorize(MetadataPermissions.Chg.Apply.Query)]
    public async Task<PagedResultDto<ChangeApplyListItemDto>> GetAppliesAsync(ChangeApplyQueryDto input)
    {
        // ★ 数据范围作为**叠加**在业务条件之上的一层约束（此前完全没有）。
        //
        // 在这之前，`GetAppliesAsync` 只按调用方传来的过滤条件查，而 `BuildQueryPredicate`
        // 在**一个过滤条件都不传**时返回 null（= 查全部）—— 于是任何持有 Chg.Apply.Query
        // 的用户都能拉到全省的异动单据（学生姓名、学籍辅号、转出/转入学校、申请单号）。
        // 这正是本项目为学生 / 照片 / 用户预置 / 学籍留痕都修过的那一类越权，
        // 异动模块当时被漏掉了。判断统一收在 ChangeApplyAccessGuard 里
        // （附件是独立路由，各写一份迟早漏掉一处）。
        //
        // 注意 null 与空集合的区别：null = 不受限（省厅 / admin），
        // 空集合 = 未关联组织的用户，**什么都看不到**。
        var visibleOrgIds = await _applyGuard.GetVisibleOrgIdsAsync();

        var predicate = BuildQueryPredicate(input, visibleOrgIds);

        var applies = await _applies.GetPagedListAsync(
            predicate, input.Sorting ?? "ApplyDate DESC", input.SkipCount, input.MaxResultCount);

        var total = await _applies.GetCountAsync(predicate);

        var items = await ToListItemsAsync(applies);

        await FillCurrentNodeAsync(items);

        return new PagedResultDto<ChangeApplyListItemDto>(total, items);
    }

    /// <summary>
    /// 给列表项补上「当前节点 / 当前审核单位」。
    ///
    /// 【这是一个真实缺陷的修复】`GetAppliesAsync` 原来**只填 CurrentSeq、不填节点名**，
    /// 而节点名只在待办查询（`GetTodoListAsync`）里被填过 —— 于是：
    ///   - 异动申请页的"当前节点"那一列**永远是空的**；
    ///   - 审核页"全部申请"页签同样空（只有"待办"页签有值）。
    /// 界面上不报错、也不缺列，只是一个空着的格子，所以一直没人发现 ——
    /// 而这恰恰是用户最关心的信息（"办到哪一步了"）。
    ///
    /// 实现上一次性把这一页的待办查回来（不按申请单逐个查：那是 N+1）。
    /// </summary>
    private async Task FillCurrentNodeAsync(List<ChangeApplyListItemDto> items)
    {
        var pending = items
            .Where(i => i.CurrentSeq.HasValue)
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        var applyIds = pending.Select(i => i.Id).ToList();

        var tasks = await _tasks.GetListAsync(
            t => applyIds.Contains(t.ApplyId) && t.Status == ChangeTaskStatus.Pending);

        var byApplyAndSeq = tasks.ToDictionary(t => (t.ApplyId, t.Seq), t => t);

        foreach (var item in pending)
        {
            if (byApplyAndSeq.TryGetValue((item.Id, item.CurrentSeq!.Value), out var task))
            {
                item.CurrentNodeName = task.NodeName;
                item.CurrentOrgName = task.OrgName;
            }
        }
    }

    /// <summary>
    /// 申请单详情。
    ///
    /// 【为什么这里不挂 `[Authorize(Chg.Apply.Query)]`】
    /// 审核页点开一条待办走的也是这个接口，而审核员手上可能只有
    /// <c>Chg.Audit.*</c>（没有 <c>Chg.Apply.Query</c>）—— 挂单一权限码会让
    /// "有审核权限却打不开任何一条"，于是本方法只要求登录，
    /// 真正的读权限判断交给 <see cref="IChangeApplyAccessGuard.EnsureReadPermissionAsync"/>
    /// （申请查询 **或** 审核查询，任一即可）。
    ///
    /// 【为什么内部调用不能走这个方法】提交 / 撤回 / 批准 / 驳回 / 撤销审核
    /// 都会在最后返回一份最新详情，而它们的调用人只需要各自的**写**权限
    /// （例如只授了 <c>Chg.Apply.Create</c> 的申请人）。若内部也走这里，
    /// 这些动作会在返回详情这一步被读权限挡住 —— 前面已经改完了库，
    /// 用户看到的却是 403。因此 DTO 的拼装单独抽成
    /// <see cref="BuildApplyDtoAsync"/>，内部调用它。
    /// </summary>
    [Authorize]
    public async Task<ChangeApplyDto> GetApplyAsync(Guid id)
    {
        await _applyGuard.EnsureReadPermissionAsync();

        // ★ 单条读取同样要过数据范围（此前完全没有）。
        //
        // 申请单 Id 是可枚举/可猜测的，"知道 Id 就放行"等于没有数据范围。
        // 越权时抛 Xjgl:00204 而不是返回 null/404，理由与学生模块一致：
        // 调用方需要能区分「这张单子存在但你看不到」与「这张单子不存在」
        // （StudentAccessGuard 同一取舍）。
        var apply = await _applyGuard.GetInScopeAsync(id);

        return await BuildApplyDtoAsync(apply);
    }

    /// <summary>把一张申请单（实体）拼成详情 DTO；不含权限与数据范围判断。</summary>
    private async Task<ChangeApplyDto> BuildApplyDtoAsync(StuChangeApply apply)
    {
        var id = apply.Id;
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

        // 附件情况：审核人要在详情里一眼看到"材料齐没齐"，而不是自己去数附件
        await FillAttachmentInfoAsync(dto, apply);

        return dto;
    }

    /// <summary>
    /// 填充申请单详情上的附件信息。
    ///
    /// 【为什么不放在列表 DTO 上】列表页一屏 20 条，逐条数附件会变成 N+1；
    /// 而"材料齐不齐"只在**详情 / 审核**这个场景里真正被看。
    /// </summary>
    private async Task FillAttachmentInfoAsync(ChangeApplyDto dto, StuChangeApply apply)
    {
        var count = await _attachments.GetCountAsync(a => a.ApplyId == apply.Id);
        var changeType = await _types.FindAsync(apply.ChangeTypeId);

        dto.AttachmentCount = (int)count;
        dto.RequireAttachment = changeType?.RequireAttachment ?? false;

        // 不需要附件的类型恒为"已满足"：前端不必再判一次 RequireAttachment
        dto.AttachmentRequirementSatisfied = !dto.RequireAttachment || count > 0;
    }

    [Authorize(MetadataPermissions.Chg.Apply.Update)]
    public async Task<ChangeApplyDto> WithdrawAsync(Guid id)
    {
        // ★ 数据范围同样管到撤回：不能跨组织撤别人的单子
        var apply = await _applyGuard.GetInScopeAsync(id);

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

        // 撤回同样要终止流程：把还没处理的节点全部置为已跳过，
        // 否则已撤回的申请单里还挂着一串"待处理"节点（虽然不会再出现在待办里）。
        await SkipPendingTasksAsync(apply.Id);

        await WriteLogAsync(apply.Id, null, null, ChangeFlowActions.Withdraw, "撤回申请");

        return await BuildApplyDtoAsync(apply);
    }

    [Authorize(MetadataPermissions.Chg.Apply.Delete)]
    public async Task DeleteAsync(Guid id)
    {
        // ★ 数据范围同样管到删除：不能跨组织删别人的单子
        var apply = await _applyGuard.GetInScopeAsync(id);

        if (apply.Status is not (ChangeApplyStatus.Draft or ChangeApplyStatus.Withdrawn
            or ChangeApplyStatus.Rejected))
        {
            throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                .WithData("reason", "只有草稿、已撤回或已驳回的申请单可以删除");
        }

        // ★ 附件先清理，再删主档。
        //
        // 只软删附件记录是不够的：附件记录与学生照片不同 —— 照片记录要跟着学生档案
        // 保留（缺照片台账以主表为准），而附件是**申请单专属的原始材料**：
        // 申请单都没了，留着文件和记录只会让共享卷上堆出无人认领的孤儿
        // （而且再没有任何入口能看到或清理它们）。
        // 顺序是"先删文件、后软删记录"：反过来的话，文件删除失败时记录已经消失，
        // 那条路径就永远追不回来了。
        await DeleteAttachmentsAsync(apply.Id);

        await _applies.DeleteAsync(apply, autoSave: true);

        var tasks = await _tasks.GetListAsync(t => t.ApplyId == id);
        if (tasks.Count > 0)
        {
            await _tasks.DeleteManyAsync(tasks.Select(t => t.Id), autoSave: true);
        }
    }

    /// <summary>
    /// 删除申请单名下的全部附件（文件 + 记录）。
    ///
    /// 与 `StudentAppService.CascadeSoftDeleteAsync` 同一形状（先取 Id 再批量删），
    /// 区别只在于这里**连文件一起删**——理由见调用点。
    /// 单个文件删不掉只记警告：一条永远删不掉的附件不该让"删除申请单"整个失败，
    /// 而且放任记录留下会让用户以为单据还在。
    /// </summary>
    private async Task DeleteAttachmentsAsync(Guid applyId)
    {
        var attachments = await _attachments.GetListAsync(a => a.ApplyId == applyId);

        if (attachments.Count == 0)
        {
            return;
        }

        foreach (var attachment in attachments)
        {
            try
            {
                await _attachmentStore.DeleteAsync(attachment.RelativePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "删除异动申请单 {ApplyId} 时，附件文件 {RelativePath} 删除失败，已留下孤儿文件。",
                    applyId, attachment.RelativePath);
            }
        }

        await _attachments.DeleteManyAsync(attachments.Select(a => a.Id), autoSave: true);
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

        // ★ 数据范围：待办也不能跨组织看，且**请求里的 OrgId 不可信**。
        //
        // 原来 `input.OrgId` 是"传什么就按什么查"——县 A 的账号传一个县 B 的
        // 组织 Id，就能读到县 B 的全部待办（谁的学生、什么异动、办到哪一步）。
        // 审批动作本身有 TaskNotAssignedToYou 兜底，所以这是**读侧泄露**，
        // 但泄露的同样是学生姓名与学籍辅号，必须堵住。
        // 叠加范围条件之后，"传范围外的 OrgId"自然得到空页（上面那句已经把这个
        // 组织之外的任务排除了），不需要额外写一个 if。
        var visibleOrgIds = await _applyGuard.GetVisibleOrgIdsAsync();

        if (visibleOrgIds != null)
        {
            tasks = tasks.Where(t => visibleOrgIds.Contains(t.OrgId)).ToList();
        }

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
        // ★ 债 1：整段审核动作包在一个事务里。
        //
        // 这一段至少要写 4 张表：流程任务 → 流程日志 → 申请单 → 学生主档（+ 学籍留痕）。
        // 在此之前每一步都是 `autoSave: true` 各自提交，于是"最后一步之前崩掉"
        // 会留下**半成品状态**，而其中最难查的一种是：
        //   申请单已是「已通过」、流程也归档了，但**学生的学籍状态没改** ——
        //   界面上显示审批完成，学生档案却还是原样，而且没有任何报错。
        //
        // 现在全成或全败。注意范围：这是 **SqlSugar 侧**的原子性（业务表之间）；
        // 跨到 EF Core 框架表的原子性见 IXjglTransactionScope 的说明（需要 Outbox）。
        ChangeApplyDto result = null!;

        await _tx.ExecuteAsync(async () =>
        {
            result = await ApproveCoreAsync(input);
        });

        return result;
    }

    private async Task<ChangeApplyDto> ApproveCoreAsync(ChangeAuditActionDto input)
    {
        var (apply, current, tasks) = await LoadCurrentTaskAsync(input.ApplyId);

        // ★ 「必须上传证明材料」的闸门放在**这里**：所有写操作之前。
        // 位置很重要 —— 拦在写之前，"批准被拒"之后申请单必须原样停在原处，
        // 不能出现"报错了但状态已经改了"（与 AllowReject 那条同一要求）。
        await EnsureAttachmentRequirementAsync(apply, tasks);

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

        return await BuildApplyDtoAsync(apply);
    }

    [Authorize(MetadataPermissions.Chg.Audit.Approve)]
    public async Task<ChangeApplyDto> RejectAsync(ChangeAuditActionDto input)
    {
        var (apply, current, _) = await LoadCurrentTaskAsync(input.ApplyId);

        // 流程模板里可以把某个节点设为「不允许驳回」（例如只负责登记备案的节点）。
        // 这条原来只是模板上的一个字段：存得下、界面没有、审核时也不看 ——
        // 于是"配置了也没用"。现在真的拦在这里。
        if (!await IsRejectAllowedAsync(apply, current))
        {
            throw new BusinessException(XjglDomainErrorCodes.RejectNotAllowed)
                .WithData("reason", $"节点「{current.NodeName}」在流程模板里被设为不允许驳回，请改用通过或撤销审核。");
        }

        current.Status = ChangeTaskStatus.Rejected;
        current.HandledByUserId = CurrentUser.Id;
        current.HandledByName = CurrentUser.UserName;
        current.HandledTime = _clock.Now;
        current.Opinion = input.Opinion;

        await _tasks.UpdateAsync(current, autoSave: true);

        // 驳回即终止流程：把后续未处理的节点统一置为「已跳过」。
        // 不这么做的话，申请单已经是"已驳回"，详情里却还挂着几个"待处理"节点 ——
        // 用户会以为还要有人继续审，而这些节点永远不会再被处理。
        // （ChangeTaskStatus.Skipped 就是为这种场景准备的。）
        await SkipPendingTasksAsync(apply.Id, current.Seq);

        apply.Status = ChangeApplyStatus.Rejected;
        apply.CurrentSeq = null;

        await _applies.UpdateAsync(apply, autoSave: true);

        await WriteLogAsync(apply.Id, current.Id, current.Seq, ChangeFlowActions.Reject,
            input.Opinion ?? "审核驳回");

        return await BuildApplyDtoAsync(apply);
    }

    /// <summary>
    /// 「必须上传证明材料」的异动类型：没有附件就不许批准。
    ///
    /// 【为什么是「批准前」而不是「提交时」】
    /// 提交时申请单**还不存在**（`applyId` 是提交那一刻生成的），客户端根本无从
    /// 先上传附件。要按"提交时必须带附件"来做，就得先引入"草稿"阶段
    /// （创建 → 传附件 → 提交），那是一次流程改造，而不是加一个开关。
    /// 拦在批准前既让规则是真的（没材料就批不了），也符合"先提交、后补材料"的实际做法。
    ///
    /// 【为什么只看「第一次批准」】
    /// 第一次批准一旦发生，就说明当时材料是齐的；后续节点再查一次没有额外价值
    /// （材料只在必要时由经办人补，不强制每一步都重新数一遍）。
    /// 判据是"这张单子上还没有任何一个节点被批准"，而不是"当前节点序号最小"——
    /// 后者会在撤销审核（节点退回待处理）后把已放行过的单子重新卡住。
    ///
    /// 【查不到异动类型时放行】与 `IsRejectAllowedAsync` 同一取舍：
    /// 历史数据对不上时不该让审核功能整体卡死，权限与状态机仍在把关。
    /// </summary>
    private async Task EnsureAttachmentRequirementAsync(
        StuChangeApply apply,
        List<StuChangeFlowTask> tasks)
    {
        var changeType = await _types.FindAsync(apply.ChangeTypeId);

        if (changeType == null || !changeType.RequireAttachment)
        {
            return;
        }

        if (tasks.Any(t => t.Status == ChangeTaskStatus.Approved))
        {
            return;
        }

        if (await _attachments.AnyAsync(a => a.ApplyId == apply.Id))
        {
            return;
        }

        throw new BusinessException(XjglDomainErrorCodes.ChangeAttachmentRequired)
            .WithData("reason",
                $"异动类型「{changeType.Name}」要求上传证明材料，但申请单「{apply.ApplyNo}」" +
                "还没有任何附件，不能批准。请先上传证明材料。");
    }

    /// <summary>
    /// 把某条申请单上「尚未处理」的节点置为已跳过。
    ///
    /// 用于流程被终止的场景（驳回、撤回）：申请单已经结束，
    /// 剩下的节点不该再以「待处理」的姿态出现在详情页里 ——
    /// 那会让用户以为还需要有人去审。
    /// </summary>
    /// <param name="afterSeq">只处理序号大于它的节点；为 <c>null</c> 表示全部。</param>
    private async Task SkipPendingTasksAsync(Guid applyId, int? afterSeq = null)
    {
        var hasAfter = afterSeq.HasValue;
        var after = afterSeq ?? 0;

        var pending = await _tasks.GetListAsync(t =>
            t.ApplyId == applyId
            && t.Status == ChangeTaskStatus.Pending
            && (!hasAfter || t.Seq > after));

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var task in pending)
        {
            task.Status = ChangeTaskStatus.Skipped;
        }

        await _tasks.UpdateManyAsync(pending, autoSave: true);
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

        return await BuildApplyDtoAsync(apply);
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

        // 先取快照：审批改的是学籍主档，同样要进变更流水
        // （异动自己的流程日志 `stu_change_flow_log` 记的是"审批动作"，
        //   这里记的是"学籍字段发生了什么变化"，两者互补）
        var before = _changeLogger.Snapshot(student);

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

        // 留痕来源写「学籍异动」并把申请单号带进原因，便于从档案反查是哪一单引起的
        await _changeLogger.LogUpdatedAsync(
            student,
            before,
            source: "学籍异动",
            reason: $"异动申请 {apply.ApplyNo} 审核通过");

        _logger.LogInformation(
            "异动申请 {ApplyNo} 审核通过，学生 {StudentName} 学籍状态更新为 {Status}。",
            apply.ApplyNo, apply.StudentName, student.StudentStatusCode);
    }

    /// <summary>撤销已通过审核时，把学生恢复到申请单记录的「变动前」状态。</summary>
    private async Task RevertStudentChangesAsync(StuChangeApply apply)
    {
        var student = await _studentsForUpdate.GetAsync(apply.StudentId);

        var before = _changeLogger.Snapshot(student);

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

        await _changeLogger.LogUpdatedAsync(
            student,
            before,
            source: "学籍异动",
            reason: $"异动申请 {apply.ApplyNo} 撤销审核，学籍回滚");

        _logger.LogWarning(
            "异动申请 {ApplyNo} 撤销审核，学生 {StudentName} 的学籍变更已回滚。",
            apply.ApplyNo, apply.StudentName);
    }

    // ==================================================================
    // 内部实现
    // ==================================================================

    /// <summary>
    /// 当前待办节点是否允许驳回（流程模板节点上的 `AllowReject`，默认允许）。
    ///
    /// 【为什么要按模板节点回查，而不是存在待办行上】
    /// 待办（`StuChangeFlowTask`）只存实例化后的结果（节点名/组织/角色/级别），
    /// 没有冗余这些配置位。申请单上带着 `FlowTemplateId`，待办带着 `Seq`，
    /// 两者足以定位到当时的模板节点，因此不必为此加字段、加迁移。
    ///
    /// 【合并节点怎么办】
    /// 一个待办可能由多个配置节点**按组织去重合并**而来（见实例化算法），
    /// `MergedFromSeqs` 记着它的来源。判定取「全部来源都允许驳回」：
    /// 只要其中**任一**节点写明了不许驳回，这次驳回就会绕过那条设定 ——
    /// 宁可拒绝，不可放过。
    ///
    /// 【查不到模板节点时放行】
    /// 模板被删、或历史数据对不上时，不该让审核功能整体卡死。
    /// 权限与状态机仍在把关，所以这里退化为"允许"。
    /// </summary>
    private async Task<bool> IsRejectAllowedAsync(StuChangeApply apply, StuChangeFlowTask task)
    {
        var merged = task.GetMergedFromSeqs();

        // 合并来源里没有别的节点时，就是它自己
        var seqs = merged.Length > 0 ? merged : new[] { task.Seq };

        var nodes = await _nodes.GetListAsync(n => n.TemplateId == apply.FlowTemplateId);

        var involved = nodes.Where(n => seqs.Contains(n.Seq)).ToList();

        if (involved.Count == 0)
        {
            return true;
        }

        return involved.All(n => n.AllowReject);
    }

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

    /// <summary>
    /// 构造申请单列表的过滤条件。
    ///
    /// <paramref name="visibleOrgIds"/> 为当前用户数据范围覆盖的组织 Id；
    /// <c>null</c> 表示不受限（不加范围条件）。申请单上只冗余了学校的 **Id**
    /// （没有物化路径），所以范围过滤是"两次 IN"而不是前缀比较——
    /// 为什么这样选、以及更优的做法，见 <see cref="IChangeApplyAccessGuard.GetVisibleOrgIdsAsync"/>。
    ///
    /// 可见性口径：**转出校或转入校任一侧在范围内即可见**。
    /// 转学单同时牵涉两所学校，只认一侧会让另一侧的县区连自己的待办都点不开。
    /// </summary>
    private static System.Linq.Expressions.Expression<Func<StuChangeApply, bool>>? BuildQueryPredicate(
        ChangeApplyQueryDto input,
        List<Guid>? visibleOrgIds)
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

        var hasScope = visibleOrgIds != null;

        // 空集合也要能生成合法 SQL：`IN ()` 是语法错误（PostgreSQL 直接 42601）。
        // 塞一个 Guid.Empty 进去，谓词恒不命中，语义与"什么都看不到"一致。
        var scopeOrgIds = visibleOrgIds ?? new List<Guid>();

        if (scopeOrgIds.Count == 0)
        {
            scopeOrgIds.Add(Guid.Empty);
        }

        if (!hasType && !hasStudent && !hasStatus && !hasSchool && !hasKeyword && !hasScope)
        {
            return null;
        }

        return a =>
            (!hasType || a.ChangeTypeId == typeId) &&
            (!hasStudent || a.StudentId == studentId) &&
            (!hasStatus || a.Status == status) &&
            (!hasSchool || a.SourceSchoolOrgId == schoolId || a.TargetSchoolOrgId == schoolId) &&
            (!hasKeyword || a.ApplyNo.Contains(keyword!) || a.StudentName.Contains(keyword!)) &&
            // 数据范围：转出校或转入校任一侧命中即可见
            (!hasScope
             || scopeOrgIds.Contains(a.SourceSchoolOrgId)
             || (a.TargetSchoolOrgId != null && scopeOrgIds.Contains(a.TargetSchoolOrgId.Value)));
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
