using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Volo.Abp.Security.Claims;
using Volo.Abp.Timing;
using Xjgl.Data;
using Xjgl.SqlSugarCore;

namespace Xjgl.Exporting;

/// <summary>
/// 导出/打印任务的后台执行器。
///
/// 形态照抄 <c>PhotoCompensationWorker</c>（每轮开新作用域、异常必须自己吞掉），
/// 但多了一件它不需要做的事：**冒充发起人**。
///
/// 【为什么必须冒充 —— 这批最容易做错的地方】
///   数据范围（`UserDataScopeProvider`）是从 `ICurrentUser` 推出来的，而
///   `ICurrentUser` 又来自 `ICurrentPrincipalAccessor`。后台线程里两者都是空的，
///   于是 `UserDataScope` 会被解析成 `Empty` —— 导出安静地变成**空文件**。
///   更糟的"修法"是以超管身份跑：那会让**任何人**的导出都绕过数据范围，
///   变成"界面看不到、导出拿得到"的越权。两种错法都不会报错。
///   所以：以发起人身份跑；解不出发起人就**让任务失败**，绝不降级成不受限。
///
/// 【为什么每个任务单独开一个 DI 作用域】
///   `UserDataScopeProvider` 是 **Scoped 且带缓存**的（同一作用域内只查一次库）。
///   若一轮里在同一个作用域里连着跑 5 个不同发起人的任务，
///   第 2 个任务会复用第 1 个人的数据范围 —— 那是跨用户的越权，而且只在
///   "同一轮里有多个任务"时才出现，测试很容易漏。
/// </summary>
public class ExportTaskWorker : BackgroundService
{
    /// <summary>一轮最多处理几个任务。取小值是为了让新任务不至于排在长队列后面等太久。</summary>
    private const int MaxTasksPerRound = 5;

    /// <summary>用于冒充身份的认证类型，仅是 ClaimsIdentity 的标记。</summary>
    private const string ImpersonationAuthType = "Xjgl.ExportTask.Impersonation";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExportTaskWorker> _logger;

    public ExportTaskWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ExportTaskWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Xjgl:ExportTask:Enabled", true))
        {
            _logger.LogInformation("[导出任务] 后台任务已通过配置关闭。");
            return;
        }

        var interval = TimeSpan.FromSeconds(
            Math.Max(2, _configuration.GetValue("Xjgl:ExportTask:IntervalSeconds", 5)));

        var initialDelay = TimeSpan.FromSeconds(
            Math.Max(0, _configuration.GetValue("Xjgl:ExportTask:InitialDelaySeconds", 10)));

        _logger.LogInformation(
            "[导出任务] 后台任务已启动：首次延迟 {Delay}，之后每 {Interval} 执行一次。",
            initialDelay, interval);

        try
        {
            await Task.Delay(initialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);

        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await ExpireArtifactsAsync(stoppingToken);

            var claimed = await ClaimPendingAsync(stoppingToken);

            foreach (var taskId in claimed)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                await ExecuteTaskAsync(taskId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机
        }
        catch (Exception ex)
        {
            // 绝不能把异常抛出去：BackgroundService 的异常会让整个主机停机
            _logger.LogError(ex, "[导出任务] 本轮执行异常，将在下个周期重试。");
        }
    }

    /// <summary>
    /// 抢占若干 Pending 任务。
    ///
    /// 用「带条件的 UPDATE」而不是"先读后写"：多实例部署时两个实例可能同时读到同一条
    /// Pending，各自跑一遍 —— 结果是同一个任务被渲染两次、产物互相覆盖。
    /// 条件更新让数据库来做仲裁：只有把状态从 Pending 改成 Running 成功的那一方才认领到。
    /// </summary>
    private async Task<List<Guid>> ClaimPendingAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var repository = scope.ServiceProvider.GetRequiredService<ISqlSugarRepository<SysExportTask>>();

