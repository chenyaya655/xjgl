using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xjgl.Sequences;
using Xunit;

namespace Xjgl.Sequences;

/// <summary>
/// 取号服务的集成测试（真实 PostgreSQL）。
///
/// 【为什么必须"多作用域并发"地测】
/// 取号的唯一承诺是「**并发下绝不发出重复号**」。而单线程顺序取号，
/// 任何实现（哪怕是 `count(*) + 1`）都不会重号 —— 顺序测试等于没测。
///
/// 但也不能在同一个 DI 作用域里并行调用：SqlSugar 的客户端**不是线程安全的**
/// （照片模块的批量上传就是为了这个才拆成"并发写存储 + 串行落库"）。
/// 真实场景是**多个 HTTP 请求**各自持有一个作用域同时取号，所以这里也这样造：
/// 每个并行任务新开一个作用域，让数据库层的建议锁去承担串行化。
/// </summary>
public class SequenceServiceTests : XjglIntegrationTestBase
{
    private readonly ISequenceService _sequence;

    public SequenceServiceTests()
    {
        _sequence = GetRequiredService<ISequenceService>();
    }

    /// <summary>每个用例用独立号段，避免用例之间互相影响。</summary>
    private static string NewSegment(string prefix) => $"{prefix}:{Guid.NewGuid():N}";

    // ------------------------------------------------------------------
    // 基本行为
    // ------------------------------------------------------------------

    [Fact]
    public void 首次取号_自动建行并从1开始()
    {
        var segment = NewSegment("TEST_SEQ");

        var first = _sequence.NextValue(segment);

        first.ShouldBe(1, "号段行应当按需自动创建，且第一个号是 1");
    }

    [Fact]
    public void 顺序取号_单调递增()
    {
        var segment = NewSegment("TEST_SEQ");

        var values = Enumerable.Range(0, 5).Select(_ => _sequence.NextValue(segment)).ToList();

        values.ShouldBe(new long[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public void 批量取号_返回起始号且区间连续()
    {
        var segment = NewSegment("TEST_SEQ");

        var start1 = _sequence.NextRange(segment, count: 10);
        var start2 = _sequence.NextRange(segment, count: 5);

        start1.ShouldBe(1);
        start2.ShouldBe(11, "第二次批量取号应当接着上一次的区间（1..10 之后从 11 开始）");
    }

    [Fact]
    public void 不同号段互不影响()
    {
        var a = NewSegment("TEST_SEQ_A");
        var b = NewSegment("TEST_SEQ_B");

        _sequence.NextRange(a, 7);

        _sequence.NextValue(b).ShouldBe(1);
        _sequence.NextValue(a).ShouldBe(8);
    }

    // ------------------------------------------------------------------
    // 并发（这是本功能的核心承诺）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 并发取号_绝不重号()
    {
        var segment = NewSegment("TEST_SEQ_CONCURRENT");
        const int workers = 20;

        // 每个任务一个新作用域 = 一个独立的 SqlSugar 客户端与连接，
        // 与"多个 HTTP 请求同时取号"等价
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            using var scope = ServiceProvider.CreateScope();

            var sequence = scope.ServiceProvider.GetRequiredService<ISequenceService>();

            // 每个 worker 取 5 个号，总共 100 个
            var taken = new List<long>();

            for (var i = 0; i < 5; i++)
            {
                taken.Add(sequence.NextValue(segment));
            }

            return taken;

        })).ToArray();

        var all = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();

        all.Count.ShouldBe(workers * 5);
        all.ShouldAllBe(v => v > 0);
        all.Distinct().Count().ShouldBe(all.Count, "并发取号出现了重复号 —— 这是学籍系统里最严重的数据事故");

        // 号段值应当正好用掉 100 个号（允许跳号，但这里没有失败回滚，因此应当连续）
        all.Max().ShouldBe(workers * 5);
    }

    [Fact]
    public async Task 并发批量取号_区间不重叠()
    {
        var segment = NewSegment("TEST_SEQ_RANGE");
        const int workers = 10;
        const int batchSize = 10;

        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            using var scope = ServiceProvider.CreateScope();

            var sequence = scope.ServiceProvider.GetRequiredService<ISequenceService>();

            return sequence.NextRange(segment, batchSize);
        })).ToArray();

        var starts = await Task.WhenAll(tasks);

        // 把每个区间展开，断言整体不重叠
        var expanded = starts.SelectMany(s => Enumerable.Range(0, batchSize).Select(i => s + i)).ToList();

        expanded.Distinct().Count().ShouldBe(workers * batchSize, "批量取号返回的区间发生了重叠");
    }

    // ------------------------------------------------------------------
    // 参数校验
    // ------------------------------------------------------------------

    [Fact]
    public void 取号数量非法_直接报错()
    {
        var segment = NewSegment("TEST_SEQ_BAD");

        Should.Throw<ArgumentOutOfRangeException>(() => _sequence.NextRange(segment, count: 0));
        Should.Throw<ArgumentException>(() => _sequence.NextValue(" "));
    }
}
