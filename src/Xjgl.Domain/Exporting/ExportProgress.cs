namespace Xjgl.Exporting;

/// <summary>
/// 一次任务执行期间的进度上报入口（债 23）。
///
/// 【为什么是"环境式"的，而不是给每个生成器加一个参数】
///   产出进度的那个生成器（分页累积的 CSV、逐张写包的照片 zip）与"知道自己在给哪个
///   任务干活"的调用方（后台 worker）之间隔着两三跳：worker → ExportTaskRunner →
///   IStudentCsvExportBuilder → IStudentAppService。要把"任务 Id"当参数一路传下去，
///   就得改七个接口的方法签名（其中四个还是别人的业务查询接口），而其中**任何一个**
///   漏传的表现都只是"这个种类的进度永远是 0" —— 一个不会报错、只有盯着轮询才发现的缺陷。
///   所以进度做成"当前执行流的环境状态"：worker 圈定范围并挂上落库实现，
///   生成器直接调 <see cref="ReportAsync"/>，中间层一行都不用改。
///
/// 【为什么是 AsyncLocal 而不是普通静态字段】
///   静态字段是**进程级**的：两个任务并行跑（多实例、或将来把 worker 改成并发）时，
///   后开始的那个会覆盖前一个的范围，于是 A 任务的进度被写进 B 任务的行里 ——
///   表现为"某个任务的已处理数莫名其妙地跳"（且只在并发时出现）。
///   AsyncLocal 跟着执行流走，每个任务自带一份，天然不会串。
///
/// 【为什么没有圈范围时是静默跳过 —— 那"忘了圈范围"靠什么防】
///   同一个生成器**不只**被后台任务调用：`StudentPrintService.RenderRosterAsync`
///   也会被集成测试直接调，照片导出的内存版 `ExportAsync` 同理。那里
///   "没有任务、所以没有进度"是**正常状态**，不是错误；为它抛异常会让一批
///   与进度无关的既有用例集体变红（本功能第一版就是这么红的：`StudentPrintTests`
///   一次红了 27 条），而那种红会把人引向"进度功能坏了"这个错误结论。
///   真实任务路径的完整性由**结构**保证而不是运行时断言：`ExportTaskWorker` 里
///   `progress.Activate(task.Id)` 把 `RunAsync` **整个包住**，所有任务种类的执行
///   都在范围之内（见该处注释）。真要防"新加一个种类忘了圈范围"，
///   靠的是那条唯一的调用链，而不是在每次上报时喊。
///   代价说清楚：万一将来有第二处调用 `IExportTaskRunner.RunAsync` 而忘了圈范围，
///   表现只会是"这个入口没有百分比"，不会有任何报错。
///
/// ⚠ 另有一条必须记住的边界：**只有分片产出的种类才报进度**。
///   一次性序列化完再一次写出的产物（组织名单、微代码）结构上给不出中间进度，
///   它们**不调用**这里，界面上会如实显示"正在生成…"。
///   绝不允许用"按已用时间估算百分比"来冒充 —— 那种数字会在任务实际
///   比预期慢或快的时候稳定地骗人，比没有百分比更糟。
/// </summary>
public static class ExportProgress
{
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>当前执行流是否已经圈定了某个任务的进度范围。</summary>
    public static bool IsActive => Current.Value != null;

    /// <summary>
    /// 圈定"接下来这一段产出算在哪个任务头上"，并把落库实现挂上去。
    ///
    /// 只在后台 worker 里调用一次；**生成器不要调它**（生成器只调 <see cref="ReportAsync"/>）。
    /// 嵌套使用是允许的（内层结束后自动恢复外层），因为"当前范围"是栈式的。
    /// </summary>
    /// <param name="taskId">任务 Id；空 Guid 直接拒绝（进度会被写进一条不存在的任务里）。</param>
    /// <param name="sink">落库实现。传 null 表示"只是圈范围、进度不落库"，仅供单测用。</param>
    public static IDisposable BeginScope(Guid taskId, IExportProgressSink? sink = null)
    {
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException(
                "任务 Id 不能是空 Guid —— 进度会被写进一条不存在的任务里。", nameof(taskId));
        }

