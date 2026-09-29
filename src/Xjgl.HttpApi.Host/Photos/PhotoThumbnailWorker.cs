using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Xjgl.Photos;

/// <summary>
/// 缩略图补齐后台任务。
///
/// 【它同时解决两件事】
///   - 债 18：缩略图不再在上传请求内生成（解码+缩放是 CPU 活，一次 500 张会把请求线程占住）；
///   - 债 19：本功能上线之前上传的老照片，<c>ThumbnailPath</c> 一直是空的，
///     列表页只能回落到加载原图。
/// 这两件事在数据上是**同一种状态**（缩略图路径为空），所以共用同一条补齐路径 ——
/// 分开写就会有两套判重口径，迟早出现"回填任务把新上传的又生成一遍"。
///
/// 【为什么它必须自己吞掉所有异常】
/// 与照片补偿任务同理：<c>BackgroundService</c> 里漏出的异常在 .NET 6+ 会**默认停掉整个主机**。
/// 缩略图生成失败只是"列表页多加载几 MB"，绝不能让它把 Web 服务带下线。
///
/// 【为什么不需要先探活 NAS】
/// 写入走容错的 <c>IPhotoStorage</c>：NAS 不可用时它会自动落本地降级区，
/// 由照片补偿任务在 NAS 恢复后连同原图一起回放。探活是"把降级区的东西搬回 NAS"
/// 才需要的判断，这里探了反而会白白跳过一整轮。
/// </summary>
public class PhotoThumbnailWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PhotoThumbnailWorker> _logger;

    public PhotoThumbnailWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PhotoThumbnailWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Xjgl:Photo:ThumbnailEnabled", true))
        {
            _logger.LogInformation("[缩略图补齐] 后台任务已通过配置关闭。");
            return;
        }

        // 默认间隔取 300 秒而不是几十秒：缩略图是可选增强，
        // 而每轮都要读原图 + 解码 + 缩放，跑得太勤会和教师上传抢 NAS 带宽与 CPU。
        var interval = TimeSpan.FromSeconds(
            Math.Max(30, _configuration.GetValue("Xjgl:Photo:ThumbnailIntervalSeconds", 300)));

        var initialDelay = TimeSpan.FromSeconds(
            Math.Max(0, _configuration.GetValue("Xjgl:Photo:ThumbnailInitialDelaySeconds", 60)));

        var batchSize = Math.Max(1, _configuration.GetValue("Xjgl:Photo:ThumbnailBatchSize", 200));

        _logger.LogInformation(
            "[缩略图补齐] 后台任务已启动：首次延迟 {Delay}，之后每 {Interval} 执行一次，单轮上限 {Batch} 张。",
            initialDelay, interval, batchSize);

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
            await RunOnceAsync(batchSize, stoppingToken);
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private async Task RunOnceAsync(int batchSize, CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            // 补齐服务依赖仓储（Scoped），因此每次都要开一个新的作用域
            using var scope = _scopeFactory.CreateScope();

            var thumbnails = scope.ServiceProvider.GetRequiredService<IPhotoThumbnailService>();

            // 不指定学校：后台任务面向全库（管理员要"只修某校"走接口的手工入口）
            var result = await thumbnails.EnsureThumbnailsAsync(
                batchSize,
                schoolOrgId: null,
                cancellationToken: stoppingToken);

            if (result.Scanned > 0)
            {
                _logger.LogInformation("[缩略图补齐] {Message}", result.Message);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机，什么都不用做
        }
        catch (Exception ex)
        {
            // 绝不能把异常抛出去：BackgroundService 的异常会让整个主机停机
            _logger.LogError(ex, "[缩略图补齐] 本轮执行异常，将在下个周期重试。");
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
