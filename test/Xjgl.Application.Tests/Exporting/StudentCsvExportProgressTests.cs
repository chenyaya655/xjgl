using Shouldly;
using Volo.Abp.Guids;
using Xjgl.Data;
using Xjgl.Exporting;
using Xjgl.Organizations;
using Xjgl.Students;
using Xjgl.Text;
using Xunit;

namespace Xjgl.Exporting;

/// <summary>
/// 导出/打印的**真进度**（债 23）集成测试。
///
/// 【这组用例守什么 —— 为什么不能只断言"最后成功了"】
///   债 23 的原状是"任务表里有 `Processed` 字段，但它恒等于 0 或终值"。
///   而 `ExportTaskWorker` 收尾时有一行 `task.Processed = result.RowCount` ——
///   也就是说，**哪怕生成器一次都不上报，只看终值的断言也照样是绿的**。
///   所以这里分成三层，每层只证明一件事：
///     · 「上报了什么」用**记录用**的 sink 直接观测（确定性，不依赖时序）；
///     · 「确实写进库了、终值与实际产出一致」用真实 sink + 真实库；
///     · 「反复上报不会把库打满」单独一组。
///
/// 【为什么不靠"边跑边查库"来观测中途值】
///   那是能想到的最直接的写法，但它依赖"生成 1000 行恰好比我轮询一次慢" ——
///   在 CI 上这是**偶发**变红的经典配方（机器快一点就观测不到，用例随机红，
///   而根因不在被测代码）。记录用 sink 把"生成器到底报了什么"变成完全确定的断言，
///   而"确实落库了"由另一条用例单独证明。
/// </summary>
public class StudentCsvExportProgressTests : XjglIntegrationTestBase
{    /// <summary>
    /// 学生名单导出的单页大小。测试配置里 `Xjgl:Export:StudentCsvPageSize` 设成了 5
    /// （生产默认 1000）—— 跨页才谈得上"分片"，而用 1000 的页大小就必须往
    /// 共享测试库里插 1000+ 名学生（实测 30 秒上下）。理由见
    /// `StudentCsvExportBuilder.PageSizeConfigKey` 的说明。
    /// </summary>
    private const int PageSize = 5;

    private readonly IXjglRepository<SysExportTask> _tasks;
    private readonly ExportProgressActivator _progress;
    private readonly IStudentCsvExportBuilder _csvExport;
    private readonly IStudentImportExportAppService _io;
    private readonly IOrgAppService _orgs;
    private readonly IGuidGenerator _guidGenerator;

