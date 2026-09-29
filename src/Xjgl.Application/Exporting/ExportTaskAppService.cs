using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Timing;
using Volo.Abp.Users;
using Xjgl.Changes;
using Xjgl.Codes;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.RateLimiting;

namespace Xjgl.Exporting;

/// <summary>
/// 导出/打印任务的服务端入口。
///
/// 【两条贯穿全类的规则】
///   1. **入队只做两件事**：认权限、把参数快照存下来。它**不生成任何文件** ——
///      生成放到后台（ExportTaskWorker），否则就退回了"同步导出"的老问题。
///   2. **"不是我的"一律表现为"不存在"**。查单条、取消、删除、下载都按这条来。
///      返回 403 等于告诉调用方"这个 Id 是存在的"，而任务 Id 会被写进日志、
///      聊天记录、工单截图里 —— 那等于一个可枚举的信息泄露面。
/// </summary>
[Authorize]
public class ExportTaskAppService : XjglAppService, IExportTaskAppService
{
    private const string RetentionDaysConfigKey = "Xjgl:ExportTask:RetentionDays";

    private const int DefaultRetentionDays = 7;

    private readonly IXjglRepository<SysExportTask> _tasks;
    private readonly IExportArtifactStore _artifacts;
    private readonly IPermissionChecker _permissionChecker;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IConfiguration _configuration;
    private readonly ITokenBucketRateLimiter _rateLimiter;
    private readonly IRateLimitPolicyProvider _rateLimitPolicies;

    public ExportTaskAppService(
        IXjglRepository<SysExportTask> tasks,
        IExportArtifactStore artifacts,
        IPermissionChecker permissionChecker,
        ICurrentUser currentUser,
        IClock clock,
        IConfiguration configuration,
        ITokenBucketRateLimiter rateLimiter,
        IRateLimitPolicyProvider rateLimitPolicies)
    {
        _tasks = tasks;
        _artifacts = artifacts;
        _permissionChecker = permissionChecker;
        _currentUser = currentUser;
        _clock = clock;
        _configuration = configuration;
        _rateLimiter = rateLimiter;
        _rateLimitPolicies = rateLimitPolicies;
    }

    public async Task<ExportTaskDto> CreateAsync(CreateExportTaskDto input)
    {
        if (!ExportTaskPermissions.IsKnown(input.Kind))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", $"未知的任务种类「{input.Kind}」");
        }

        var userId = _currentUser.Id
                     ?? throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                         .WithData("reason", "未登录，无法提交导出任务");

        await EnsurePermittedAsync(input.Kind);
        await EnforceSubmitRateLimitAsync(userId);

        // 参数在入队时就序列化成快照：执行时再取一次的话，任务从入队到执行之间
        // 用户改了筛选条件，导出的就不是他点按钮时看到的那批数据了。
        var parametersJson = SerializeParameters(input);

        var retentionDays = Math.Max(1, _configuration.GetValue(RetentionDaysConfigKey, DefaultRetentionDays));

        var entity = new SysExportTask(
            GuidGenerator.Create(),
            input.Kind,
            userId,
            _currentUser.UserName,
            _currentUser.Roles is { Length: > 0 } roles ? string.Join(',', roles) : null,
            parametersJson)
        {
            ExpiresTime = _clock.Now.AddDays(retentionDays)
        };

        await _tasks.InsertAsync(entity, autoSave: true);

