# 05 EF Core 接 PostgreSQL：先让 ABP 框架表跑起来

> **对应阶段**：P0 ｜ **预计耗时**：1.5 小时 ｜ **产出**：ABP 自带的身份/权限/审计/OpenIddict 表在 PostgreSQL 里建好

---

## 5.1 要解决的问题

ABP 不是"一个空框架" —— 它自带一批开箱即用的模块，每个模块都有自己的表：

| 模块 | 表 | 用途 |
|---|---|---|
| Identity | `AbpUsers`、`AbpRoles`、`AbpUserRoles`… | 用户、角色 |
| PermissionManagement | `AbpPermissionGrants` | 谁有什么权限 |
| SettingManagement | `AbpSettings` | 系统设置 |
| AuditLogging | `AbpAuditLogs` | 操作审计 |
| OpenIddict | `OpenIddictApplications`、`OpenIddictTokens`… | 登录令牌 |
| BackgroundJobs | `AbpBackgroundJobs` | 后台任务队列 |

**这些表由 EF Core 管理，不走 SqlSugar。** 为什么？

1. 这些模块的仓储、查询、迁移全都是 ABP 写好的 EF Core 实现，改成 SqlSugar 等于重写它们；
2. 它们的数据量小、结构稳定、几乎不需要复杂 SQL —— 用不上 SqlSugar 的优势；
3. 混用两种 ORM 的代价集中在一个明确边界上（框架表 vs 业务表），比"全都混着来"可控得多。

**业务表**（学生、异动、照片、码表…）才用 SqlSugar，从第 07 章开始。

---

## 5.2 动手步骤

### 步骤 1：加包

`src/Xjgl.EntityFrameworkCore/Xjgl.EntityFrameworkCore.csproj`：

```xml
<ItemGroup>
  <PackageReference Include="Volo.Abp.EntityFrameworkCore.PostgreSql" Version="$(AbpVersion)" />
  <PackageReference Include="Volo.Abp.Identity.EntityFrameworkCore" Version="$(AbpVersion)" />
  <PackageReference Include="Volo.Abp.PermissionManagement.EntityFrameworkCore" Version="$(AbpVersion)" />
  <PackageReference Include="Volo.Abp.SettingManagement.EntityFrameworkCore" Version="$(AbpVersion)" />
  <PackageReference Include="Volo.Abp.FeatureManagement.EntityFrameworkCore" Version="$(AbpVersion)" />
  <PackageReference Include="Volo.Abp.AuditLogging.EntityFrameworkCore" Version="$(AbpVersion)" />
  <PackageReference Include="Volo.Abp.BackgroundJobs.EntityFrameworkCore" Version="$(AbpVersion)" />
  <PackageReference Include="Volo.Abp.TenantManagement.EntityFrameworkCore" Version="$(AbpVersion)" />
  <PackageReference Include="Volo.Abp.OpenIddict.EntityFrameworkCore" Version="$(AbpVersion)" />

  <!-- 迁移工具；PrivateAssets=all 表示不传递给引用方 -->
  <PackageReference Include="Microsoft.EntityFrameworkCore.Tools" Version="$(DotNetExtVersion)">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

### 步骤 2：写 DbContext

`src/Xjgl.EntityFrameworkCore/EntityFrameworkCore/XjglDbContext.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.Modeling;
using Volo.Abp.Identity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.SettingManagement;
using Volo.Abp.AuditLogging;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.TenantManagement;
using Volo.Abp.OpenIddict.Applications;
using Volo.Abp.OpenIddict.Authorizations;
using Volo.Abp.OpenIddict.Scopes;
using Volo.Abp.OpenIddict.Tokens;

namespace Xjgl.EntityFrameworkCore;

