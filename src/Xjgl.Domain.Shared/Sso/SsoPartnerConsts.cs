namespace Xjgl.Sso;

/// <summary>
/// 教育系统内其它业务系统（RP）接入约定的常量。
///
/// 【为什么这一小撮常量不放在 Host 的 SsoConstants 里】
/// 种子逻辑在 <c>Xjgl.Domain</c>（OpenIddictDataSeedContributor），而 Domain
/// **不依赖也不允许依赖** Host —— 所以 Host 里的常量它引用不到。
/// 按分层约束「接口与视图模型放 Domain.Shared」，凡是种子与 Host 都要认的字符串
/// 必须落在这一层；Host 侧只保留它自己用的端点路径、Cookie 名与前端契约。
///
/// 【为什么值得为几个字符串单开一个类】
/// 它们同时出现在"appsettings 的键"与"种子代码读取的键"两处。
/// 两边各写一遍字面量的后果是：改了配置节名，种子静默地什么都不种 ——
/// DbMigrator 正常退出、日志正常，只是 RP 一个都没登记。
/// </summary>
public static class SsoPartnerConsts
{
    /// <summary>RP 种子配置节：<c>OpenIddict:EducationResourcePartners</c>。</summary>
    public const string ConfigSection = "OpenIddict:EducationResourcePartners";

    /// <summary>客户端注册的「前通道登出地址」在配置里的键名。</summary>
    public const string FrontChannelLogoutUriKey = "FrontChannelLogoutUri";

    /// <summary>
    /// 只配置了 RootUrl 时推导出的登录回跳路径（占位用）。
    ///
    /// ⚠ 推导值几乎不可能与真实 RP 的注册值一致，它的唯一作用是让占位配置
    ///   也能通过 URI 格式校验。真实联调必须显式配置三个地址。
    /// </summary>
    public const string DefaultRedirectPath = "/signin-oidc";

    /// <summary>只配置了 RootUrl 时推导出的登出后回跳路径（占位用，理由同上）。</summary>
    public const string DefaultPostLogoutRedirectPath = "/signout-callback-oidc";

    /// <summary>只配置了 RootUrl 时推导出的前通道登出地址（占位用，理由同上）。</summary>
    public const string DefaultFrontChannelLogoutPath = "/signout-iframe";
}