        var previous = Current.Value;
        var scope = new Scope(previous, sink);

        Current.Value = scope;

        return scope;
    }

    /// <summary>
    /// 上报"已经产出 / 处理了多少条"。要不要真的落库由实现内部按节流规则决定。
    ///
    /// 没有圈定范围时**静默跳过**（理由见类注释）：那条路径上"没有任务"是正常状态。
    ///
    /// 【为什么 Total 必须跟着一起报】
    ///   只报"已处理"是没法算百分比的：前端还得再猜一个总数，而那个数在分页累积的
    ///   生成器里同样是"取完第一页才知道"。两者必须来自同一个时刻的同一次上报，
    ///   否则会出现"已处理 3000 / 总数 0"这种自相矛盾的中间态。
    ///
    /// 【为什么 processed &lt;= 0 或 total &lt;= 0 的调用被直接忽略】
    ///   "处理了 0 条"没有任何信息量，而后台任务刚开始时它会让前端多显示一次 0% ——
    ///   用户看到的就是"卡在 0%"。等真有第一条产出、且**知道总数**之后再报，
    ///   是这里唯一允许的上报时机。
    /// </summary>
    /// <param name="processed">已处理条数（分页累积的生成器就是"已经攒到的行数"）。</param>
    /// <param name="total">总数；**必须 >= processed**。不知道总数时不要调用本方法。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static Task ReportAsync(long processed, long total, CancellationToken cancellationToken = default)
    {
        if (processed <= 0 || total <= 0)
        {
            return Task.CompletedTask;
        }

        var scope = Current.Value;

        if (scope == null)
        {
            return Task.CompletedTask;
        }

        return scope.Sink.ReportAsync(processed, total, cancellationToken);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Scope? _previous;
        private bool _disposed;

        public Scope(Scope? previous, IExportProgressSink? sink)
        {
            _previous = previous;

            // 没传实现时用空实现兜底：单测里只想验证"生成器确实按页上报了"这类事实时，
            // 不必为此准备一个假的落库实现。
            Sink = sink ?? NullExportProgressSink.Instance;
        }

        public IExportProgressSink Sink { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Current.Value = _previous;
        }
    }
}

/// <summary>
/// 进度落库接口（债 23）。
///
/// 放在领域层且**只依赖 Task**：进度是"尽力而为"的旁路信息，
/// 生成产物的正确性绝不能因为它而改变 —— 所以它的实现不允许把异常抛出去，
/// 也不允许把"这次落库失败"变成任务失败。
/// 实现见 <c>Xjgl.Application/Exporting/ThrottledExportProgressSink.cs</c>。
/// </summary>
public interface IExportProgressSink
{
    /// <summary>
    /// 上报进度。实现**自己**决定这次要不要真的落库（节流），调用方不要额外去判断。
    /// **不得抛异常**：进度写不进去只是界面少一个百分比，不能让整次导出失败。
    /// </summary>
    /// <param name="processed">已处理条数。</param>
    /// <param name="total">总数（>= processed）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ReportAsync(long processed, long total, CancellationToken cancellationToken = default);
}

/// <summary>
/// 空实现：只在"圈了范围但不需要落库"的场景下使用（单测），
/// 以及 <see cref="ExportProgress.BeginScope"/> 没传实现时的兜底。
/// </summary>
public sealed class NullExportProgressSink : IExportProgressSink
{
    /// <summary>无状态，共用一个实例即可（每次 new 一个只是多一个 GC 对象）。</summary>
    public static readonly NullExportProgressSink Instance = new();

    public Task ReportAsync(long processed, long total, CancellationToken cancellationToken = default)
    {
        // 刻意什么都不做：这个实现的存在只为"没有落库实现时不要抛异常"。
        return Task.CompletedTask;
    }
}
