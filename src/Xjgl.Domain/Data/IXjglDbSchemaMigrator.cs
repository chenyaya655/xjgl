namespace Xjgl.Data;

/// <summary>
/// 数据库结构迁移器。
/// 本系统有两套建表来源，靠 <see cref="Order"/> 保证执行顺序：
///   1) EF Core 迁移 —— 建 ABP 框架表（Identity / OpenIddict / 权限 / 审计 ...）
///   2) SqlSugar CodeFirst —— 建业务表（sys_ / stu_ 前缀）
/// </summary>
public interface IXjglDbSchemaMigrator
{
    /// <summary>执行顺序，数值小的先执行。</summary>
    int Order { get; }

    Task MigrateAsync();
}