        // 时间一律走 IClock，不直接用 DateTime.UtcNow：ExpiresTime 是入队时用
        // IClock.Now 写下的，而 ABP 默认的 IClock.Now 是**本地时间**（本仓库没有
        // 配置 AbpClockOptions.Kind）。两处混用会让过期判断整整偏一个时区
        // （东八区下晚 8 小时才清理），开始/结束时间也会与创建时间对不上。
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var pending = await repository.GetPagedListAsync(
            t => t.Status == ExportTaskStatuses.Pending,
            "CreationTime ASC",
            0,
            MaxTasksPerRound,
            stoppingToken);

        var claimed = new List<Guid>();
        var now = clock.Now;

        foreach (var task in pending)
        {
            var affected = await repository.Client
                .Updateable<SysExportTask>()
                .SetColumns(t => new SysExportTask
                {
                    Status = ExportTaskStatuses.Running,
                    StartedTime = now
                })
                .Where(t => t.Id == task.Id && t.Status == ExportTaskStatuses.Pending)
                .ExecuteCommandAsync();

            if (affected > 0)
            {
                claimed.Add(task.Id);
            }
            else
            {
                // 被别人抢走了（或多实例下的重复扫描），不是异常
                _logger.LogDebug("[导出任务] 任务 {TaskId} 已被其它实例认领。", task.Id);
            }
        }

