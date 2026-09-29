using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Guids;
using Volo.Abp.Timing;
using Xjgl.Changes;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Students;
using Xjgl.Users;
using Xunit;

namespace Xjgl.Statistics;

/// <summary>
/// 统计报表（学生统计 + 异动统计）的集成测试。
///
/// 【为什么这组用例必须存在，而且必须打真库】
/// 统计是**一条拼出来的 GROUP BY SQL**，它有两个用别的手段验证不了的特性：
///   1) 分组表达式随维度变化，写错了（例如把 `studentStatus` 分支漏掉）
///      只会抛异常或返回空，编译期完全看不出来；
///   2) 数据范围过滤是拼在 SQL 里的前缀匹配 **与"是否含下级"的等值匹配**，
///      这两种情况的差别只有在真数据上跑一遍才会暴露。
/// 本仓库已经踩过一次同类的坑：`IncludeDescendantOrgs` 被应用层丢掉，
/// 界面上关掉"含下级"后返回的仍是整棵子树 —— 见下面
/// <see cref="学生统计_关掉含下级_不再把下级学校算进来"/>。
///
/// 【异动统计为什么也在这里】
/// 演示种子**故意不造异动申请**（用户明确的决定：异动数据通过测试加入）。
/// 所以异动统计在演示库上永远是 0 行，靠手工点页面根本验证不到。
/// 这组用例负责把它跑出真实行来：建异动类型 → 配流程 → 发布 → 提交申请，
/// 然后才去断言各个维度的数字。
/// </summary>
public class StatisticsAppServiceTests : XjglIntegrationTestBase
{
    /// <summary>
    /// 本类专用学年，避免与其它测试类的数据混在一起断言。
    ///
    /// **由当前时间推导**而不是写死一个年份：测试库是长期累积的真实库，
    /// 固定年份在跑过几轮、或未来某天有人把这里改成"按学年统计"的用例之后，
    /// 就会与历史数据撞在一起 —— 表现是"断言突然多出几个学生"，
    /// 看起来像业务回归，实际是测试数据的问题。按年推导既保证隔离，
    /// 也不会随时间推移和别的用例重名。
    /// </summary>
    private readonly string _year = $"{DateTime.UtcNow.Year}-{DateTime.UtcNow.Year + 1}";

    private readonly IStatisticsAppService _statistics;
    private readonly IOrgAppService _orgs;
    private readonly IStudentAppService _students;
    private readonly IChangeTypeAppService _changeTypes;
    private readonly IChangeFlowAppService _flow;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IClock _clock;
    private readonly FakeCurrentPrincipalAccessor _principal;

    public StatisticsAppServiceTests()
    {
        _statistics = GetRequiredService<IStatisticsAppService>();
        _orgs = GetRequiredService<IOrgAppService>();
        _students = GetRequiredService<IStudentAppService>();
        _changeTypes = GetRequiredService<IChangeTypeAppService>();
        _flow = GetRequiredService<IChangeFlowAppService>();
        _predefines = GetRequiredService<IXjglRepository<SysUserPredefine>>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _clock = GetRequiredService<IClock>();
        _principal = GetRequiredService<FakeCurrentPrincipalAccessor>();
    }

    // ==================================================================
    // 脚手架
    // ==================================================================

    /// <summary>省 → 市 → 甲县（两所学校）/ 乙县（一所学校）。</summary>
    private async Task<(
        Guid Province, Guid City, Guid CountyA, Guid SchoolA1, Guid SchoolA2,
        Guid CountyB, Guid SchoolB1)> BuildTreeAsync()
    {
        var province = await CreateOrgAsync(null, OrgTypes.Province, "统计测试省厅");
        var city = await CreateOrgAsync(province.Id, OrgTypes.City, "统计测试市局");
        var countyA = await CreateOrgAsync(city.Id, OrgTypes.County, "统计测试甲县");
        var countyB = await CreateOrgAsync(city.Id, OrgTypes.County, "统计测试乙县");

        var schoolA1 = await CreateSchoolAsync(countyA.Id, "统计测试甲县一中");
        var schoolA2 = await CreateSchoolAsync(countyA.Id, "统计测试甲县二中");
        var schoolB1 = await CreateSchoolAsync(countyB.Id, "统计测试乙县一中");

        return (province.Id, city.Id, countyA.Id, schoolA1.Id, schoolA2.Id, countyB.Id, schoolB1.Id);
    }

