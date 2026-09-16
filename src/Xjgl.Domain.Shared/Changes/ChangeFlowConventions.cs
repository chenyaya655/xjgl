namespace Xjgl.Changes;

/// <summary>
/// 组织解析器：描述一个流程节点「审的是哪一级组织」。
///
/// 这是整套可配置审批流的关键抽象 —— 节点不写死具体组织，
/// 而是写「转入学校所在县区」这样的**相对描述**，
/// 实例化时再结合具体学生与目标学校解析出真实组织，
/// 从而自动实现「同县区转学县区只审一次」。
/// </summary>
public static class ChangeOrgResolvers
{
    /// <summary>发起人所在学校（转学的转入校）。</summary>
    public const string ApplicantSchool = "ApplicantSchool";

    /// <summary>学生当前学籍所在学校。</summary>
    public const string StudentSchool = "StudentSchool";

    /// <summary>目标学校（转入校）。</summary>
    public const string TargetSchool = "TargetSchool";

    /// <summary>原学校（转出校）。</summary>
    public const string SourceSchool = "SourceSchool";

    /// <summary>目标学校所在县区。</summary>
    public const string TargetCounty = "TargetCounty";

    /// <summary>目标学校所在地市。</summary>
    public const string TargetCity = "TargetCity";

    /// <summary>原学校所在县区。</summary>
    public const string SourceCounty = "SourceCounty";

    /// <summary>原学校所在地市。</summary>
    public const string SourceCity = "SourceCity";

    /// <summary>省级教育行政部门。</summary>
    public const string Province = "Province";

    public static readonly string[] All =
    {
        ApplicantSchool, StudentSchool, TargetSchool, SourceSchool,
        TargetCounty, TargetCity, SourceCounty, SourceCity, Province
    };

    public static string GetDisplayName(string resolver) => resolver switch
    {
        ApplicantSchool => "发起人所在学校",
        StudentSchool => "学生当前学校",
        TargetSchool => "转入学校",
        SourceSchool => "转出学校",
        TargetCounty => "转入学校所在县区",
        TargetCity => "转入学校所在地市",
        SourceCounty => "转出学校所在县区",
        SourceCity => "转出学校所在地市",
        Province => "省级教育行政部门",
        _ => resolver
    };
}

/// <summary>节点合并策略。</summary>
public static class ChangeMergeStrategies
{
    /// <summary>合并到序号最靠后的节点（默认）。上级成为终审，符合行政实务。</summary>
    public const string MergeToLast = "MergeToLast";

    /// <summary>合并到序号最靠前的节点。</summary>
    public const string MergeToFirst = "MergeToFirst";

    /// <summary>不合并（同一组织会出现多个待办）。</summary>
    public const string NoMerge = "NoMerge";

    public static readonly string[] All = { MergeToLast, MergeToFirst, NoMerge };
}

/// <summary>审核级别及顺序。</summary>
public static class ApproveLevels
{
    public const string School = "School";
    public const string County = "County";
    public const string City = "City";
    public const string Province = "Province";

    private static readonly Dictionary<string, int> Order = new(StringComparer.Ordinal)
    {
        [School] = 1,
        [County] = 2,
        [City] = 3,
        [Province] = 4
    };

    public static int GetOrder(string level) => Order.TryGetValue(level, out var order) ? order : 0;

    public static bool IsValid(string level) => Order.ContainsKey(level);

    public static string GetDisplayName(string level) => level switch
    {
        School => "校级",
        County => "县区级",
        City => "地市级",
        Province => "省级",
        _ => level
    };
}
