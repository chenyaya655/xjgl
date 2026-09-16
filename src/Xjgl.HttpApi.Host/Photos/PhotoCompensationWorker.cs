using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Xjgl.Photos;

/// <summary>
/// 降级照片的回放后台任务。
///
/// 为什么必须有它：照片存储的熔断降级只保证「教师上传不失败」，
/// 但照片最终必须落到 NAS —— 否则本地盘满了、机器重装了，照片就真丢了。
/// 这个任务在后台周期性尝试把 <c>FallbackPending</c> 的照片搬回 NAS。
///
/// 设计要点：
///   - **周期 + 探活**：NAS 没恢复时探针会直接失败，任务立刻收工，
///     不会每个周期都去做几百次超时写入（那会把线程池拖垮）。
///   - **异常全捕获**：后台任务里漏出的异常在 .NET 6+ 会**默认停掉整个主机**，
///     这里必须自己吞掉并记日志，照片补传失败绝不能让 Web 服务下线。
///   - **单实例假设**：多实例部署时每个实例都会跑这个任务。
///     回放是幂等的（同一路径覆盖写、状态更新带乐观并发戳），
///     代价只是重复劳动；要严格单实例可换成分布式锁。
///   - **放在 Host 而不是应用层**：它是「进程级」的关注点，
///     集成测试不应该因为引用了应用层就意外跑起后台轮询。
/// </summary>
public class PhotoCompensationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PhotoCompensationWorker> _logger;

    public PhotoCompensationWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PhotoCompensationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Xjgl:Photo:CompensationEnabled", true))
        {
            _logger.LogInformation("[照片补偿] 后台任务已通过配置关闭。");
            return;
        }

        var interval = TimeSpan.FromSeconds(
            Math.Max(30, _configuration.GetValue("Xjgl:Photo:CompensationIntervalSeconds", 300)));

        var initialDelay = TimeSpan.FromSeconds(
            Math.Max(0, _configuration.GetValue("Xjgl:Photo:CompensationInitialDelaySeconds", 60)));

        _logger.LogInformation(
            "[照片补偿] 后台任务已启动：首次延迟 {Delay}，之后每 {Interval} 执行一次。",
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
            // 补偿服务依赖仓储（Scoped），因此每次都要开一个新的作用域
            using var scope = _scopeFactory.CreateScope();

            var compensation = scope.ServiceProvider.GetRequiredService<IPhotoCompensationService>();

            var result = await compensation.CompensateAsync(cancellationToken: stoppingToken);

            if (result.Scanned > 0)
            {
                _logger.LogInformation("[照片补偿] {Message}", result.Message);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机，什么都不用做
        }
        catch (Exception ex)
        {
            // 绝不能把异常抛出去：BackgroundService 的异常会让整个主机停机
            _logger.LogError(ex, "[照片补偿] 本轮执行异常，将在下个周期重试。");
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