/// <summary>
/// ABP 框架表的 DbContext（**只放框架表，业务表由 SqlSugar 管**）。
///
/// 这里刻意不把 StuStudent / StuPhoto 之类的业务实体加进来：
/// 两种 ORM 管同一批表，迟早会出现"EF 的变更追踪与 SqlSugar 的更新互相覆盖"。
/// 边界划清楚：框架表归 EF，业务表归 SqlSugar。
/// </summary>
[ConnectionStringName("Default")]
public class XjglDbContext : AbpDbContext<XjglDbContext>
{
    public DbSet<IdentityUser> Users { get; set; }
    public DbSet<IdentityRole> Roles { get; set; }
    public DbSet<IdentityClaimType> ClaimTypes { get; set; }
    public DbSet<OrganizationUnit> OrganizationUnits { get; set; }
    public DbSet<IdentitySecurityLog> SecurityLogs { get; set; }
    public DbSet<IdentityLinkUser> LinkUsers { get; set; }
    public DbSet<IdentityUserDelegation> UserDelegations { get; set; }
    public DbSet<IdentitySession> Sessions { get; set; }

    public DbSet<PermissionGroupDefinitionRecord> PermissionGroups { get; set; }
    public DbSet<PermissionDefinitionRecord> Permissions { get; set; }
    public DbSet<PermissionGrant> PermissionGrants { get; set; }

    public DbSet<Setting> Settings { get; set; }
    public DbSet<FeatureValue> FeatureValues { get; set; }

    public DbSet<AuditLog> AuditLogs { get; set; }
    public DbSet<BackgroundJobRecord> BackgroundJobs { get; set; }

    public DbSet<Tenant> Tenants { get; set; }
    public DbSet<TenantConnectionString> TenantConnectionStrings { get; set; }

    public DbSet<OpenIddictApplication> OpenIddictApplications { get; set; }
    public DbSet<OpenIddictAuthorization> OpenIddictAuthorizations { get; set; }
    public DbSet<OpenIddictScope> OpenIddictScopes { get; set; }
    public DbSet<OpenIddictToken> OpenIddictTokens { get; set; }

    public XjglDbContext(DbContextOptions<XjglDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // 每个实体都必须 ConfigureByConvention()，
        // 否则 ABP 的约定（表名前缀、审计字段、并发戳、ExtraProperties）不会生效，
        // 表现为表建出来了但字段缺失、插入时报 not-null 违例。
        builder.Entity<IdentityUser>(b => { b.ToTable("AbpUsers"); b.ConfigureByConvention(); });
        builder.Entity<IdentityRole>(b => { b.ToTable("AbpRoles"); b.ConfigureByConvention(); });
        builder.Entity<IdentityClaimType>(b => { b.ToTable("AbpClaimTypes"); b.ConfigureByConvention(); });
        builder.Entity<OrganizationUnit>(b => { b.ToTable("AbpOrganizationUnits"); b.ConfigureByConvention(); });
        builder.Entity<IdentitySecurityLog>(b => { b.ToTable("AbpSecurityLogs"); b.ConfigureByConvention(); });
        builder.Entity<IdentityLinkUser>(b => { b.ToTable("AbpLinkUsers"); b.ConfigureByConvention(); });
        builder.Entity<IdentityUserDelegation>(b => { b.ToTable("AbpUserDelegations"); b.ConfigureByConvention(); });
        builder.Entity<IdentitySession>(b => { b.ToTable("AbpSessions"); b.ConfigureByConvention(); });

        builder.Entity<PermissionGroupDefinitionRecord>(b => { b.ToTable("AbpPermissionGroups"); b.ConfigureByConvention(); });
        builder.Entity<PermissionDefinitionRecord>(b => { b.ToTable("AbpPermissions"); b.ConfigureByConvention(); });
        builder.Entity<PermissionGrant>(b => { b.ToTable("AbpPermissionGrants"); b.ConfigureByConvention(); });

        builder.Entity<Setting>(b => { b.ToTable("AbpSettings"); b.ConfigureByConvention(); });
        builder.Entity<FeatureValue>(b => { b.ToTable("AbpFeatureValues"); b.ConfigureByConvention(); });

        builder.Entity<AuditLog>(b => { b.ToTable("AbpAuditLogs"); b.ConfigureByConvention(); });
        builder.Entity<BackgroundJobRecord>(b => { b.ToTable("AbpBackgroundJobs"); b.ConfigureByConvention(); });

        builder.Entity<Tenant>(b => { b.ToTable("AbpTenants"); b.ConfigureByConvention(); });
        builder.Entity<TenantConnectionString>(b => { b.ToTable("AbpTenantConnectionStrings"); b.ConfigureByConvention(); });

        builder.Entity<OpenIddictApplication>(b => { b.ToTable("OpenIddictApplications"); b.ConfigureByConvention(); });
        builder.Entity<OpenIddictAuthorization>(b => { b.ToTable("OpenIddictAuthorizations"); b.ConfigureByConvention(); });
        builder.Entity<OpenIddictScope>(b => { b.ToTable("OpenIddictScopes"); b.ConfigureByConvention(); });
        builder.Entity<OpenIddictToken>(b => { b.ToTable("OpenIddictTokens"); b.ConfigureByConvention(); });

        builder.ConfigureIdentity();
        builder.ConfigurePermissionManagement();
        builder.ConfigureSettingManagement();
        builder.ConfigureFeatureManagement();
        builder.ConfigureAuditLogging();
        builder.ConfigureBackgroundJobs();
        builder.ConfigureTenantManagement();
        builder.ConfigureOpenIddict();
    }
}
```

### 步骤 3：写设计时工厂（供 `dotnet ef` 使用）

```csharp
// XjglDbContextFactory.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Xjgl.EntityFrameworkCore;

