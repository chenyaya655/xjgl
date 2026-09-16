namespace Xjgl.Organizations;

/// <summary>
/// 数据范围。
/// 与微代码 <c>DATA_SCOPE</c> 的码值保持一致。
/// 权限决定「能做什么」，数据范围决定「能对谁做」——两者正交。
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

    public static readonly string[] AllScopes =
    {
        All, OrgAndChildren, OrgOnly, SelfOnly, Custom
    };

    public static string GetDisplayName(string scope) => scope switch
    {
        All => "全部数据",
        OrgAndChildren => "本级及下级",
        OrgOnly => "仅本级",
        SelfOnly => "仅本人",
        Custom => "自定义组织集合",
        _ => scope
    };
}
