namespace Xjgl.Statistics;

/// <summary>学生统计的维度。取值必须是这里列出的之一（写错会抛异常，见 IStatisticsQuery）。</summary>
public static class StudentStatDimensions
{
    /// <summary>按学校（最能反映"各校人数"的口径）。</summary>
    public const string School = "school";

    /// <summary>按年级。</summary>
    public const string Grade = "grade";

    /// <summary>按班级。</summary>
    public const string Class = "class";

    /// <summary>按学籍状态（在读 / 转出 / 休学 …）。</summary>
    public const string StudentStatus = "studentStatus";

    /// <summary>按性别。</summary>
    public const string Gender = "gender";

    /// <summary>按学年。</summary>
    public const string AcademicYear = "academicYear";

    /// <summary>按"有没有照片"（对应缺照片名单的补全进度）。</summary>
    public const string HasPhoto = "hasPhoto";

    public static readonly IReadOnlyList<string> All = new[]
    {
        School, Grade, Class, StudentStatus, Gender, AcademicYear, HasPhoto
    };

    public static bool IsKnown(string? dimension) =>
        dimension != null && All.Contains(dimension, StringComparer.OrdinalIgnoreCase);
}

/// <summary>异动统计的维度。</summary>
public static class ChangeStatDimensions
{
    /// <summary>按异动类型（转学 / 休学 / 复学 …）—— 这是用户明确要的口径。</summary>
    public const string ChangeType = "changeType";

    /// <summary>按审批状态。</summary>
    public const string Status = "status";

    /// <summary>按申请月份（yyyy-MM）。</summary>
    public const string Month = "month";

    /// <summary>按转出学校。</summary>
    public const string SourceSchool = "sourceSchool";

    /// <summary>按转入学校。</summary>
    public const string TargetSchool = "targetSchool";

    public static readonly IReadOnlyList<string> All = new[]
    {
        ChangeType, Status, Month, SourceSchool, TargetSchool
    };

    public static bool IsKnown(string? dimension) =>
        dimension != null && All.Contains(dimension, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 一个分组及其计数。
///
/// <paramref name="Key"/> 是分组键（Id 或码值），<paramref name="Name"/> 是可直接展示的名称。
/// **Name 可能为 null**：学校/年级/班级能从表里直接取到名称，而学籍状态、性别、异动状态
/// 是微代码码值 —— 那些名称由前端用既有的码表查询翻译（本系统前端一直这么干，
/// 在这里再查一遍码表等于把同一件事做两遍，还得为此在接口里绕一圈）。
/// </summary>
public sealed record StatCountItem(string Key, string? Name, long Count);

/// <summary>学生统计的过滤条件（与列表页口径一致，避免"统计数"和"列表数"对不上）。</summary>
public sealed class StudentStatFilter
{
    public Guid? SchoolOrgId { get; set; }

    /// <summary>
    /// **是否把下级组织的学校也算进来**。
    ///
    /// 必须显式传下来：这个开关决定 SQL 里是 `path like '路径.%'`（含后代）
    /// 还是 `path = '路径'`（仅本级）。统计接口最初**丢掉了这个参数**，
    /// 于是界面上关掉"含下级"后返回的仍然是整棵子树的人数 ——
    /// 而对使用者来说，"全县各校汇总"与"县局本级"是两个完全不同的口径，
    /// 数字对不上又不能解释，比报错更糟。
    /// </summary>
    public bool IncludeDescendantOrgs { get; set; }

    public string? AcademicYear { get; set; }

    public Guid? GradeId { get; set; }

    public Guid? ClassId { get; set; }

    public string? StudentStatusCode { get; set; }

    public string? GenderCode { get; set; }
}

/// <summary>异动统计的过滤条件。</summary>
public sealed class ChangeStatFilter
{
    public Guid? SchoolOrgId { get; set; }

    /// <summary>是否把下级组织也算进来（语义同 <see cref="StudentStatFilter.IncludeDescendantOrgs"/>）。</summary>
    public bool IncludeDescendantOrgs { get; set; }

    public Guid? ChangeTypeId { get; set; }

    public string? Status { get; set; }

    /// <summary>申请日期下界（含）。</summary>
    public DateTime? From { get; set; }

    /// <summary>申请日期上界（含当天，由调用方把时间补到 23:59:59）。</summary>
    public DateTime? To { get; set; }
}

/// <summary>
/// 统计聚合查询。
///
/// 【为什么它不在 Application 层用 IXjglRepository 实现】
/// 统计是 <c>GROUP BY</c> 的活，而 <c>IXjglRepository</c> 刻意不暴露 join/group。
/// 在内存里分组的做法在这个场景不成立：按学校统计有 6,789 个分组键，
/// 那意味着把全省学生整表读进内存再 GroupBy —— 一个人点一下就把服务器拖死。
///
/// 所以按本仓库既有的分层做法（同 <c>SqlSugarModuleMetadataCache</c>、
/// <c>SqlSugarXjglDbSchemaMigrator</c>）：**接口放 Domain，SqlSugar 实现放 Xjgl.SqlSugarCore**。
/// 这样应用层仍然只依赖抽象，"业务层不得直接依赖 SqlSugar"这条约束不被破坏。
///
/// <paramref name="scopePathCode"/> 为 <c>null</c> 表示不受限（省厅 / admin）；
/// 否则是当前用户组织的物化路径，实现里按 <c>LIKE '路径%'</c> 前缀过滤 ——
/// 与 <c>UserDataScope.AllowsEntityPath</c> 同一套语义，**统计绝不能比列表看到更多**。
/// 过滤条件里的 <c>IncludeDescendantOrgs</c> 决定是"本级及下级"（前缀匹配）
/// 还是"仅本级"（路径完全相等）。
/// </summary>
public interface IStatisticsQuery
{
    Task<IReadOnlyList<StatCountItem>> CountStudentsAsync(
        string dimension,
        string? scopePathCode,
        StudentStatFilter filter,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StatCountItem>> CountChangesAsync(
        string dimension,
        string? scopePathCode,
        ChangeStatFilter filter,
        CancellationToken cancellationToken = default);
}
