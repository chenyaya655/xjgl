using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Xjgl.Data;

namespace Xjgl.Exporting;

/// <summary>
/// 把生成器上报的进度**节流后**写进任务表（债 23）。
///
/// 【为什么必须节流 —— 这不是"优化"，而是不做就不能上】
///   每个 CSV 生成器都是"每页 1000 行累积"，10 万行就是 100 页。若每页都落一次库，
///   一次导出会多出成百条 `UPDATE sys_export_task`，而它们与**生成本身**抢的正是
///   同一条数据库连接（还是 ABP 之外那条 SqlSugar 客户端）。结果是：
///   一次导出把库打得更满、自己跑得更慢，同一时间别人的列表页也跟着变慢 ——
///   也就是说，"加进度"这个纯展示功能反而拖垮了它本要服务的那个操作。
///   照片打包更极端：它是逐张读文件再写 zip，若逐张上报，500 张就是 500 次 UPDATE。
///   所以节流不是可选项。
///
/// 【为什么节流状态放在进程里，而不是加库表字段】
///   判断"该不该写"只需要"上次写到哪儿、什么时候写的"这两个值，而它们**只对
///   正在跑这个任务的那一个执行流有意义**。落到库里（例如加一个 `ProgressUpdatedTime`）
///   会引入一次额外的读 + 一次迁移，而多实例时还要处理"另一个实例也在写"的竞争 ——
///   换来的信息量为零。这也是债 23 要求"优先不加库表字段"的原因。
///   代价说清楚：**进程重启后节流窗口重来一次**，表现为重启后第一次上报总是立刻落库。
///   这是可接受的（进度本来就是尽力而为的旁路信息），所以刻意不做持久化。
///
/// 【为什么 processed 只增不减】
///   同一次执行里生成器是顺序上报的，正常情况下不会回退。但一旦回退（例如某个
///   生成器先按页报、又回头补一次带修正总数的上报），界面上的百分比就会**跳回去**，
///   用户看到的是"进度倒退"，比不动更让人怀疑系统坏了。所以这里单调收口一次，
///   代价是可能短暂停留在一个偏大的值上 —— 而它最终会被 worker 写的终值覆盖。
///
/// 【为什么整条链路是 async 的（而不是同步 Report + 阻塞等待）】
///   同步等一次数据库写会把后台线程占住，而这个类将来也可能被别的后台任务复用。
///   生成器本来就在 `await` 循环里，await 一次落库不增加任何写法成本。
/// </summary>
public class ThrottledExportProgressSink : IExportProgressSink, ITransientDependency
{
    /// <summary>
    /// 两次落库之间的最小间隔。取 2 秒是因为前端轮询间隔是 1.5 秒
    /// （`EXPORT_POLL_INTERVAL_MS`）—— 比轮询还快就是纯浪费；明显更慢则用户会看到
    /// 百分比一次跳一大格。2 秒是"每轮轮询都有机会看到新值"的最小代价。
    /// </summary>
    private const string MinIntervalSecondsConfigKey = "Xjgl:Export:Progress:MinIntervalSeconds";

    private const double DefaultMinIntervalSeconds = 2;

    /// <summary>
    /// 距上次落库至少又处理了这么多条才值得写一次。
    ///
    /// 与时间间隔是**与**的关系（两个条件都要满足）：单看时间的话，
    /// "几百条、几毫秒一页"的小导出会在 2 秒里积攒上百页、一次写一个跳跃的巨大值；
    /// 而这条下限让写入的粒度至少是"一页 1000 行"这个生成器本来就有的批次单位。
    /// （照片打包是逐张的、天然跨不过这个下限，于是它实际按时间间隔走 —— 那正是想要的。）
    /// </summary>
    private const string MinBatchSizeConfigKey = "Xjgl:Export:Progress:MinBatchSize";

    private const long DefaultMinBatchSize = 1000;

    private readonly IXjglRepository<SysExportTask> _tasks;
    private readonly ILogger<ThrottledExportProgressSink> _logger;

    private readonly TimeSpan _minInterval;
    private readonly long _minBatchSize;

    /// <summary>
    /// 节流状态（每次 <see cref="AttachTask"/> 重置，只在当前任务的执行流里有效）。
    /// 单任务串行上报，加锁是为了不依赖"将来没人把它改成并发上报"这个假设。
    /// </summary>
    private readonly object _gate = new();

    private Guid _taskId;
    private bool _written;
    private long _lastWrittenProcessed;
    private Stopwatch _sinceLastWrite = Stopwatch.StartNew();

