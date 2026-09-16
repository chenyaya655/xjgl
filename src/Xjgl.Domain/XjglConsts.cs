namespace Xjgl;

/// <summary>
/// 全局常量。
/// </summary>
public static class XjglConsts
{
    /// <summary>
    /// EF Core 的表前缀。
    /// ABP 框架自带的表固定使用 Abp 前缀（AbpUsers / AbpAuditLogs ...），
    /// 业务表由 SqlSugar 负责建表并使用 sys_ / stu_ 前缀，因此这里保持空串。
    /// </summary>
    public const string DbTablePrefix = "";

    /// <summary>数据库 Schema，PostgreSQL 下使用默认的 public。</summary>
    public const string? DbSchema = null;

    /// <summary>OpenIddict 受众（资源），与 Host 中 AddAudiences 保持一致。</summary>
    public const string OpenIddictAudience = "Xjgl";

    /// <summary>本系统 API 的 OAuth Scope 名称。</summary>
    public const string ApiScope = "Xjgl";
}