    public StudentCsvExportProgressTests()
    {
        _tasks = GetRequiredService<IXjglRepository<SysExportTask>>();
        _progress = GetRequiredService<ExportProgressActivator>();
        _csvExport = GetRequiredService<IStudentCsvExportBuilder>();
        _io = GetRequiredService<IStudentImportExportAppService>();
        _orgs = GetRequiredService<IOrgAppService>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    // ==================================================================
    // 脚手架
    // ==================================================================

    /// <summary>造一所本轮专属的学校（名字带唯一前缀，便于人工核对库里留下的数据）。</summary>
    private async Task<OrgDto> CreateSchoolAsync(string prefix)
    {
        return await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School,
            Code = "41" + Random.Shared.Next(10000000, 99999999),
            Name = $"{prefix}学校"
        });
    }

    /// <summary>建一个"挂在这次任务上"的导出任务行（进度就是往它上面写）。</summary>
    private async Task<SysExportTask> CreateTaskAsync()
    {
        var task = new SysExportTask(
            _guidGenerator.Create(),
            ExportTaskKinds.StudentCsv,
            Guid.NewGuid(),
            "债23测试",
            null,
            "{}");

        await _tasks.InsertAsync(task, autoSave: true);

        return task;
    }

    /// <summary>
    /// 造 <paramref name="count"/> 名学生，姓名统一带本轮前缀 <paramref name="prefix"/>。
    ///
    /// 【为什么走一次性 CSV 导入，而不是 for 循环 CreateAsync】
    ///   本组用例要**跨页**才谈得上"分片"，导入是一次请求、逐行容错，
    ///   同样的数据量比逐条创建快得多，而且用的正是真实链路。
    ///
    /// 姓名**不能含数字**：建学生走的是与手工录入完全相同的校验，而
    /// `RareCharacterText.IsValidPersonName` 不接受姓名里带数字或拉丁字母
    /// （见 `StudentImportExportTests` 里"生僻字姓名与手工录入同样被校验"那条）。
    /// 前缀由 `NewNamePrefix` 生成（随机数用汉字写），序号也用汉字。
    ///
    /// 【为什么要按行重试】
    ///   身份证号来自基类的计数器生成器，而测试库是**长期累积**的：
    ///   跨轮撞上历史记录是已知的低概率事件（基类注释里写过）。逐条创建那条路
    ///   有 `RetryOnDuplicateIdCardAsync` 兜底，而导入**逐行容错**、不抛异常，
    ///   所以这里必须自己把失败的行挑出来换号重报 —— 否则表现是
    ///   "造数据少了几条"，而断言会在进度上红，root cause 却在一千行之外。
    /// </summary>
    private async Task CreateStudentsAsync(OrgDto school, string prefix, int count)
    {
        var header = new[]
        {
            "姓名", "性别", "出生日期", "证件类型", "身份证号",
            "学校标识码", "学年", "年级编码", "班级编码", "学籍状态", "入学日期", "联系电话", "家庭住址"
        };

        // 行号（数据行，从 1 开始）→ 姓名；用于把导入报回来的失败行映射回姓名，
        // 从而只重报那几行，而不是整批重来（整批重来会撞上"这次成功的那些"的查重）。
        var names = new List<string>();

        for (var i = 1; i <= count; i++)
        {
            names.Add($"{prefix}{NumberToChinese(i)}生");
        }

        var pending = names.ToList();

        for (var attempt = 1; attempt <= 6 && pending.Count > 0; attempt++)
        {
            var rows = new List<string[]> { header };
            var rowNames = new List<string>();

            foreach (var name in pending)
            {
                rows.Add(Row(school.Code, name));
                rowNames.Add(name);
            }

            var result = await _io.ImportAsync(new ImportStudentsDto { Content = Csv.Write(rows) });

            if (result.Failed == 0)
            {
                pending.Clear();
                break;
            }

            // 导入的 RowIndex 是"表格行号"（表头是第 1 行），减 2 才落到 rowNames 的下标
            var failedNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var error in result.Errors)
            {
                var index = error.RowIndex - 2;

                if (index >= 0 && index < rowNames.Count)
                {
                    failedNames.Add(rowNames[index]);
                }
            }

            // 一行都没映射上（列数对不上之类）时不该静默重试，直接失败更可信
            failedNames.Count.ShouldBeGreaterThan(0,
                $"导入失败 {result.Failed} 行，但没能把失败行映射回姓名："
                + string.Join("；", result.Errors.Take(3).Select(e => $"第{e.RowIndex}行 {e.Message}")));

            pending = failedNames.ToList();
        }

        pending.ShouldBeEmpty(
            $"多次换号后仍有 {pending.Count} 名学生没能建起来；"
            + "进度断言的分母会因此不对，先解决造数据这一步");
    }

    /// <summary>导入 CSV 的一行（与 StudentImportExportTests 的列口径一致）。</summary>
    private static string[] Row(string schoolCode, string name) => new[]
    {
        name, "男", "2016-03-01", "居民身份证", NewUniqueIdCard(2016),
        schoolCode, "2066-2067", string.Empty, string.Empty, "在读", "2016-09-01",
        "13800000000", "郑州市"
    };

    /// <summary>把序号转成纯汉字（姓名里不允许出现数字）。</summary>
    private static string NumberToChinese(int value)
    {
        const string digits = "零一二三四五六七八九";

        if (value < 10)
        {
            return digits[value].ToString();
        }

        var builder = new System.Text.StringBuilder();
        var rest = value;

        if (rest >= 1000)
        {
            builder.Append(digits[rest / 1000]).Append('千');
            rest %= 1000;
        }

        if (rest >= 100)
        {
            builder.Append(digits[rest / 100]).Append('百');
            rest %= 100;
        }

        if (rest >= 10)
        {
            // 十位是 1 且前面没有更高位时省略"一"（十、十一…），与中文习惯一致
            if (rest / 10 > 1 || builder.Length > 0)
            {
                builder.Append(digits[rest / 10]);
            }

            builder.Append('十');
            rest %= 10;
        }

        if (rest > 0)
        {
            builder.Append(digits[rest]);
        }

        return builder.ToString();
    }

    private static StudentQueryDto Query(OrgDto school, string prefix) => new()
    {
        SchoolOrgId = school.Id,
        Keyword = prefix,
        Sorting = "StudentNo ASC"
    };

    /// <summary>
    /// 只记录"被报了什么"的 sink。
    ///
    /// 记录用而不是断言用：它把生成器**每一次**上报原样留下来，
    /// 于是"每页之后各报一次"这种关于次数的断言才有可能（只看数据库是看不到次数的 ——
    /// 那里已被节流压扁）。
    /// </summary>
    private sealed class RecordingSink : IExportProgressSink
    {
        private readonly List<(long Processed, long Total)> _reports = new();

        public IReadOnlyList<(long Processed, long Total)> Reports => _reports;

        public string Describe() => string.Join("、", _reports.Select(r => $"{r.Processed}/{r.Total}"));

        public Task ReportAsync(long processed, long total, CancellationToken cancellationToken = default)
        {
            _reports.Add((processed, total));

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 把上报同时交给"记录"和"真实落库"两个 sink。
    ///
    /// 这样一次执行里既能断言"报了什么"，又能断言"落了什么" ——
    /// 两者分开跑的话，就得靠时序去对齐，那是偶发变红的来源。
    /// </summary>
    private sealed class ChainedSink : IExportProgressSink
    {
        private readonly IExportProgressSink _first;
        private readonly IExportProgressSink _second;

        public ChainedSink(IExportProgressSink first, IExportProgressSink second)
        {
            _first = first;
            _second = second;
        }

        public Task ReportAsync(long processed, long total, CancellationToken cancellationToken = default)
        {
            _first.ReportAsync(processed, total, cancellationToken);

            return _second.ReportAsync(processed, total, cancellationToken);
        }
    }

    // ==================================================================
    // 一、分片上报：每页之后都报，且分母是"本次真的会写出去的行数"
    // ==================================================================

    [Fact]
    public async Task 学生名单导出_分页累积时每页之后各上报一次_且已处理不超过总数()
    {
        // 刻意造满一页 + 5：只有跨页才能证明"每页之后上报一次"。
        // 恰好一页的话，"每页一次"与"只在开头报一次"给出的是同一个观测结果。
        var count = PageSize + 5;
        var prefix = NewDataPrefix("债二十三甲");
        var namePrefix = NewNamePrefix("债甲");
        var school = await CreateSchoolAsync(prefix);

        await CreateStudentsAsync(school, namePrefix, count);

        var sink = new RecordingSink();

        using (ExportProgress.BeginScope(_guidGenerator.Create(), sink))
        {
            var result = await _csvExport.BuildAsync(Query(school, namePrefix));

            result.RowCount.ShouldBe(count);
            result.Total.ShouldBe(count);
        }

        // 两页 → 恰好两次上报。第一页之后是 1000/1005，第二页之后是 1005/1005。
        sink.Reports.Count.ShouldBe(2, $"每取完一页上报一次；{count} 行 = 2 页。实际上报：{sink.Describe()}");

        var first = sink.Reports[0];

        first.Processed.ShouldBe(PageSize, $"第一次上报应当是第一页取完之后的条数。实际：{sink.Describe()}");
        first.Total.ShouldBe(count, "总数在第一页之后就确定了（TotalCount 是查询回带出来的）");

        // ★ 核心不变式：中途那一刻必须是**真的中间值**，不是 0 也不是终值。
        //   0 表示"没有进度"；等于总数表示"上报写在了收尾处"。两种情况都等于没做这件事。
        first.Processed.ShouldBeGreaterThan(0, "中途的已处理数不能是 0 —— 那等于没有进度");
        first.Processed.ShouldBeLessThan(count, "中途的已处理数不该已经等于总数 —— 那说明它是在收尾时才报的");

        sink.Reports[^1].ShouldBe((count, count), $"最后一次上报必须是终值。实际：{sink.Describe()}");

        // 全部上报都满足"已处理 <= 总数"：前端直接拿这两个数算百分比，
        // 一旦出现 105%，界面上那句话就变成了不可信的数据。
        foreach (var (processed, total) in sink.Reports)
        {
            processed.ShouldBeLessThanOrEqualTo(total, $"已处理超过总数会让前端算出 >100%。实际：{sink.Describe()}");
        }
    }

    // ==================================================================
    // 二、真的写进库了；而"跑完时已处理 == RowCount"由生成器报出的终值保证
    // ==================================================================

    [Fact]
    public async Task 学生名单导出_进度真的落库_且生成器报出的终值等于实际产出行数()
    {
        // 【为什么这里要分两段来断言，而不是直接读库断言 Processed == RowCount】
        //   一开始我写的就是"跑完直接读库、断言 Processed == 1005"，它**红了**：
        //   第二页取完与第一页之间只隔了几百毫秒（差值只有 5 行），
        //   于是那一次上报被节流合法地跳过了，库里停在上一次的 1000。
        //
        //   这不是缺陷，而是这套设计的既定分工：
        //     · 上报是**节流**的，只保证"界面上能持续看到前进"；
        //     · 终值由 `ExportTaskWorker` 收尾时那一行 `task.Processed = result.RowCount`
        //       写下去 —— 它不经过节流，所以终值一定是准的。
        //   所以"终值正确"这件事的**责任方**是"生成器报出的最后一个值 == 实际行数"，
        //   而 worker 那一行只是把它抄下来。测试因此必须去验责任方，
        //   而不是去验节流撞没撞上（后者是时序，不能当断言）。
        var count = PageSize + 5;
        var prefix = NewDataPrefix("债二十三乙");
        var namePrefix = NewNamePrefix("债乙");
        var school = await CreateSchoolAsync(prefix);

        await CreateStudentsAsync(school, namePrefix, count);

        var task = await CreateTaskAsync();

        // 用**生产实现**（节流 sink + 真实库 + 真实 repository），
        // 且**不覆盖**节流参数（见 XjglTestConfiguration 里的说明）。
        // 外面再套一层记录用 sink：它把真实 sink 收到的上报原样记下来，
        // 于是"报了什么"与"落了什么"可以在同一次执行里对上。
        var recording = new RecordingSink();
        var realSink = GetRequiredService<ThrottledExportProgressSink>();
        var chained = new ChainedSink(recording, realSink);

        realSink.AttachTask(task.Id);

        using (ExportProgress.BeginScope(task.Id, chained))
        {
            var result = await _csvExport.BuildAsync(Query(school, namePrefix));

            count.ShouldBe(result.RowCount, "先钉死实际产出行数，下面才有比较的基准");

            // ★ 终值的来源：生成器报出的**最后一个**值必须等于实际产出行数。
            //   少一条，界面就会停在 99%；多一条，就会显示 >100%。
            recording.Reports.ShouldNotBeEmpty();
            recording.Reports[^1].Processed.ShouldBe(result.RowCount,
                $"生成器报出的终值必须等于实际产出行数。实际上报：{recording.Describe()}");
            recording.Reports[^1].Total.ShouldBe(result.RowCount,
                "分母必须是「本次真的会写出去的行数」—— 拿「符合条件总数」当分母时，"
                + "被 MaxRows 截断的导出会永远差最后几个百分点");

            // 中途确实落过库（而不是一路节流到收尾才写）：
            // 第一页之后那一次在各用例的时序里都必然通过节流闸门。
            var persisted = await _tasks.FindAsync(task.Id);

            persisted.ShouldNotBeNull();
            persisted!.Processed.ShouldBeGreaterThan(0,
                "跑到一半时库里就该有一个非零的已处理数 —— 全程为 0 等于没有进度");
            persisted.Processed.ShouldBeLessThanOrEqualTo(persisted.Total,
                "已处理超过总数会让前端算出 100% 以上的百分比");
            persisted.Total.ShouldBe(count, "总数在第一页之后就确定了");
        }

        // 落库这一步不能只写 Processed/Total 而把别的列抹掉。
        // `Kind` 与 `ParametersJson` 是否还在，就是"用的是列级更新、
        // 不是整行覆盖"的判据 —— 整行覆盖时它们会被写回默认值。
        var afterColumns = await _tasks.FindAsync(task.Id);

        afterColumns!.Kind.ShouldBe(ExportTaskKinds.StudentCsv, "进度写库不能把任务的其它列一起覆盖掉");
        afterColumns.ParametersJson.ShouldBe("{}");
        afterColumns.RequestedByName.ShouldBe("债23测试");
    }

    // ==================================================================
    // 五、花名册（PDF）也是分页取数，所以它同样有真进度
    // ==================================================================

    [Fact]
    public async Task 花名册打印_分页取数时每页之后各上报一次进度()
    {
        // 【为什么花名册也要单独验一次，虽然它和 CSV 长得像】
        //   本功能的文档与前端文案都声称「花名册能给出真百分比」，
        //   而这句话完全依赖于 `StudentPrintService.RenderRosterAsync` 的那一行上报。
        //   CSV 那边的用例**证明不了**这一条：两个生成器是两处独立实现，
        //   其中一处删掉上报，另一处照样绿。
        //
        // 与 CSV 不同的是这里会**真的渲染 PDF**（花名册产物是 PDF 而不是文本），
        // 所以页大小（`Xjgl:Print:RosterPageSize`，测试设为 5）必须调小 ——
        // 用 1000 的页大小意味着为了跨页要把 1000+ 名学生渲染进 PDF。
        const int rosterPageSize = 5;

        var count = rosterPageSize + 2;
        var prefix = NewDataPrefix("债二十三丙");
        var namePrefix = NewNamePrefix("债丙");
        var school = await CreateSchoolAsync(prefix);

        await CreateStudentsAsync(school, namePrefix, count);

        var recording = new RecordingSink();
        var print = GetRequiredService<IStudentPrintService>();

        using (ExportProgress.BeginScope(_guidGenerator.Create(), recording))
        {
            var file = await print.RenderRosterAsync(new StudentRosterQueryDto
            {
                SchoolOrgId = school.Id,
                Keyword = namePrefix,
                MaxRows = 1000
            });

            file.RowCount.ShouldBe(count);
        }

        recording.Reports.Count.ShouldBe(2,
            $"每取完一页上报一次；{count} 行 = 2 页。实际上报：{recording.Describe()}");

        recording.Reports[0].Processed.ShouldBe(rosterPageSize,
            $"第一次上报应当是第一页取完之后的条数。实际：{recording.Describe()}");
        recording.Reports[0].Processed.ShouldBeLessThan(count,
            "中途的已处理数不该已经等于总数 —— 那说明它是在收尾时才报的");

        recording.Reports[^1].ShouldBe((count, count),
            $"最后一次上报必须是终值。实际：{recording.Describe()}");
    }

    // ==================================================================
    // 三、节流：不节流的话一次导出会把库打满
    // ==================================================================

    [Fact]
    public async Task 进度节流_第一次上报立刻落库_之后同一批内的重复上报不再写库()
    {
        var task = await CreateTaskAsync();
        var sink = GetRequiredService<ThrottledExportProgressSink>();

        // 显式挂任务并重置节流状态：这个类在 DI 里是 Transient，但"当前任务"
        // 是实例状态，测试里直接用就必须自己指定一次（生产里由 Activator 做）。
        sink.AttachTask(task.Id);

        // 第一次：**必须**立刻写。理由见实现注释 —— 用户点了导出，界面上要尽快出现
        // "已处理 1000 / 共 N"，否则最前面那段（可能正是最慢的第一次查询）
        // 一直显示"正在生成…"，看起来就像没在跑。
        await sink.ReportAsync(PageSize, PageSize * 2);

        (await _tasks.FindAsync(task.Id))!.Processed.ShouldBe(PageSize);

        // 紧接着再报一次（生产里就是下一页取完了）：与上次只差 5 条，
        // 也没到最小批大小（默认 1000），而两次之间几乎不花时间 ——
        // 两个条件都不满足，必须被节流掉。
        await sink.ReportAsync(PageSize + 5, PageSize * 2);

        (await _tasks.FindAsync(task.Id))!.Processed.ShouldBe(PageSize,
            "两次上报只差 5 条、且间隔不到最小间隔，不该再写一次库 —— 否则 10 万行的导出会打上百次 UPDATE");

        // 攒够一批之后即使间隔没到也必须写：否则"几毫秒一页"的快速导出
        // 会在 2 秒里积攒上百页、一次写下一个跳跃的巨大值，反而看不出在动。
        await sink.ReportAsync(PageSize * 2 + 1000, PageSize * 2 + 1000);

        (await _tasks.FindAsync(task.Id))!.Processed.ShouldBe(PageSize * 2 + 1000,
            "又攒够一批（>= 最小批大小）就该落库，不能傻等时间间隔");
    }

    [Fact]
    public async Task 进度节流_只差几条时靠时间间隔放行_而不是永远不写()
    {
        // 这条守的是节流的**另一半**：前一条证明"该省的时候省"，
        // 这一条证明"省过头更糟"—— 照片打包是逐张上报的，每张之间差 1 条、
        // 永远攒不满最小批大小；若只看批大小，它的进度会一直停在第一张，
        // 直到跑完才跳到 100%。
        var task = await CreateTaskAsync();
        var sink = GetRequiredService<ThrottledExportProgressSink>();

        sink.AttachTask(task.Id);

        await sink.ReportAsync(1, 500);

        (await _tasks.FindAsync(task.Id))!.Processed.ShouldBe(1);

        // 默认最小间隔 2 秒。这里真的等过去 —— 用"把间隔配小"来绕过的话，
        // 测的就不是生产参数了，而节流规则本身正是这条用例要测的东西。
        await Task.Delay(TimeSpan.FromSeconds(2.2));

        await sink.ReportAsync(2, 500);

        (await _tasks.FindAsync(task.Id))!.Processed.ShouldBe(2,
            "间隔到了就必须放行，否则逐张上报的种类（照片打包）进度会一直不动");
    }

    // ==================================================================
    // 四、不该写库的东西不能写库
    // ==================================================================

    [Fact]
    public async Task 进度上报_没有总数或没有产出时不落库()
    {
        // 组织名单、微代码是**一次性序列化**的产物（见 ExportTaskRunner 里那份名单），
        // 它们结构上给不出中间进度，界面上靠 `describeExportProgress` 在 `total == 0`
        // 时回退到"正在生成…"来如实交代。这里守的是另一半：
        // 就算有人误调，也不能往库里写假进度。
        var task = await CreateTaskAsync();
        var sink = GetRequiredService<ThrottledExportProgressSink>();

        sink.AttachTask(task.Id);

        await sink.ReportAsync(0, 0);
        await sink.ReportAsync(500, 0);
        await sink.ReportAsync(0, 500);

        var persisted = await _tasks.FindAsync(task.Id);

        persisted!.Processed.ShouldBe(0);
        persisted.Total.ShouldBe(0);
    }
}