/// <summary>
/// 设计时工厂：`dotnet ef migrations add` 时 EF 工具会用它创建 DbContext。
///
/// 为什么不能靠运行期的 DI：迁移命令不启动应用，
/// 因此拿不到 appsettings 与 DI 容器，必须在这里自己读配置。
/// </summary>
public class XjglDbContextFactory : IDesignTimeDbContextFactory<XjglDbContext>
{
    public XjglDbContext CreateDbContext(string[] args)
    {
        var configuration = BuildConfiguration();

        var builder = new DbContextOptionsBuilder<XjglDbContext>()
            .UseNpgsql(configuration.GetConnectionString("Default"));

        return new XjglDbContext(builder.Options);
    }

    private static IConfigurationRoot BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "../Xjgl.HttpApi.Host/"))
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }
}
```

### 步骤 4：写模块

```csharp
// XjglEntityFrameworkCoreModule.cs
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.PostgreSql;
using Volo.Abp.Modularity;

namespace Xjgl.EntityFrameworkCore;

[DependsOn(
    typeof(XjglDomainModule),
    typeof(AbpEntityFrameworkCorePostgreSqlModule)
)]
public class XjglEntityFrameworkCoreModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // 注册 DbContext 及其默认仓储（IdentityUserRepository 等）
        context.Services.AddAbpDbContext<XjglDbContext>(options =>
        {
            options.AddDefaultRepositories(includeAllEntities: true);
        });

        Configure<AbpDbContextOptions>(options =>
        {
            options.Configure(ctx =>
            {
                ctx.DbContextOptions.UseNpgsql(npgsql =>
                {
                    // 学籍系统是内网政务系统，网络抖动比互联网环境更常见，
                    // 开启瞬时故障重试能挡掉一部分"偶发 500"
                    npgsql.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(3),
                        errorCodesToAdd: null);

                    npgsql.CommandTimeout(60);
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory");
                });
            });
        });
    }
}
```

### 步骤 5：配置连接串

`src/Xjgl.HttpApi.Host/appsettings.json`：

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=xjgl;User ID=xjgl;Password=Xjgl@Dev2026;Timeout=30;Command Timeout=60"
  }
}
```

> **注意 `Host=localhost` 就够** —— 第 01 章讲过，WSL2 会把容器端口转发到 Windows 的 localhost。
> 记得把密码放进 `appsettings.Secrets.json` 并在生产环境改用环境变量，
> 不要把生产密码提交进 Git。

### 步骤 6：生成迁移

```powershell
cd src\Xjgl.EntityFrameworkCore
dotnet ef migrations add Initial -o Migrations
```

---

## 5.3 验证方法

**① 迁移文件生成了**