    public ThrottledExportProgressSink(
        IXjglRepository<SysExportTask> tasks,
        IConfiguration configuration,
        ILogger<ThrottledExportProgressSink> logger)
    {
        _tasks = tasks;
        _logger = logger;

        var seconds = configuration.GetValue<double?>(MinIntervalSecondsConfigKey) ?? DefaultMinIntervalSeconds;
        var batch = configuration.GetValue<long?>(MinBatchSizeConfigKey) ?? DefaultMinBatchSize;

        // 非法值一律回落默认而不是抛异常：这个类只影响"界面上有没有百分比"，
        // 让一个配错的展示项把整条导出链路打挂是不相称的。
        _minInterval = TimeSpan.FromSeconds(seconds > 0 ? seconds : DefaultMinIntervalSeconds);
        _minBatchSize = batch > 0 ? batch : DefaultMinBatchSize;
    }

    /// <summary>
    /// 指定本次执行写哪一行，并**重置节流状态**。
    ///
    /// 重置是必须的：同一轮 worker 会连着跑多个任务，若沿用上一个任务的
    /// "上次写到 12000、刚刚才写过"，新任务的前几页会被节流掉，表现为
    /// "第二个任务在界面上一直显示正在生成…"。
    /// </summary>
    public void AttachTask(Guid taskId)
    {
        lock (_gate)
        {
            _taskId = taskId;
            _written = false;
            _lastWrittenProcessed = 0;
            _sinceLastWrite = Stopwatch.StartNew();
        }
    }

    /// <inheritdoc />
    public async Task ReportAsync(long processed, long total, CancellationToken cancellationToken = default)
    {
        if (processed <= 0 || total <= 0)
        {
            return;
        }

        // 生成器报的 processed 有可能因为"条数上限"的计算而多出几条（截断场景），
        // 夹一次是为了守住"已处理绝不大于总数"这条前端直接拿来算百分比的不变式：
        // 一旦出现 105%，界面上那句话就变成了不可信的数据。
        processed = Math.Min(processed, total);

        if (!ShouldWrite(processed))
        {
            return;
        }

        // 只写 Processed / Total 两列，**不整行 UPDATE**：
        // 整行写要求先用旧快照把实体填满，那会把这段时间里用户改过的
        // Message / Status（例如"已取消"）静默抹掉。列级更新从结构上杜绝了这件事。
        //
        // 这里刻意把所有异常都吞掉并降级成一条警告：进度写不进去只是界面上少一格，
        // 而"让一次本来成功的导出因为进度写失败而失败"是绝对不能接受的取舍。
        try
        {
            await _tasks.UpdateColumnsAsync(
                _taskId,
                new Dictionary<string, object?>
                {
                    [nameof(SysExportTask.Processed)] = processed,
                    [nameof(SysExportTask.Total)] = total
                },
                autoSave: true,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "[导出任务] 进度落库失败（不影响导出本身，界面会停在上一格）：任务 {TaskId}，{Processed}/{Total}",
                _taskId, processed, total);
        }
    }

    /// <summary>
    /// 是否该落库。单独抽出来是因为它是本类唯一的"有判断"的地方，
    /// 而它一旦写错，表现只是"百分比不动"或"库被打满"，两种都不容易在功能测试里发现。
    /// </summary>
    private bool ShouldWrite(long processed)
    {
        lock (_gate)
        {
            if (processed <= _lastWrittenProcessed)
            {
                return false;
            }

            var advanced = processed - _lastWrittenProcessed;

            // 第一次上报必须立刻写：用户点了导出，界面上要尽快出现
            // "已处理 1000 / 共 N"，否则在最前面那段（可能正是最慢的第一次查询）里
            // 界面一直显示"正在生成…"，看起来就像没在跑。
            //
            // 两个条件都满足（间隔够了 **且** 又攒够一批）才写：
            // 只看间隔的话，一次几毫秒一页的导出会在 2 秒里攒上百页、
            // 写下一个跳跃的巨大值，反而看不出"在动"。
            if (_written
                && _sinceLastWrite.Elapsed < _minInterval
                && advanced < _minBatchSize)
            {
                return false;
            }

            _written = true;
            _lastWrittenProcessed = processed;
            _sinceLastWrite.Restart();

            return true;
        }
    }
}

/// <summary>
/// 把 <see cref="ThrottledExportProgressSink"/> 挂到 <see cref="ExportProgress"/> 的环境范围上。
///
/// 单独一个类（而不是在 worker 里直接调）是为了让 worker 只写一行：
/// `using var progress = _progress.Activate(task.Id);` —— 圈范围、挂实现、
/// 告诉 sink 该写哪一行，三件事的**先后顺序**有讲究（必须先 AttachTask 再圈范围，
/// 否则第一条上报会因为找不到落库实现而走空实现、被静默丢掉），收在一处就不会写反。
/// </summary>
public class ExportProgressActivator : ITransientDependency
{
    private readonly ThrottledExportProgressSink _sink;

    public ExportProgressActivator(ThrottledExportProgressSink sink)
    {
        _sink = sink;
    }

    /// <summary>圈定范围并挂上节流落库实现；释放后范围自动解除。</summary>
    public IDisposable Activate(Guid taskId)
    {
        _sink.AttachTask(taskId);

        return ExportProgress.BeginScope(taskId, _sink);
    }
}