        return ToDto(entity);
    }

    public async Task<ExportTaskDto> GetAsync(Guid id)
    {
        return ToDto(await GetOwnedAsync(id));
    }

    public async Task<PagedResultDto<ExportTaskDto>> GetListAsync(ExportTaskQueryDto input)
    {
        var userId = _currentUser.Id;

        if (userId == null)
        {
            return new PagedResultDto<ExportTaskDto>(0, new List<ExportTaskDto>());
        }

        var kind = input.Kind;
        var status = input.Status;
        var ownerId = userId.Value;

        // 恒定按发起人过滤：这个列表接口**没有**"看别人的任务"的用法，
        // 而一旦漏掉这个条件，产物下载的授权就形同虚设（能列出就能猜到 Id）。
        //
        // 必须是 Expression 而不是 Func：仓储的谓词要交给 SqlSugar 翻译成 SQL，
        // 传 Func 会退化成"把全表拉回内存再过滤"——数据量一上来就是灾难。
        System.Linq.Expressions.Expression<Func<SysExportTask, bool>> predicate = t =>
            t.RequestedByUserId == ownerId
            && (kind == null || t.Kind == kind)
            && (status == null || t.Status == status);

        var total = await _tasks.GetCountAsync(predicate);

        var items = await _tasks.GetPagedListAsync(
            predicate,
            input.Sorting ?? "CreationTime DESC",
            input.SkipCount,
            input.MaxResultCount);

        return new PagedResultDto<ExportTaskDto>(total, items.Select(ToDto).ToList());
    }

    public async Task<ExportTaskDto> CancelAsync(Guid id)
    {
        var entity = await GetOwnedAsync(id);

        if (entity.Status == ExportTaskStatuses.Running)
        {
            // 渲染/打包是 CPU 密集的一次性动作，中途打断只会留下半个产物，
            // 而清理它比让它跑完更麻烦。让用户等它结束（或直接删掉任务）。
            throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                .WithData("reason", "任务正在执行，无法取消；请等它结束后删除");
        }

        if (entity.IsTerminal())
        {
            return ToDto(entity);
        }

        entity.Status = ExportTaskStatuses.Canceled;
        entity.FinishedTime = _clock.Now;
        entity.Message = "已取消";

        await _tasks.UpdateAsync(entity, autoSave: true);

        return ToDto(entity);
    }

    public async Task DeleteAsync(Guid id)
    {
        var entity = await GetOwnedAsync(id);

        // 先删产物再删记录：反过来的话，删记录成功而删文件失败会留下一个
        // 谁也找不到、谁也清理不掉的孤儿文件（保留期清理依赖记录里的路径）。
        if (!string.IsNullOrWhiteSpace(entity.RelativeArtifactPath))
        {
            await _artifacts.DeleteAsync(entity.RelativeArtifactPath!);
        }

        await _tasks.DeleteAsync(entity, autoSave: true);
    }

    /// <summary>
    /// 取一条**属于当前用户**的任务；不存在或不属于自己都抛同一个"实体不存在"。
    ///
    /// 【为什么用 EntityNotFoundException 而不是 BusinessException】
    ///   ABP 把 BusinessException 映射成 **403**，把 EntityNotFoundException 映射成 **404**。
    ///   这里用 404 有两个理由：
    ///     1) 语义正确 —— 对调用方而言"不是我的任务"就是"查不到这个任务"；
    ///     2) 与下载接口一致 —— 手写控制器那边返回的也是 404，
    ///        两处口径不同会让前端与排查的人以为是两类问题。
    ///   实测过：早先版本用 BusinessException 时，查一条已删除的任务返回的是 403。
    ///
    /// 两种情况合并成同一个异常是刻意的：分开报的话，攻击者可以用
    /// "任务不存在"与"无权访问"的差异枚举出系统里有哪些任务 Id。
    /// </summary>
    private async Task<SysExportTask> GetOwnedAsync(Guid id)
    {
        var entity = await _tasks.FindAsync(id);

        if (entity == null || entity.RequestedByUserId != _currentUser.Id)
        {
            throw new EntityNotFoundException(typeof(SysExportTask), id);
        }

        return entity;
    }

    /// <summary>
    /// 按种类校验权限。
    ///
    /// 用"逐个都必须通过"而不是 <c>IsGrantedAsync(string[])</c>：
    /// 后者是**任意其一**即通过，而照片导出要求 Export 与 Query **同时**具备
    /// （导出内部复用台账查询）。用错语义的后果是：只有 Export 的人能提交任务，
    /// 然后在后台线程里炸出 403 —— 任务失败，而失败原因跟权限有关又看不出来。
    /// </summary>
    /// <summary>
    /// 提交任务前的限流。
    ///
    /// 【为什么要限"提交"这件看起来很便宜的事】
    /// 入队本身只写一行记录，但它**排的是一个串行的队列**：worker 按顺序消费，
    /// 一次渲染可能要几秒到几十秒。所以一个人连点几十下不会拖慢他自己，
    /// 而是把全校其他人的导出都堵在他后面 —— 这是"便宜接口导致的昂贵后果"，
    /// 单看接口耗时永远发现不了。
    ///
    /// 这里只用**用户**维度：导出任务天然带 userId，而 IP 维度对"同一个人刷"没有帮助；
    /// 要覆盖"某人用多个账号刷"，靠的是 IP 维度由控制器层（XjglController）统一加。
    ///
    /// 【为什么抛 BusinessException 而不是返回 429】
    /// 应用服务没有 HTTP 上下文。错误码 <see cref="XjglDomainErrorCodes.RateLimitExceeded"/>
    /// 在 Host 里被显式映射成 429（见 ConfigureRateLimiting），
    /// 因此 HTTP 层拿到的仍是标准的"稍后重试"语义，而不是笼统的 403。
    /// </summary>
    private async Task EnforceSubmitRateLimitAsync(Guid userId)
    {
        var policy = _rateLimitPolicies.Get(RateLimitPolicies.ExportTask.Name);

        var decision = await _rateLimiter.AcquireAsync(policy, $"{policy.Name}:user:{userId}");

        if (decision.IsAllowed)
        {
            return;
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));

        throw new BusinessException(XjglDomainErrorCodes.RateLimitExceeded)
            .WithData("retryAfterSeconds", seconds)
            .WithData("reason", $"提交导出任务过于频繁，请 {seconds} 秒后再试。");
    }

    private async Task EnsurePermittedAsync(string kind)
    {
        foreach (var permission in ExportTaskPermissions.GetRequiredPermissions(kind))
        {
            if (!await _permissionChecker.IsGrantedAsync(permission))
            {
                throw new BusinessException(XjglDomainErrorCodes.PermissionNotOwned)
                    .WithData("reason", $"没有「{ExportTaskDisplayNames.GetPermissionName(permission)}」权限，无法提交该任务")
                    .WithData("permission", permission);
            }
        }
    }

    private static string SerializeParameters(CreateExportTaskDto input)
    {
        // 只序列化与该种类相关的那一份参数：全存的话，
        // 前端某次多传了一个无关字段就会被一并固化进快照，将来读的人无法判断哪个才是准的。
        object? payload = input.Kind switch
        {
            ExportTaskKinds.StudentCsv => input.StudentQuery
                ?? throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                    .WithData("reason", "学生名单导出缺少查询条件"),

            ExportTaskKinds.StudentRosterPdf => input.RosterQuery
                ?? throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                    .WithData("reason", "花名册打印缺少查询条件"),

            ExportTaskKinds.StudentCardPdf => input.StudentId
                ?? throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                    .WithData("reason", "学籍卡打印缺少学生 Id"),

            ExportTaskKinds.PhotoZip => input.PhotoQuery
                ?? throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                    .WithData("reason", "照片打包导出缺少查询条件"),

            // 组织导出允许**不带条件**（导出全部）：OnlyEnabled 默认 false，
            // 因为"导出"通常是为了搬运/备份，把停用的组织漏掉反而是意外。
            ExportTaskKinds.OrganizationCsv => input.OrgQuery ?? new OrgQueryDto { OnlyEnabled = false },

            ExportTaskKinds.CodeCsv => input.CodeQuery ?? new CodeExportQueryDto(),

            // 异动两种导出共用同一份查询条件（申请单清单 / 它的审核流水）
            ExportTaskKinds.ChangeApplyCsv or ExportTaskKinds.ChangeAuditCsv
                => input.ChangeQuery ?? new ChangeApplyQueryDto(),

            _ => null
        };

        return JsonSerializer.Serialize(payload, payload!.GetType());
    }

    private static ExportTaskDto ToDto(SysExportTask entity)
    {
        return new ExportTaskDto
        {
            Id = entity.Id,
            Kind = entity.Kind,
            KindName = ExportTaskKinds.GetDisplayName(entity.Kind),
            Status = entity.Status,
            FileName = entity.FileName,
            ContentType = entity.ContentType,
            FileSize = entity.FileSize,
            Total = entity.Total,
            RowCount = entity.RowCount,
            Processed = entity.Processed,
            IsTruncated = entity.IsTruncated,
            Message = entity.Message,
            FailureReason = entity.FailureReason,
            StartedTime = entity.StartedTime,
            FinishedTime = entity.FinishedTime,
            ExpiresTime = entity.ExpiresTime,
            CreationTime = entity.CreationTime,
            CanDownload = entity.Status == ExportTaskStatuses.Succeeded
                           && !string.IsNullOrWhiteSpace(entity.RelativeArtifactPath)
        };
    }
}

/// <summary>权限码 → 给人看的名字（报错信息用）。</summary>
internal static class ExportTaskDisplayNames
{
    public static string GetPermissionName(string permission) => permission switch
    {
        "Stu.Student.Export" => "学生导出",
        "Stu.Student.Print" => "学籍打印",
        "Pho.Photo.Export" => "照片导出",
        "Pho.Photo.Query" => "照片查询",
        _ => permission
    };
}