```powershell
Get-ChildItem src\Xjgl.EntityFrameworkCore\Migrations
# 期望：<时间戳>_Initial.cs、<时间戳>_Initial.Designer.cs、XjglDbContextModelSnapshot.cs
```

打开 `_Initial.cs`，确认里面有 `CreateTable(name: "AbpUsers"`、
`"AbpPermissionGrants"`、`"OpenIddictApplications"` 等。

**② 迁移能真的执行**

```powershell
cd src\Xjgl.DbMigrator
dotnet run
```

日志里应该出现：

```
[INF] 执行迁移器：EntityFrameworkCoreXjglDbSchemaMigrator（Order=10）
```

**③ 表真的建出来了**

```powershell
docker exec -it xjgl-postgres psql -U xjgl -d xjgl -c "\dt" | Select-String "Abp"
```

期望看到 `AbpUsers`、`AbpRoles`、`AbpPermissionGrants`、`AbpAuditLogs`、
`AbpSettings`、`OpenIddictApplications` 等。

**④ 迁移历史表有记录（说明不是手工建的表）**

```powershell
docker exec -it xjgl-postgres psql -U xjgl -d xjgl `
  -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory";'
```

---

## 5.4 踩坑记录

### 坑 1：忘记 `ConfigureByConvention()`

漏掉它，表还是能建出来，但**审计字段、并发戳、`ExtraProperties` 全都没有**。
表现是插入时报 `null value in column "ExtraProperties" violates not-null constraint`
或并发更新时静默覆盖别人的修改。

**排查技巧**：对比 EF 生成的建表 SQL 与 ABP 文档里的标准表结构，
看是否缺 `ExtraProperties`、`ConcurrencyStamp`、`CreationTime`。

### 坑 2：设计时工厂的 BasePath 找不到 appsettings

`SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "../Xjgl.HttpApi.Host/"))`
是相对于**执行 `dotnet ef` 命令时的当前目录**。
所以必须在 `src\Xjgl.EntityFrameworkCore` 目录下执行，
否则报 `The configuration file 'appsettings.json' was not found`。

更稳的写法是用程序集位置推导：

```csharp
.SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "../../../src/Xjgl.HttpApi.Host"))
```

或者干脆在工厂里硬编码一个开发用连接串（**只用于生成迁移文件，不用于运行**）。

### 坑 3：把业务实体也加进 DbContext

一旦把 `StuStudent` 加进来，EF 就会为它建表 —— 而 SqlSugar 的 CodeFirst 也会建 ——
两张表名一样，谁先建谁赢，字段还不一样。**严格分开：这个 DbContext 只放框架表。**

### 坑 4：`AbpUsers` 与 PostgreSQL 保留字

ABP 的表名是 PascalCase（`AbpUsers`），在 PostgreSQL 里需要用双引号引用。
EF Core 会自动处理，但你在 Adminer 里手写 SQL 时必须写 `SELECT * FROM "AbpUsers"`，
写 `SELECT * FROM AbpUsers` 会报 `relation "abpusers" does not exist`（被转成小写了）。

### 坑 5：改了实体但忘记加迁移

开发期最常见的"我明明改了代码怎么没生效"。
养成习惯：改完 `XjglDbContext` 或任何框架实体后立刻

```powershell
dotnet ef migrations add <描述性的名字> -o Migrations
```

并在提交前确认 `Migrations/` 目录里有新文件。

---

## 5.5 本章产出

| 项 | 值 |
|---|---|
| 文件 | `XjglDbContext.cs`、`XjglDbContextFactory.cs`、`XjglEntityFrameworkCoreModule.cs` |
| 迁移 | `Migrations/<时间戳>_Initial.cs` |
| 建出的表 | 约 20 张 `Abp*` / `OpenIddict*` 表 |
| 边界 | **框架表归 EF Core，业务表归 SqlSugar**（第 07 章开始） |

---

**上一章：** [04 九个项目怎么分层](04-分层与每层放什么.md) ｜ **下一章：** [06 DbMigrator](06-DbMigrator迁移器.md)
