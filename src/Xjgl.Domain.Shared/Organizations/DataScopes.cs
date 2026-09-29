namespace Xjgl.Organizations;

/// <summary>
/// 数据范围。
/// 与微代码 <c>DATA_SCOPE</c> 的码值保持一致。
/// 权限决定「能做什么」，数据范围决定「能对谁做」——两者正交。
///
/// 【为什么在 Domain.Shared 而不是 Domain】
/// 它是一组**跨层共享的常量**（领域判定、应用层查询、接口层的范围下拉都要用）。
/// 放在 Domain 里，`Xjgl.Application.Contracts` 就用不到 ——
/// 契约层不该反向依赖领域层（本项目其余模块的做法同此：
/// `ExportTaskConsts`、`ChangeFlowConventions` 也都在 Domain.Shared）。
/// </summary>
public static class DataScopes
{
    /// <summary>全部数据（省厅超管、省级管理员）。</summary>
    public const string All = "All";

    /// <summary>本级及下级（默认值，符合「上级能看下级」的行政逻辑）。</summary>
    public const string OrgAndChildren = "OrgAndChildren";

    /// <summary>仅本级。</summary>
    public const string OrgOnly = "OrgOnly";

    /// <summary>仅本人（如教师只维护自己录入的数据）。</summary>
    public const string SelfOnly = "SelfOnly";

    /// <summary>自定义组织集合。</summary>
    public const string Custom = "Custom";

    /// <summary>
    /// 用于表达「仅本级」的哨兵前缀：一个在真实物化路径里不可能出现的字符。
    ///
    /// 这样"仅本级"就退化成"前缀永不匹配、只剩路径相等"，
    /// 从而不必在表达式里写三元 —— SqlSugar 翻译不了嵌套三元的路径比较，
    /// 实测会抛 `argument do not support`（见 `StudentAppService` 的说明）。
    ///
    /// 【为什么放在这里，而不是各服务各自 `private const`】
    /// 它已经有 **4 个**使用点（学生 / 照片 / 变更留痕 / 预置名单）。
    /// 各写一份的真实代价不是"重复几行"，而是**新加一处查询时会漏掉这一条** ——
    /// 而漏掉它的表现是 `OrgOnly` 静默退化成"本级及下级"，也就是
    /// **比配置更宽**（管理员以为收紧了，实际放开了，且不报错）。
    /// 预置名单最初就是漏掉的那一处。
    /// </summary>
    public const string ImpossiblePathPrefix = "\u0001";

    public static readonly string[] AllScopes =
    {
        All, OrgAndChildren, OrgOnly, SelfOnly, Custom
    };

    /// <summary>
    /// 可以**配置到角色上**的范围。
    ///
    /// 刻意不含 <see cref="Custom"/>：自定义组织集合需要额外存一组组织 Id，
    /// 而各查询的 SQL 谓词目前都按"单一根路径"生成（`路径相等 OR 前缀匹配`）。
    /// 与其让它悄悄退化成"本级及下级"（那会**比配置更宽**），
    /// 不如先不允许配置 —— 见 ROADMAP 第 21 条。
    /// </summary>
    public static readonly string[] Configurable =
    {
        All, OrgAndChildren, OrgOnly, SelfOnly
    };

    public static bool IsConfigurable(string? scope)
        => !string.IsNullOrWhiteSpace(scope) && Configurable.Contains(scope);

    public static string GetDisplayName(string scope) => scope switch
    {
        All => "全部数据",
        OrgAndChildren => "本级及下级",
        OrgOnly => "仅本级",
        SelfOnly => "仅本人",
        Custom => "自定义组织集合",
        _ => scope
    };

    /// <summary>
    /// 范围的一句话解释（前端在选项下面显示"选它意味着能看谁"）。
    ///
    /// 【为什么这句文案放在后端】
    /// 放在前端就得写一份"码值 → 文案"的映射，而那份映射就是**第二份码值**：
    /// 码值改名或新增时它不会报错，只会静默少一句提示（或显示不出说明）。
    /// 本项目已经被"前端手写码值"坑过好几次（学籍状态过滤、异动前后状态），
    /// 所以文案与码值一样，由后端连同选项一起下发 —— 前端一个码值都不写。
    /// </summary>
    public static string GetDescription(string scope) => scope switch
    {
        All => "能看全省的数据（含所有下级）。省厅与需要跨区汇总的角色用它。",
        OrgAndChildren => "能看本组织**及所有下级**。默认行为，也是四级行政体系里最常用的口径。",
        OrgOnly => "只看本组织这一级，**不含下级**。例：某市局只看本市、不看他下属的县。",
        SelfOnly => "只看本组织范围内**自己录入**的数据。适合新入职、实习、代管的账号。",
        Custom => "自定义一组组织（尚未支持配置）。",
        _ => string.Empty
    };

    /// <summary>
    /// 一个角色上配置的范围有多"宽"（越大越宽）。
    ///
    /// 未知值（含未启用的 <see cref="Custom"/>）按 <see cref="OrgOnly"/> 计 ——
    /// 本项目在数据范围上的原则是**宁可收紧也不要放行**：
    /// 认不出来的配置绝不能比它写着的更宽。
    /// </summary>
    private static int Rank(string scope) => scope switch
    {
        All => 4,
        OrgAndChildren => 3,
        OrgOnly => 2,
        SelfOnly => 1,
        _ => 2
    };

    /// <summary>
    /// 从"用户各角色上配置的范围"里选出**生效**的那一个；没有任何配置时返回 <c>null</c>。
    ///
    /// 【为什么多个角色取最宽，而不是最窄】
    /// 角色在本系统里是**叠加**的（权限就是按加法合并的：任一角色授予即拥有）。
    /// 数据范围如果反过来取最窄，就会出现一件无法解释的事：
    /// 给某人加一个"全省"的角色，他的可见范围**反而变小了**。
    /// 那种"权限越多看得越少"的行为没有业务人员能理解，也没有人能排查。
    ///
    /// 因此这里与权限保持同一套语义：**最宽者胜**。
    /// 代价是"加一个宽范围的角色等于扩权"—— 这本来就是角色的含义，
    /// 所以配置界面上要把每个角色配了什么范围显示清楚（见角色管理页的「数据范围」列）。
    /// </summary>
    public static string? ResolveEffective(IEnumerable<string>? configuredScopes)
    {
        if (configuredScopes == null)
        {
            return null;
        }

        string? widest = null;

        foreach (var scope in configuredScopes)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                continue;
            }

            if (widest == null || Rank(scope) > Rank(widest))
            {
                widest = scope;
            }
        }

        return widest;
    }
}