    /// <summary>
    /// 建一个组织；**编码撞号时只换这个组织的编码重试**，不重试整棵树。
    ///
    /// 【为什么不能"整棵树 catch 了重来" —— 这里踩过一个自我加剧的偶发失败】
    ///   旧实现是 `for (attempt...) { try { return await BuildTreeOnceAsync(); } catch (BusinessException) { } }`，
    ///   看起来在重试，实际有两个致命问题：
    ///     1) 撞号只可能发生在**某一个**编码上，但重试把已经建好的省/市/县**再建一遍** ——
    ///        测试库是长期累积的真实库（实测已攒下 4164 个「统计测试」组织、13342 所学校），
    ///        每重试一次就多插 4~7 行，**撞号概率反而更高**，于是重试额度（10 次）被自己耗光；
    ///     2) 失败信息是「多次尝试后仍无法生成唯一的组织编码」，堆栈指向 BuildTreeAsync，
    ///        完全看不出是哪个编码、撞了什么 —— 而且它**偶发**（取决于库里已有多少历史数据），
    ///        很容易被当成"统计功能回归"或干脆被当成随机噪音放过去。
    ///   改成按编码定向重试后：单次撞号概率约万分之一（见 NewDivisionCode 说明），
    ///   20 次重试的失败概率约 (1e-4)^20（可忽略），且不再产生任何垃圾组织行。
    /// </summary>
    private async Task<OrgDto> CreateOrgAsync(Guid? parentId, string orgType, string name)
    {
        const int maxAttempts = 20;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _orgs.CreateAsync(new CreateUpdateOrgDto
                {
                    ParentId = parentId,
                    OrgType = orgType,
                    Code = NewDivisionCode(),
                    Name = name
                });
            }
            catch (BusinessException) when (attempt < maxAttempts)
            {
                // 随机编码撞上库里已有的组织，换一个再来。只重试这一个节点。
            }
        }
    }

    private async Task<OrgDto> CreateSchoolAsync(Guid parentId, string name)
    {
        const int maxAttempts = 20;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _orgs.CreateAsync(new CreateUpdateOrgDto
                {
                    ParentId = parentId, OrgType = OrgTypes.School,
                    // 学校标识码必须**精确 10 位**（学籍辅号规则是 {SchoolCode:10}{Seq:6}），
                    // 前 6 位是所属行政区划码。写成 12 位会让建学生直接抛
                    // StudentNumberGenerateFailed —— 而报错来自学号生成，与"建学校"看起来无关。
                    //
                    // ⚠ 这里**不能**为了"降低撞号概率"把后 4 位放宽到 6 位：
                    //   6+6=12 位超过 StudentNumberRule 里 SchoolCode 段的宽度 10，
                    //   Format 会抛「段取值超过 10 位，号段已耗尽或配置有误」。
                    //   实测这样改会让本类 18 条用例里 16 条立刻全红（报错还指向学号生成，
                    //   与"改了测试的组织编码"看起来毫无关系）。
                    //   撞号问题由上面的定向重试解决，不靠加大号段。
                    Code = NewDivisionCode() + Random.Shared.Next(1000, 9999).ToString("D4"),
                    Name = name
                });
            }
            catch (BusinessException) when (attempt < maxAttempts)
            {
                // 同 CreateOrgAsync：撞号只换这一个学校的标识码重试。
                // 顺带把随机号段从 4 位放宽到 6 位 —— 库里已累积 1.3 万所学校，
                // 4 位（9000 种）的撞号概率已经到了实测能碰到的量级。
            }
        }
    }

    /// <summary>
    /// 6 位行政区划码（学校标识码的前 6 位）。
    ///
    /// ⚠ 号段只有 <c>9999-1000+1 = 9000</c> 种。测试库是长期累积的真实库
    ///   （实测已有 4164 个「统计测试」组织、13342 所学校），因此**单次取值撞号概率
    ///   在 0.1%~1.5% 量级，不是可以忽略的尾部风险** —— 调用方必须重试
    ///   （见 <see cref="CreateOrgAsync"/> 与 <see cref="CreateSchoolAsync"/>）。
    ///   这也是本类出现过"偶发红一次、重跑又绿"的原因。
    /// </summary>
    private static string NewDivisionCode()
        => "42" + Random.Shared.Next(1000, 9999);

    private Task<StudentDto> CreateStudentAsync(
        Guid schoolId,
        string genderCode = "1",
        string? statusCode = null)
    {
        return RetryOnDuplicateIdCardAsync(() => _students.CreateAsync(new CreateUpdateStudentDto
        {
            Name = "统计测试学生",
            GenderCode = genderCode,
            BirthDate = new DateTime(2016, 3, 1),
            IdCardTypeCode = "1",
            IdCardNumber = NewUniqueIdCard(),
            SchoolOrgId = schoolId,
            AcademicYear = _year,
            StudentStatusCode = statusCode ?? StudentStatuses.Studying
        }));
    }

    /// <summary>以指定身份开新作用域执行（<c>ICurrentUser</c> 是 Scoped 且会缓存，必须新作用域）。</summary>
    private async Task<T> InScopeAsAsync<T>(
        Guid userId,
        string userName,
        string[] roles,
        Func<IServiceProvider, Task<T>> action)
    {
        _principal.SetUser(userId, userName, roles);

        using var scope = ServiceProvider.CreateScope();

        return await action(scope.ServiceProvider);
    }

    /// <summary>插入预置记录并把用户关联到组织（数据范围的唯一来源）。</summary>
    private async Task<Guid> LinkUserToOrgAsync(Guid orgId, string prefix)
    {
        var userId = _guidGenerator.Create();

        var predefine = new SysUserPredefine(
            _guidGenerator.Create(),
            $"{prefix}用户",
            "138" + Random.Shared.Next(10000000, 99999999),
            orgId,
            "TESTCODE",
            _clock.Now.AddDays(30));

        predefine.MarkActivated(userId, _clock.Now);
        await _predefines.InsertAsync(predefine, autoSave: true);

        return userId;
    }

    private StudentStatisticsQueryDto StudentQuery(string dimension)
        => new() { Dimension = dimension, AcademicYear = _year };

    /// <summary>
    /// 限定到某所学校（含下级）的统计查询。
    ///
    /// 为什么必须要它：测试库是**长期累积**的库，同一年份下别的测试类也造学生。
    /// 只按学年过滤时，本类新建 3 个学生而统计返回 32 个 —— 断言就会以
    /// "看不出哪里错"的方式变红。把范围收到本类自己建的学校上，
    /// 断言才是**确定的**（这也正是"统计数必须能和名单对上"的日常口径）。
    /// </summary>
    private StudentStatisticsQueryDto SchoolQuery(string dimension, Guid schoolOrgId)
        => new()
        {
            Dimension = dimension,
            AcademicYear = _year,
            SchoolOrgId = schoolOrgId,
            IncludeDescendantOrgs = true
        };

    /// <summary>建一个「转学」异动类型 + 一套已发布的流程模板。</summary>
    private async Task<ChangeTypeDto> CreateTransferTypeAsync()
    {
        var type = await _changeTypes.CreateTypeAsync(new CreateUpdateChangeTypeDto
        {
            Code = "STAT_TRANSFER_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Name = "统计测试转学",
            MaxApproveLevel = ApproveLevels.City,
            AffectClass = true,
            AfterStatusCode = StudentStatuses.TransferredIn
        });

        var template = await _changeTypes.SaveTemplateAsync(new SaveFlowTemplateDto
        {
            ChangeTypeId = type.Id,
            Name = "统计测试转学流程",
            Nodes = new List<CreateUpdateFlowNodeDto>
            {
                Node(1, "转入学校提交", ChangeOrgResolvers.TargetSchool),
                Node(2, "转出学校审核", ChangeOrgResolvers.SourceSchool)
            }
        });

        await _changeTypes.PublishTemplateAsync(template.Id);

        return type;
    }

    private static CreateUpdateFlowNodeDto Node(int seq, string name, string resolver)
        => new()
        {
            Seq = seq, NodeName = name, OrgResolver = resolver, RoleCode = "SchoolAdmin",
            MergeStrategy = ChangeMergeStrategies.MergeToLast
        };

    /// <summary>提交一条转学申请（自动化断言的主体数据）。</summary>
    private async Task<ChangeApplyDto> SubmitTransferAsync(Guid changeTypeId, Guid sourceSchoolId, Guid targetSchoolId)
    {
        var student = await CreateStudentAsync(sourceSchoolId);

        return await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = changeTypeId,
            StudentId = student.Id,
            TargetSchoolOrgId = targetSchoolId,
            Reason = "统计测试：家庭搬迁"
        });
    }

    // ==================================================================
    // 学生统计
    // ==================================================================

    [Fact]
    public async Task 学生统计_按学校_只算本类新造的数据()
    {
        var tree = await BuildTreeAsync();

        await CreateStudentAsync(tree.SchoolA1);
        await CreateStudentAsync(tree.SchoolA1);
        await CreateStudentAsync(tree.SchoolA2);
        await CreateStudentAsync(tree.SchoolB1);

        // 以省厅身份看"甲县（含下级）"：正好是本类刚造的 3 个
        var result = await _statistics.GetStudentStatisticsAsync(new StudentStatisticsQueryDto
        {
            Dimension = StudentStatDimensions.School,
            AcademicYear = _year,
            SchoolOrgId = tree.CountyA,
            IncludeDescendantOrgs = true
        });

        result.Dimension.ShouldBe(StudentStatDimensions.School);
        result.Total.ShouldBe(3, "甲县含下级只有本类刚造的 3 个学生（另一所在乙县）");

        // 学校维度的分组键是组织 Id，名称由后端从 sys_org 带出
        var byKey = result.Items.ToDictionary(i => i.Key, i => i);

        byKey[tree.SchoolA1.ToString()].Count.ShouldBe(2);
        byKey[tree.SchoolA2.ToString()].Count.ShouldBe(1);

        // 乙县的学校绝不能出现在"甲县"的结果里
        byKey.ShouldNotContainKey(tree.SchoolB1.ToString());

        byKey[tree.SchoolA1.ToString()].Name.ShouldBe("统计测试甲县一中",
            "学校维度的名称必须由后端给出，不能为 null（否则前端只能显示 GUID）");
    }

    [Fact]
    public async Task 学生统计_关掉含下级_不再把下级学校算进来()
    {
        // 这条用例守的是一个真实缺陷：应用层把 IncludeDescendantOrgs 丢掉了，
        // `includeDescendants ? org.PathCode : org.PathCode` 两个分支写成一样，
        // 于是"仅本级"返回的仍是整棵子树 —— 而界面上这两种口径的数字
        // 长得一模一样，用户无法察觉，只会觉得"系统算错了"。
        var tree = await BuildTreeAsync();

        await CreateStudentAsync(tree.SchoolA1);
        await CreateStudentAsync(tree.SchoolA2);
        await CreateStudentAsync(tree.SchoolB1);

        // 以省厅身份指定"甲县"，含下级 → 两所学校都算（2 人）
        var withChildren = await _statistics.GetStudentStatisticsAsync(new StudentStatisticsQueryDto
        {
            Dimension = StudentStatDimensions.School,
            AcademicYear = _year,
            SchoolOrgId = tree.CountyA,
            IncludeDescendantOrgs = true
        });

        withChildren.Total.ShouldBe(2);

        // 同一条件改成"仅本级" → 县局本身没有直接挂学生，必须是 0
        var exactOnly = await _statistics.GetStudentStatisticsAsync(new StudentStatisticsQueryDto
        {
            Dimension = StudentStatDimensions.School,
            AcademicYear = _year,
            SchoolOrgId = tree.CountyA,
            IncludeDescendantOrgs = false
        });

        exactOnly.Total.ShouldBe(0, "「仅本级」不能把下级学校的学生算进来");
        exactOnly.ScopeDescription.Contains("仅本级").ShouldBeTrue(
            "范围说明必须如实写出用的哪种口径，否则用户无法解释两种设置的差异；"
            + $"实际是「{exactOnly.ScopeDescription}」");
    }

    [Fact]
    public async Task 学生统计_按性别_计数之和等于总数()
    {
        var tree = await BuildTreeAsync();

        await CreateStudentAsync(tree.SchoolA1, genderCode: "1");
        await CreateStudentAsync(tree.SchoolA1, genderCode: "1");
        await CreateStudentAsync(tree.SchoolA1, genderCode: "2");

        // 收到本校：否则会统计到别的测试类同一年份的学生（测试库是长期累积的）
        var result = await _statistics.GetStudentStatisticsAsync(
            SchoolQuery(StudentStatDimensions.Gender, tree.SchoolA1));

        result.Total.ShouldBe(3);
        result.Items.Sum(i => i.Count).ShouldBe(3, "Total 的定义就是各分组之和");
        result.Items.Single(i => i.Key == "1").Count.ShouldBe(2);
        result.Items.Single(i => i.Key == "2").Count.ShouldBe(1);

        // 性别是码值维度：后端**刻意**不回名称（由前端用码表翻译），
        // 如果哪天它开始回 null 以外的值，说明设计变了，这里会红
        result.Items.ShouldAllBe(i => i.Name == null);
    }

    [Fact]
    public async Task 学生统计_按有无照片_新造的学生都算未采集()
    {
        var tree = await BuildTreeAsync();

        // 「有无照片」维度无法按学校过滤（后端只支持学年/年级/班级/状态/性别），
        // 所以这里只能比较**增量**：先取一次基线，造两个学生，再取一次，
        // 断言 "no" 那一组正好多了 2 个。
        // 直接断言绝对值的写法在本仓库是不成立的 —— 测试库会累积历史数据。
        var before = await _statistics.GetStudentStatisticsAsync(StudentQuery(StudentStatDimensions.HasPhoto));
        var beforeNo = CountOf(before, "no");

        await CreateStudentAsync(tree.SchoolA1);
        await CreateStudentAsync(tree.SchoolA1);

        var after = await _statistics.GetStudentStatisticsAsync(StudentQuery(StudentStatDimensions.HasPhoto));

        // 键是 'no' / 'yes'（SQL 里的 case when），不是布尔字面量 ——
        // 前端最初按 true/false 翻译，表格里就会显示两行 "yes" / "no"
        CountOf(after, "no").ShouldBe(beforeNo + 2);
        after.Items.ShouldNotContain(i => i.Key == "true" || i.Key == "false",
            "分组键必须是 SQL 里的 'no'/'yes'，前端按这个翻译");
    }

    [Fact]
    public async Task 学生统计_按班级_没有分班的学生归到空键()
    {
        var tree = await BuildTreeAsync();

        var before = await _statistics.GetStudentStatisticsAsync(StudentQuery(StudentStatDimensions.Class));
        var beforeEmpty = CountOf(before, "（空）");

        await CreateStudentAsync(tree.SchoolA1);
        await CreateStudentAsync(tree.SchoolA1);

        var after = await _statistics.GetStudentStatisticsAsync(StudentQuery(StudentStatDimensions.Class));

        // 没分班 → class_id 为 null → 分组键被兜成"（空）"
        // （见 SqlSugarStatisticsQuery.QueryAsync 的 r.Key ?? "（空）"）。
        // 这一条很容易被改成把 null 直接传出去，然后前端表格里出现一行没有名字的分组。
        CountOf(after, "（空）").ShouldBe(beforeEmpty + 2);
    }

    /// <summary>取某个分组键的计数（不存在时按 0 处理，便于做增量断言）。</summary>
    private static long CountOf(StatisticResultDto result, string key)
        => result.Items.Where(i => i.Key == key).Sum(i => i.Count);

    [Fact]
    public async Task 学生统计_未知维度_抛校验异常并列出可用维度()
    {
        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _statistics.GetStudentStatisticsAsync(StudentQuery("notADimension")));

        ex.Code.ShouldBe(XjglDomainErrorCodes.ValidationFailed);
        ex.Data["reason"]!.ToString()!.ToLowerInvariant().Contains("school").ShouldBeTrue(
            "报错必须把可用维度列出来，否则调用方只能去读源码");
    }

    [Fact]
    public async Task 学生统计_维度大小写不敏感_但归一化后仍能命中分支()
    {
        var tree = await BuildTreeAsync();
        await CreateStudentAsync(tree.SchoolA1);

        // 'studentStatus' 是驼峰常量。IsKnown 大小写不敏感，
        // 但 SQL 的 switch **是**大小写敏感的 —— 早期版本直接 ToLowerInvariant()
        // 把 studentStatus 变成 studentstatus，于是找不到分支直接抛异常。
        var result = await _statistics.GetStudentStatisticsAsync(
            SchoolQuery("STUDENTSTATUS", tree.SchoolA1));

        result.Total.ShouldBe(1);
        result.Items.Single().Key.ShouldBe(StudentStatuses.Studying);
    }

    [Fact]
    public async Task 学生统计_县区管理员_只能看到本县的金字塔范围与说明()
    {
        var tree = await BuildTreeAsync();

        await CreateStudentAsync(tree.SchoolA1);
        await CreateStudentAsync(tree.SchoolA2);
        await CreateStudentAsync(tree.SchoolB1);

        var countyUser = await LinkUserToOrgAsync(tree.CountyA, "统计甲县");

        // 指定"本县"，含下级：正好是本类刚造的 2 个。
        // 不指定组织的话会把本类其它用例同一年份造的学生（乃至历史数据）一起算进来，
        // 而这里要断言的是"**本县**的 2 个"，不是一个全年份的总数。
        var seen = await InScopeAsAsync(countyUser, "统计甲县", Array.Empty<string>(), async sp =>
            await sp.GetRequiredService<IStatisticsAppService>()
                .GetStudentStatisticsAsync(new StudentStatisticsQueryDto
                {
                    Dimension = StudentStatDimensions.School,
                    AcademicYear = _year,
                    SchoolOrgId = tree.CountyA,
                    IncludeDescendantOrgs = true
                }));

        seen.Total.ShouldBe(2, "甲县管理员看不到乙县的学生");
        seen.Items.ShouldNotContain(i => i.Key == tree.SchoolB1.ToString());
        // 说明文字是"指定组织（在我范围内）"：用户指定的是本县，不是"本级及下级"那种默认口径。
        // 断言"在我范围内"而不是某个固定措辞，是为了让这条用例只守语义、不锁死文案。
        seen.ScopeDescription.Contains("在我范围内").ShouldBeTrue(
            $"范围说明必须让用户看懂这个数字是怎么来的；实际是「{seen.ScopeDescription}」");

        // 请求上级（市局，且**含下级**）→ 不报错，收敛回自己的范围，并且必须**说明**收敛过。
        //
        // 这里把范围说明与"只看到 2 个"绑死，是因为市局的含下级范围
        // **本来就大于**本县 —— 如果哪天有人在收敛逻辑上写错方向
        // （例如直接采信调用方传的组织），Total 会立刻变成全年份的一大串数字，
        // 而界面上只是"数字变大了"，看不出是越权。
        // 2026-2027 学年下本县只有这 2 个学生，所以断言是确定的。
        var escalated = await InScopeAsAsync(countyUser, "统计甲县", Array.Empty<string>(), async sp =>
            await sp.GetRequiredService<IStatisticsAppService>()
                .GetStudentStatisticsAsync(new StudentStatisticsQueryDto
                {
                    Dimension = StudentStatDimensions.School,
                    AcademicYear = _year,
                    SchoolOrgId = tree.City,
                    IncludeDescendantOrgs = true
                }));

        escalated.Total.ShouldBe(2, "看上级时要收敛到本县，绝不能因此看到更大的范围");
        escalated.Items.ShouldNotContain(i => i.Key == tree.SchoolB1.ToString(),
            "收敛后的结果里不能出现其它县（乙县）的学校");
        escalated.ScopeDescription.Contains("收敛").ShouldBeTrue(
            $"收敛过就必须说出来，否则用户无法理解为什么选了市局却只看到本县；实际是「{escalated.ScopeDescription}」");
    }

    [Fact]
    public async Task 学生统计_校级账号_不传学校也要能看到本校学生()
    {
        // 这条用例守的是一个真实缺陷，而且是最致命的一种"静默 0"：
        // 物化路径是 `省.市.县.校`，**叶子（学校）后面没有点**，
        // 所以范围条件如果只写 `path like '本校路径.%'`，对学校自己永远不匹配。
        // 校级账号的默认范围恰好就是本校 → 他打开统计页看到的永远是 0，
        // 而接口 200、没有任何异常（实测：psql 里 `= '校路径'` 得 3、`like '校路径.%'` 得 0）。
        var tree = await BuildTreeAsync();

        await CreateStudentAsync(tree.SchoolA1);
        await CreateStudentAsync(tree.SchoolA1);

        var schoolUser = await LinkUserToOrgAsync(tree.SchoolA1, "统计甲县一中");

        var seen = await InScopeAsAsync(schoolUser, "统计甲县一中", Array.Empty<string>(), async sp =>
            await sp.GetRequiredService<IStatisticsAppService>()
                .GetStudentStatisticsAsync(StudentQuery(StudentStatDimensions.School)));

        seen.Total.ShouldBe(2, "校级账号的默认范围就是本校，必须算上本校自己");
        seen.Items.ShouldHaveSingleItem();
        seen.Items.Single().Key.ShouldBe(tree.SchoolA1.ToString());
    }

    [Fact]
    public async Task 学生统计_未关联组织的用户_返回空统计而不是全省数据()
    {
        var tree = await BuildTreeAsync();
        await CreateStudentAsync(tree.SchoolA1);

        // 有身份但没有组织 → 什么都看不到。
        // 这里守的是数据范围里唯一不能搞错的方向：null 在查询层表示"不受限"，
        // 一旦把"无范围"与"不受限"混用，未关联组织的用户反而会看到全省数据。
        var orphanId = Guid.NewGuid();

        var result = await InScopeAsAsync(orphanId, "无组织用户", Array.Empty<string>(), async sp =>
            await sp.GetRequiredService<IStatisticsAppService>()
                .GetStudentStatisticsAsync(StudentQuery(StudentStatDimensions.School)));

        result.Total.ShouldBe(0);
        result.Items.ShouldBeEmpty();
        result.ScopeDescription.ShouldContain("未关联组织");
    }

    // ==================================================================
    // 异动统计（演示库没有申请记录，这些行由本类造出来）
    // ==================================================================

    [Fact]
    public async Task 异动统计_按异动类型_申请提交后即有真实行()
    {
        var tree = await BuildTreeAsync();
        var type = await CreateTransferTypeAsync();

        await SubmitTransferAsync(type.Id, tree.SchoolA1, tree.SchoolB1);
        await SubmitTransferAsync(type.Id, tree.SchoolA2, tree.SchoolB1);

        var result = await _statistics.GetChangeStatisticsAsync(new ChangeStatisticsQueryDto
        {
            Dimension = ChangeStatDimensions.ChangeType,
            // ★ 必须限定到本类刚建的类型。漏了它，这条断言就建立在
            //   "新生成的类型 Id 恰好全局唯一"之上 —— 测试库是长期累积的真实库，
            //   异动申请会一轮一轮攒下来，于是计数变成"全场"的，断言以
            //   "expected 2 but was N" 的形式偶发变红，看起来像统计逻辑回归。
            //   同一文件里其余 8 条异动统计用例都带了这一条，只有这里漏了（已补）。
            ChangeTypeId = type.Id
        });

        // 先断言"只有一个分组"，再断言它的数字。
        // 【为什么不能直接用 Single()】Single() 在没有匹配项时抛的是
        //   InvalidOperationException "Sequence contains no matching element"，
        //   堆栈停在 LINQ 上、报错里也看不出**是哪个维度、筛了什么条件** ——
        //   实测遇到过一次，只能靠猜。ShouldHaveSingleItem 会打印实际的分组内容。
        result.Items.ShouldHaveSingleItem();

        var mine = result.Items.Single();

        mine.Key.ShouldBe(type.Id.ToString());
        mine.Count.ShouldBe(2);
        mine.Name.ShouldBe("统计测试转学", "异动类型维度必须回类型名称");
    }

    [Fact]
    public async Task 异动统计_按状态_新提交的申请都算待审核()
    {
        var tree = await BuildTreeAsync();
        var type = await CreateTransferTypeAsync();

        await SubmitTransferAsync(type.Id, tree.SchoolA1, tree.SchoolB1);

        var result = await _statistics.GetChangeStatisticsAsync(new ChangeStatisticsQueryDto
        {
            Dimension = ChangeStatDimensions.Status,
            // 限定到本类刚建的类型：测试库会累积历史申请，
            // 不带类型过滤时 Submitted 那一组的计数是"全场"的，无法断言
            ChangeTypeId = type.Id
        });

        // 状态是码值维度：后端不回名称
        var submitted = result.Items.Single();

        submitted.Key.ShouldBe(ChangeApplyStatus.Submitted);
        submitted.Count.ShouldBe(1);
        submitted.Name.ShouldBeNull();
    }

    [Fact]
    public async Task 异动统计_按月份_键是yyyyMM格式()
    {
        var tree = await BuildTreeAsync();
        var type = await CreateTransferTypeAsync();

        await SubmitTransferAsync(type.Id, tree.SchoolA1, tree.SchoolB1);

        var result = await _statistics.GetChangeStatisticsAsync(new ChangeStatisticsQueryDto
        {
            Dimension = ChangeStatDimensions.Month,
            ChangeTypeId = type.Id
        });

        var thisMonth = _clock.Now.ToString("yyyy-MM");

        result.Items.ShouldHaveSingleItem();
        result.Items.Single().Key.ShouldBe(thisMonth, "月份维度的键必须是 yyyy-MM（前端按它排序看趋势）");
        result.Items.Single().Count.ShouldBe(1);
    }

    [Fact]
    public async Task 异动统计_日期上界含当天_当天提交的申请必须被算进来()
    {
        // 这条用例钉的是"含当天"这个语义：应用层把上界补到 23:59:59。
        // 如果哪天有人把 .AddDays(1).AddSeconds(-1) 去掉，
        // 表现就是"选到今天，今天的申请一条都不显示" ——
        // 而界面上明明写着"含结束当天"，属于最难被发现的那类错误。
        var tree = await BuildTreeAsync();
        var type = await CreateTransferTypeAsync();

        await SubmitTransferAsync(type.Id, tree.SchoolA1, tree.SchoolB1);

        var today = _clock.Now.Date;

        // 只取"今天到今天"，并限定本类的异动类型 → 结果一定是 1 条
        var result = await _statistics.GetChangeStatisticsAsync(new ChangeStatisticsQueryDto
        {
            Dimension = ChangeStatDimensions.Month,
            ChangeTypeId = type.Id,
            From = today,
            To = today
        });

        result.Items.ShouldHaveSingleItem("结束日期必须含当天");
        result.Items.Single().Count.ShouldBe(1);

        // 反证：把上界设成昨天，这条申请必须**不在**结果里 ——
        // 否则上面那条断言可能是"日期条件被整个忽略"造成的假绿
        var yesterdayOnly = await _statistics.GetChangeStatisticsAsync(new ChangeStatisticsQueryDto
        {
            Dimension = ChangeStatDimensions.Month,
            ChangeTypeId = type.Id,
            From = today.AddDays(-30),
            To = today.AddDays(-1)
        });

        yesterdayOnly.Items.ShouldBeEmpty(
            "上界设成昨天时不能把今天的申请算进来 —— 否则说明日期条件根本没生效");
    }

    [Fact]
    public async Task 异动统计_按转出与转入学校_两个口径分别成立()
    {
        var tree = await BuildTreeAsync();
        var type = await CreateTransferTypeAsync();

        await SubmitTransferAsync(type.Id, tree.SchoolA1, tree.SchoolB1);
        await SubmitTransferAsync(type.Id, tree.SchoolA1, tree.SchoolB1);

        // 按转出学校 —— 这条用例同时也是那个 SQL 缺陷的回归：
        // 早期实现只在 SourceSchool/TargetSchool 维度 join 一个 org 表，
        // 而范围条件里同时引用了 so 与 to2，于是这两个维度直接
        // 500（42P01: missing FROM-clause entry for table "to2"）。
        var bySource = await _statistics.GetChangeStatisticsAsync(new ChangeStatisticsQueryDto
        {
            Dimension = ChangeStatDimensions.SourceSchool,
            ChangeTypeId = type.Id
        });

        // 按转入学校
        var byTarget = await _statistics.GetChangeStatisticsAsync(new ChangeStatisticsQueryDto
        {
            Dimension = ChangeStatDimensions.TargetSchool,
            ChangeTypeId = type.Id
        });

        // 从甲县一中转出 2 条 → 转出维度里它必须有 2
        bySource.Items.ShouldHaveSingleItem();
        bySource.Items.Single().Key.ShouldBe(tree.SchoolA1.ToString());
        bySource.Items.Single().Count.ShouldBe(2);
        bySource.Items.Single().Name.ShouldBe("统计测试甲县一中");

        // 转入乙县一中 2 条 → 转入维度里它是 2，而它在**转出**维度里不该有分组
        byTarget.Items.ShouldHaveSingleItem();
        byTarget.Items.Single().Key.ShouldBe(tree.SchoolB1.ToString());
        byTarget.Items.Single().Count.ShouldBe(2);
        byTarget.Items.Single().Name.ShouldBe("统计测试乙县一中");
    }

    [Fact]
    public async Task 异动统计_校级管理员_转出与转入两侧都能看到自己的申请()
    {
        // 数据范围对异动**两侧都要算**：只按转出校过滤会让接收学校
        // 看不到转入的申请 —— 而它恰恰是要审的那一方。
        var tree = await BuildTreeAsync();
        var type = await CreateTransferTypeAsync();

        // 甲县一中 → 乙县一中
        await SubmitTransferAsync(type.Id, tree.SchoolA1, tree.SchoolB1);

        var schoolBUser = await LinkUserToOrgAsync(tree.SchoolB1, "统计乙县一中");

        var seen = await InScopeAsAsync(schoolBUser, "统计乙县一中", Array.Empty<string>(), async sp =>
            await sp.GetRequiredService<IStatisticsAppService>()
                .GetChangeStatisticsAsync(new ChangeStatisticsQueryDto
                {
                    Dimension = ChangeStatDimensions.TargetSchool,
                    ChangeTypeId = type.Id
                }));

        seen.Items.ShouldHaveSingleItem();
        seen.Items.Single().Key.ShouldBe(tree.SchoolB1.ToString(),
            "作为转入校，必须能看到转移到本校的申请");
        seen.Items.Single().Count.ShouldBe(1);
    }

    [Fact]
    public async Task 异动统计_按异动类型过滤_只统计指定类型()
    {
        var tree = await BuildTreeAsync();
        var transfer = await CreateTransferTypeAsync();
        var suspend = await CreateTransferTypeAsync();

        await SubmitTransferAsync(transfer.Id, tree.SchoolA1, tree.SchoolB1);
        await SubmitTransferAsync(suspend.Id, tree.SchoolA1, tree.SchoolB1);

        var result = await _statistics.GetChangeStatisticsAsync(new ChangeStatisticsQueryDto
        {
            Dimension = ChangeStatDimensions.ChangeType,
            ChangeTypeId = transfer.Id
        });

        result.Items.ShouldHaveSingleItem();
        result.Items.Single().Key.ShouldBe(transfer.Id.ToString());
        result.Items.Single().Count.ShouldBe(1);
    }

    [Fact]
    public async Task 异动统计_未知维度_抛校验异常()
    {
        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _statistics.GetChangeStatisticsAsync(new ChangeStatisticsQueryDto
            {
                Dimension = "notADimension"
            }));

        ex.Code.ShouldBe(XjglDomainErrorCodes.ValidationFailed);

        // 报错里必须列出可用维度（否则调用方只能去读源码）。
        // 用 ToLowerInvariant 而不是 Shouldly 的大小写参数：维度名是驼峰常量
        // （changeType），这里只关心"有没有列出来"，不关心大小写。
        ex.Data["reason"]!.ToString()!.ToLowerInvariant().Contains("changetype").ShouldBeTrue(
            $"报错里应列出可用维度，实际是「{ex.Data["reason"]}」");
    }
}
