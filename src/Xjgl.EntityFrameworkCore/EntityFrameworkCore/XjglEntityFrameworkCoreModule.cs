using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.DistributedEvents;
using Volo.Abp.EntityFrameworkCore.PostgreSql;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;
using Volo.Abp.AuditLogging.EntityFrameworkCore;
using Volo.Abp.BackgroundJobs.EntityFrameworkCore;
using Volo.Abp.FeatureManagement.EntityFrameworkCore;
using Volo.Abp.Identity.EntityFrameworkCore;
using Volo.Abp.OpenIddict.EntityFrameworkCore;
using Volo.Abp.PermissionManagement.EntityFrameworkCore;
using Volo.Abp.SettingManagement.EntityFrameworkCore;
using Volo.Abp.TenantManagement.EntityFrameworkCore;

namespace Xjgl.EntityFrameworkCore;

[DependsOn(
    typeof(XjglDomainModule),
    typeof(AbpEntityFrameworkCorePostgreSqlModule),
    typeof(AbpIdentityEntityFrameworkCoreModule),
    typeof(AbpOpenIddictEntityFrameworkCoreModule),
    typeof(AbpPermissionManagementEntityFrameworkCoreModule),
    typeof(AbpSettingManagementEntityFrameworkCoreModule),
    typeof(AbpFeatureManagementEntityFrameworkCoreModule),
    typeof(AbpAuditLoggingEntityFrameworkCoreModule),
    typeof(AbpBackgroundJobsEntityFrameworkCoreModule),
    typeof(AbpTenantManagementEntityFrameworkCoreModule)
)]
public class XjglEntityFrameworkCoreModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        // Npgsql 6+ 的时间戳行为兼容开关（ABP 官方模板同样设置）
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<XjglDbContext>(options =>
        {
            options.AddDefaultRepositories(includeAllEntities: true);
        });

        Configure<AbpDbContextOptions>(options =>
        {
            // 框架表的数据库提供者：PostgreSQL
            options.UseNpgsql();
        });

        // ── 事务性发件箱 / 收件箱（债 4）──
        //
        // 【为什么配在这里，而不是 Host 的 ConfigureEventBus】
        // 这是**数据访问层**的能力（它要 `UseDbContext<XjglDbContext>()` 来落库），
        // 而且集成测试也必须走到它 —— 只在 Host 配的话，
        // "事件真的落进 AbpEventOutbox 了吗"这件事在测试里根本验不到，
        // 只能靠"上线后观察"，而那正是本仓库反复强调要避免的验证方式。
        //
        // 【它解决的是哪一个缺陷】
        // 不开 Outbox 时，`PublishAsync` 是**在工作单元提交阶段直接发到 broker**；
        // broker 不可达 → 提交失败 → 事务回滚 —— 而 HTTP 响应早已写出，
        // 客户端只看到"成功"，数据却没了（建用户静默丢失，有完整实测证据）。
        // 开启后事件先落库（同事务），由 ABP 自带的 `OutboxSender` 后台重发：
        // **broker 不可达不再影响业务写入**。
        Configure<AbpDistributedEventBusOptions>(options =>
        {
            options.Outboxes.Configure(box => box.UseDbContext<XjglDbContext>());
            options.Inboxes.Configure(box => box.UseDbContext<XjglDbContext>());
        });
    }
}
