using Microsoft.EntityFrameworkCore;
using Volo.Abp.AuditLogging.EntityFrameworkCore;
using Volo.Abp.BackgroundJobs.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.DistributedEvents;
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
///
/// 【为什么还实现了 <see cref="IHasEventOutbox"/> / <see cref="IHasEventInbox"/>（债 4）】
/// 在做这层改造之前，接入 RabbitMQ 会造成**静默数据丢失**（有完整实测证据，见
/// <c>XjglHttpApiHostModule.ConfigureEventBus</c> 的注释）：
/// ABP 的工作单元在**响应写出之后**才提交，而身份模块的写操作会发布分布式事件；
/// MQ 发布失败 → 工作单元提交失败 → 事务回滚 —— 但 HTTP 响应早已发出，
/// 客户端只看到"成功"，库里却没有那条数据。
///
/// 开启 Outbox 之后，事件的落库与业务数据在**同一个 EF Core 事务**里完成，
/// 再由后台 worker 重发到 broker。于是 broker 不可达只意味着"事件稍后重发"，
/// **不再影响业务写入** —— 这正是重新接入 MQ 的前置条件。
/// </summary>
[ReplaceDbContext(typeof(IIdentityDbContext))]
[ReplaceDbContext(typeof(ITenantManagementDbContext))]
[ConnectionStringName("Default")]
public class XjglDbContext :
    AbpDbContext<XjglDbContext>,
    IIdentityDbContext,
    ITenantManagementDbContext,
    IHasEventOutbox,
    IHasEventInbox
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

    // 事务性发件箱 / 收件箱（债 4）
    public DbSet<OutgoingEventRecord> OutgoingEvents { get; set; } = default!;
    public DbSet<IncomingEventRecord> IncomingEvents { get; set; } = default!;

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

        // 发件箱 / 收件箱表（债 4）
        builder.ConfigureEventOutbox();
        builder.ConfigureEventInbox();

        // 业务表不在这里配置，见 Xjgl.SqlSugarCore
    }
}
