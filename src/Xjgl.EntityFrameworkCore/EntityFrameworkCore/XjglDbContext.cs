using Microsoft.EntityFrameworkCore;
using Volo.Abp.AuditLogging.EntityFrameworkCore;
using Volo.Abp.BackgroundJobs.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.FeatureManagement.EntityFrameworkCore;
using Volo.Abp.Identity;
using Volo.Abp.Identity.EntityFrameworkCore;
using Volo.Abp.OpenIddict.EntityFrameworkCore;
using Volo.Abp.PermissionManagement.EntityFrameworkCore;
using Volo.Abp.SettingManagement.EntityFrameworkCore;
using Volo.Abp.TenantManagement;
using Volo.Abp.TenantManagement.EntityFrameworkCore;

namespace Xjgl.EntityFrameworkCore;

/// <summary>
/// ABP 框架模块的数据库上下文。
///
/// 重要：本上下文只承载框架表（Identity / OpenIddict / 权限 / 设置 / 审计 / 后台作业 / 租户），
/// 业务表（sys_ / stu_ 前缀）由 SqlSugar CodeFirst 负责，不放进这里。
/// 这样两套 ORM 的职责边界清晰，互不干扰。
/// </summary>
[ReplaceDbContext(typeof(IIdentityDbContext))]
[ReplaceDbContext(typeof(ITenantManagementDbContext))]
[ConnectionStringName("Default")]
public class XjglDbContext :
    AbpDbContext<XjglDbContext>,
    IIdentityDbContext,
    ITenantManagementDbContext
{
    #region 来自 ABP 模块的实体

    // Identity
    public DbSet<IdentityUser> Users { get; set; } = default!;
    public DbSet<IdentityRole> Roles { get; set; } = default!;
    public DbSet<IdentityClaimType> ClaimTypes { get; set; } = default!;
    public DbSet<OrganizationUnit> OrganizationUnits { get; set; } = default!;
    public DbSet<IdentitySecurityLog> SecurityLogs { get; set; } = default!;
    public DbSet<IdentityLinkUser> LinkUsers { get; set; } = default!;
    public DbSet<IdentityUserDelegation> UserDelegations { get; set; } = default!;
    public DbSet<IdentitySession> Sessions { get; set; } = default!;

    // Tenant Management（本系统关闭多租户，但保留模块与表，避免改动框架装配）
    public DbSet<Tenant> Tenants { get; set; } = default!;
    public DbSet<TenantConnectionString> TenantConnectionStrings { get; set; } = default!;

    #endregion

    public XjglDbContext(DbContextOptions<XjglDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // 把 ABP 各框架模块的表结构注册进来
        builder.ConfigurePermissionManagement();
        builder.ConfigureSettingManagement();
        builder.ConfigureBackgroundJobs();
        builder.ConfigureAuditLogging();
        builder.ConfigureIdentity();
        builder.ConfigureOpenIddict();
        builder.ConfigureFeatureManagement();
        builder.ConfigureTenantManagement();

        // 业务表不在这里配置，见 Xjgl.SqlSugarCore
    }
}
