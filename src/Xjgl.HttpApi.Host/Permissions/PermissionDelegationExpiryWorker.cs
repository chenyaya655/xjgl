using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Volo.Abp.Timing;
using Xjgl.Data;
using Xjgl.Permissions;

namespace Xjgl.Permissions;

/// <summary>
/// 委派到期的回收任务（ROADMAP 第 19 条）。
///
/// 【为什么必须有它，而不是"读的时候判一下有没有过期"】
/// 委派生效靠的是 ABP 的**授权存储**（用户级授权），而 `[Authorize]` 只认那份存储、
/// 不认本项目的台账表。所以"到期"这件事必须在**到期之后就真的把授权删掉** ——
/// 只在查询时过滤台账，等于一条到期委派会**永远生效**（而且台账上看着是"已到期"，更危险）。
///
/// 【为什么不开事务、逐条处理】
/// 每条委派的回收是独立的（删一条用户级授权）；一条失败不该影响其它条。
/// 与照片补偿、缩略图补齐两个任务同理：这里也**吞掉所有异常** ——
/// `BackgroundService` 里漏出的异常在 .NET 6+ 会默认把整个主机停掉，
/// 而"委派回收失败"绝不能导致 Web 服务下线。
/// </summary>
public class PermissionDelegationExpiryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PermissionDelegationExpiryWorker> _logger;

    public PermissionDelegationExpiryWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PermissionDelegationExpiryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Xjgl:PermissionDelegation:ExpiryEnabled", true))
        {
            _logger.LogInformation("[委派回收] 后台任务已通过配置关闭。");
            return;
        }

        // 默认 5 分钟一轮：到期时间通常精确到天，"晚几分钟收回"没有实际影响，
        // 而跑得太勤只是白查库。
        var interval = TimeSpan.FromSeconds(
            Math.Max(30, _configuration.GetValue("Xjgl:PermissionDelegation:ExpiryIntervalSeconds", 300)));

        var initialDelay = TimeSpan.FromSeconds(
            Math.Max(0, _configuration.GetValue("Xjgl:PermissionDelegation:ExpiryInitialDelaySeconds", 60)));

        _logger.LogInformation(
            "[委派回收] 后台任务已启动：首次延迟 {Delay}，之后每 {Interval} 执行一次。",
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
            using var scope = _scopeFactory.CreateScope();

            var provider = scope.ServiceProvider;
            var delegations = provider.GetRequiredService<IXjglRepository<SysPermissionDelegation>>();
            var manager = provider.GetRequiredService<IPermissionDelegationManager>();
            var clock = provider.GetRequiredService<IClock>();

            var now = clock.Now;

            // 只取"未撤销且已过期"的：`IsRevoked == false` 必须带上，
            // 否则每轮都会把历史记录重新捞出来处理一遍
            var expired = await delegations.GetListAsync(d =>
                !d.IsRevoked && d.ExpiresAt != null && d.ExpiresAt <= now);

            if (expired.Count == 0)
            {
                return;
            }

            foreach (var delegation in expired)
            {
                delegation.MarkRevoked(now, "已到期自动回收");

                await delegations.UpdateAsync(delegation, autoSave: true);

                await manager.RevokeGrantIfUnusedAsync(delegation);
            }

            _logger.LogInformation("[委派回收] 本轮回收 {Count} 条到期的权限委派。", expired.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机
        }
        catch (Exception ex)
        {
            // 绝不能把异常抛出去：BackgroundService 的异常会让整个主机停机
            _logger.LogError(ex, "[委派回收] 本轮执行异常，将在下个周期重试。");
        }
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
