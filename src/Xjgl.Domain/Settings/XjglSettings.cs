namespace Xjgl.Settings;

/// <summary>
/// 系统参数名常量。
/// 这些参数可在“系统管理 / 系统参数”里改，无需改代码即可调整运行行为。
/// </summary>
public static class XjglSettings
{
    public const string GroupName = "Xjgl";

    /// <summary>学生列表默认分页大小。</summary>
    public const string DefaultPageSize = GroupName + ".DefaultPageSize";

    /// <summary>学籍辅号规则（分段模板），格式见 IStudentNumberGenerator 实现。</summary>
    public const string StudentNumberRule = GroupName + ".StudentNumberRule";

    /// <summary>照片存储根目录（NAS 挂载点）。</summary>
    public const string PhotoRootPath = GroupName + ".PhotoStorage.RootPath";

    /// <summary>照片上传单批最大张数。</summary>
    public const string PhotoMaxBatchSize = GroupName + ".PhotoStorage.MaxBatchSize";

    /// <summary>照片写入并发舱壁大小。</summary>
    public const string PhotoBulkheadSize = GroupName + ".PhotoStorage.BulkheadSize";

    /// <summary>NAS 写入熔断：失败率阈值（0~1）。</summary>
    public const string PhotoCircuitBreakerFailureRate = GroupName + ".PhotoStorage.CircuitBreakerFailureRate";

    /// <summary>NAS 写入熔断：最短熔断时长（秒）。</summary>
    public const string PhotoCircuitBreakerSeconds = GroupName + ".PhotoStorage.CircuitBreakerSeconds";

    /// <summary>是否允许 NAS 不可用时降级写本地磁盘。</summary>
    public const string PhotoFallbackEnabled = GroupName + ".PhotoStorage.FallbackEnabled";

    /// <summary>学生照片规格：最小宽度（像素）。</summary>
    public const string PhotoMinWidth = GroupName + ".PhotoStorage.MinWidth";

    /// <summary>学生照片规格：最小高度（像素）。</summary>
    public const string PhotoMinHeight = GroupName + ".PhotoStorage.MinHeight";
}