        return claimed;
    }

    private async Task ExecuteTaskAsync(Guid taskId, CancellationToken stoppingToken)
    {
        // 每个任务一个独立作用域：数据范围解析器是 Scoped 且带缓存的，
        // 共用作用域会让上一个任务发起人的范围泄漏到下一个任务。
        using var scope = _scopeFactory.CreateScope();

        var provider = scope.ServiceProvider;
        var repository = provider.GetRequiredService<IXjglRepository<SysExportTask>>();
        var runner = provider.GetRequiredService<IExportTaskRunner>();
        var principalAccessor = provider.GetRequiredService<ICurrentPrincipalAccessor>();
        var clock = provider.GetRequiredService<IClock>();

        var task = await repository.FindAsync(taskId, stoppingToken);

        if (task == null)
        {
            return;
        }

        try
        {
            var principal = BuildImpersonationPrincipal(task);

            ExportTaskRunResult result;

            // 冒充发起人：这一段之内的 ICurrentUser / 数据范围都按发起人算
            using (principalAccessor.Change(principal))
            {
                result = await runner.RunAsync(task, stoppingToken);
            }

            task.Status = ExportTaskStatuses.Succeeded;
            task.FileName = result.FileName;
            task.ContentType = result.ContentType;
            task.RelativeArtifactPath = result.RelativeArtifactPath;
            task.FileSize = result.FileSize;
            task.Total = result.Total;
            task.RowCount = result.RowCount;
            task.Processed = result.RowCount;
            task.IsTruncated = result.IsTruncated;
            task.Message = result.Message;
            task.FailureReason = null;
            task.FinishedTime = clock.Now;

            await repository.UpdateAsync(task, autoSave: true, cancellationToken: stoppingToken);

            _logger.LogInformation(
                "[导出任务] {Kind} 完成：发起人 {User}，{Rows}/{Total} 行，{Bytes} 字节{Truncated}。",
                task.Kind, task.RequestedByUserId, task.RowCount, task.Total, task.FileSize,
                task.IsTruncated ? "（已截断）" : string.Empty);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停机：把任务退回 Pending，下次启动继续跑，而不是让它永远卡在 Running
            await MarkFailedAsync(repository, task, "服务停止，任务已中断，将在下次启动时重试", clock, stoppingToken, backToPending: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[导出任务] {TaskId} 执行失败。", taskId);

            await MarkFailedAsync(repository, task, Describe(ex), clock, stoppingToken);
        }
    }

    private async Task MarkFailedAsync(
        IXjglRepository<SysExportTask> repository,
        SysExportTask task,
        string reason,
        IClock clock,
        CancellationToken stoppingToken,
        bool backToPending = false)
    {
        try
        {
            task.Status = backToPending ? ExportTaskStatuses.Pending : ExportTaskStatuses.Failed;
            task.FailureReason = reason;
            task.StartedTime = backToPending ? null : task.StartedTime;
            task.FinishedTime = backToPending ? null : clock.Now;

            await repository.UpdateAsync(task, autoSave: true, cancellationToken: stoppingToken);
        }
        catch (Exception ex)
        {
            // 连"记录失败"都失败了，只能留日志 —— 但绝不能再抛出去
            _logger.LogError(ex, "[导出任务] 回写失败状态时又出错，任务 {TaskId} 可能停在 Running。", task.Id);
        }
    }

    /// <summary>
    /// 清理过期产物。
    ///
    /// 只清文件、不动"已经失败/取消"的状态：状态是给人看的事实，
    /// 而这里要解决的只是磁盘占用。
    /// </summary>
    private async Task ExpireArtifactsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var repository = scope.ServiceProvider.GetRequiredService<IXjglRepository<SysExportTask>>();
        var artifacts = scope.ServiceProvider.GetRequiredService<IExportArtifactStore>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var now = clock.Now;

        var expired = await repository.GetPagedListAsync(
            t => t.ExpiresTime != null && t.ExpiresTime <= now && t.RelativeArtifactPath != null,
            "ExpiresTime ASC",
            0,
            50,
            stoppingToken);

        foreach (var task in expired)
        {
            try
            {
                await artifacts.DeleteAsync(task.RelativeArtifactPath!, stoppingToken);

                task.RelativeArtifactPath = null;

                // 成功的任务过期后状态改成 Expired，让界面能说清"文件过期了"；
                // 失败/取消的任务保持原状态，它们本来就没有可下载的东西。
                if (task.Status == ExportTaskStatuses.Succeeded)
                {
                    task.Status = ExportTaskStatuses.Expired;
                    task.Message = "产物已过保留期并被清理";
                }

                await repository.UpdateAsync(task, autoSave: true, cancellationToken: stoppingToken);

                _logger.LogInformation("[导出任务] 已清理过期产物：{TaskId}", task.Id);
            }
            catch (Exception ex)
            {
                // 单个文件删不掉不该影响其它任务（Windows 上"文件被占用"很常见）
                _logger.LogWarning(ex, "[导出任务] 清理产物失败：{TaskId}", task.Id);
            }
        }
    }

    /// <summary>
    /// 构造冒充用的主体。
    ///
    /// **角色声明不能省**：数据范围解析里有一条超管旁路
    /// （`_currentUser.Roles.Contains("admin")`），只带 userId 不带角色的话，
    /// admin 发起的导出反而会被判成"未关联组织"→ 什么都看不到，
    /// 于是超级管理员的导出是空的，而普通用户的却正常 —— 极难理解的现象。
    /// </summary>
    private static ClaimsPrincipal BuildImpersonationPrincipal(SysExportTask task)
    {
        if (task.RequestedByUserId == Guid.Empty)
        {
            // 宁可失败也不"以不受限身份跑"：这条分支的另一种写法就是越权。
            throw new InvalidOperationException(
                $"任务 {task.Id} 没有记录发起人，拒绝执行 —— 无法确定数据范围，以不受限身份执行会造成越权。");
        }

        var claims = new List<Claim>
        {
            new(AbpClaimTypes.UserId, task.RequestedByUserId.ToString())
        };

        if (!string.IsNullOrWhiteSpace(task.RequestedByName))
        {
            claims.Add(new Claim(AbpClaimTypes.UserName, task.RequestedByName!));
        }

        claims.AddRange(task.GetRequestedByRoles().Select(role => new Claim(AbpClaimTypes.Role, role)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, ImpersonationAuthType));
    }

    private static string Describe(Exception ex)
    {
        if (ex is Volo.Abp.BusinessException business && business.Data.Count > 0)
        {
            var reason = business.Data["reason"];

            if (reason != null)
            {
                return reason.ToString()!;
            }
        }

        return ex.Message;
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
