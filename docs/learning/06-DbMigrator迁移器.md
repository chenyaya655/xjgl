# 06 DbMigrator：迁移器的职责与幂等

> **对应阶段**：P0 ｜ **预计耗时**：1 小时 ｜ **产出**：一条命令完成"建表 + 种子数据"，可重复执行

---

## 6.1 要解决的问题

数据库结构变更与种子数据初始化，有三个绕不开的要求：

1. **顺序**：ABP 框架表（EF Core）必须先建，业务表（SqlSugar）依赖它
   （例如 `sys_user_predefine.activated_user_id` 指向 `AbpUsers`）；
2. **幂等**：部署脚本可能被重复执行（发版重试、回滚后再发），
   第二次执行不能报错、不能产生重复数据；
3. **不能由 Web 进程做**：多实例部署时三个实例同时启动、同时建表，必然打架。

**结论：单独做一个控制台程序 `Xjgl.DbMigrator`，部署时先跑它，再起 Web。**

---

## 6.2 动手步骤

### 步骤 1：定义"可排序的迁移器"抽象

不要直接在 DbMigrator 里 `new XjglDbContext().Database.Migrate()` ——
那样 SqlSugar 的表就没机会建。抽象一层，让每种 ORM 自己实现：

```csharp
// src/Xjgl.Domain/Data/IXjglDbSchemaMigrator.cs
namespace Xjgl.Data;

/// <summary>
/// 数据库结构迁移器。
///
/// 为什么要抽象 + 排序：本项目有两套建表机制 ——
/// ABP 框架表用 EF Core 迁移，业务表用 SqlSugar CodeFirst。
/// 它们的执行顺序有依赖（业务表的外键/冗余字段引用框架表），
/// 靠 Order 明确排定，而不是靠"注册顺序"这种隐式约定。
/// </summary>
public interface IXjglDbSchemaMigrator
{
    /// <summary>执行顺序，小的先跑。</summary>
    int Order { get; }

    Task MigrateAsync();
}
```

### 步骤 2：两个实现

**EF Core 侧**（`src/Xjgl.EntityFrameworkCore/EntityFrameworkCore/`）：

```csharp
public class EntityFrameworkCoreXjglDbSchemaMigrator : IXjglDbSchemaMigrator, ITransientDependency
{
    private readonly IServiceProvider _serviceProvider;

    public EntityFrameworkCoreXjglDbSchemaMigrator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    // 框架表先建，业务表可能要引用它
    public int Order => 10;

    public async Task MigrateAsync()
    {
        // 从根容器解析（迁移器可能运行在作用域之外）
        await _serviceProvider
            .GetRequiredService<XjglDbContext>()
            .Database
            .MigrateAsync();
    }
}
```

**SqlSugar 侧**（`src/Xjgl.SqlSugarCore/`）：

```csharp
public class SqlSugarXjglDbSchemaMigrator : IXjglDbSchemaMigrator, ITransientDependency
{
    private readonly ISqlSugarClientFactory _factory;
    private readonly ILogger<SqlSugarXjglDbSchemaMigrator> _logger;

    public int Order => 20;

    public Task MigrateAsync()
    {
        var client = _factory.Create();

        // 找出所有业务实体：实现了 IXjglBusinessEntity 标记接口的类型
        var entityTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a.FullName!.StartsWith("Xjgl"))
            .SelectMany(SafeGetTypes)
            .Where(t => typeof(IXjglBusinessEntity).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false })
            .Distinct()
            .ToArray();

        if (entityTypes.Length == 0)
        {
            _logger.LogInformation("没有发现业务实体（IXjglBusinessEntity），跳过业务表建表。");
            return Task.CompletedTask;
        }

        _logger.LogInformation("发现 {Count} 个业务实体，开始 CodeFirst 建表 ...", entityTypes.Length);

        // InitTables 幂等：表不存在则建表，字段缺失则补字段
        client.CodeFirst.InitTables(entityTypes);

        _logger.LogInformation("业务表建表完成。");
        return Task.CompletedTask;
    }
}
```

**`IXjglBusinessEntity` 这个标记接口是整套机制的关键**：

```csharp
// src/Xjgl.Domain.Shared/Entities/IXjglBusinessEntity.cs
namespace Xjgl.Entities;

/// <summary>
/// 业务表标记。
///
/// 为什么需要它：SqlSugar 的 CodeFirst 需要一份"要建哪些表"的清单。
/// 让实体继承某个基类（如 FullAuditedAggregateRoot）不够 ——
/// 框架自己的实体也继承它。用一个空标记接口，语义最清楚：
/// **实现了它 = 这张表归 SqlSugar 管**。
/// </summary>
public interface IXjglBusinessEntity
{
}
```

于是每个业务实体长这样：

```csharp
public class StuStudent : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    ...
}
```

> **新增业务表时你只需要做一件事**：给实体加 `IXjglBusinessEntity`，
> 然后跑 DbMigrator。不用写迁移文件、不用改任何配置。

### 步骤 3：迁移服务（编排顺序 + 种子数据）

```csharp
// src/Xjgl.Domain/Data/XjglDbMigrationService.cs
public class XjglDbMigrationService : ITransientDependency
{
    private readonly IEnumerable<IXjglDbSchemaMigrator> _migrators;
    private readonly IDataSeeder _dataSeeder;
    private readonly ILogger<XjglDbMigrationService> _logger;

    public async Task MigrateAsync()
    {
        _logger.LogInformation("========== 开始数据库迁移 ==========");

        // 1) 按 Order 依次执行结构迁移
        foreach (var migrator in _migrators.OrderBy(m => m.Order))
        {
            _logger.LogInformation("执行迁移器：{Name}（Order={Order}）",
                migrator.GetType().Name, migrator.Order);

            await migrator.MigrateAsync();
        }

        // 2) 种子数据（ABP 的 IDataSeeder 会调用所有 IDataSeedContributor）
        _logger.LogInformation("---------- 开始初始化种子数据 ----------");
        await _dataSeeder.SeedAsync();

        _logger.LogInformation("========== 数据库迁移与种子数据初始化完成 ==========");
    }
}
```

### 步骤 4：控制台入口

```csharp
// src/Xjgl.DbMigrator/Program.cs
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Async(c => c.Console())
            .CreateLogger();

        try
        {
            Log.Information("========== 学籍管理系统 数据库迁移工具 ==========");

            await Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHostedService<DbMigratorHostedService>();
                })
                .RunConsoleAsync();

            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "数据库迁移失败！");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
```

`DbMigratorHostedService` 只在 `StartAsync` 里跑一次，跑完就 `StopApplication`：

```csharp
public async Task StartAsync(CancellationToken cancellationToken)
{
    using var application = await AbpApplicationFactory.CreateAsync<XjglDbMigratorModule>(options =>
    {
        options.Services.ReplaceConfiguration(_configuration);
        options.UseAutofac();
        options.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSerilog(Log.Logger, dispose: false);
        });
    });

    await application.InitializeAsync();
    await application.ServiceProvider.GetRequiredService<XjglDbMigrationService>().MigrateAsync();
    await application.ShutdownAsync();

    _hostApplicationLifetime.StopApplication();
}
```

---

## 6.3 验证方法

**① 首次执行：建表 + 种子**

```powershell
cd src\Xjgl.DbMigrator
dotnet run
```

期望日志（顺序很重要）：

```
[INF] ========== 学籍管理系统 数据库迁移工具 ==========
[INF] Initialized all ABP modules.
[INF] ========== 开始数据库迁移 ==========
[INF] 执行迁移器：EntityFrameworkCoreXjglDbSchemaMigrator（Order=10）
[INF] 执行迁移器：SqlSugarXjglDbSchemaMigrator（Order=20）
[INF] 发现 18 个业务实体，开始 CodeFirst 建表 ...
[INF] 业务表建表完成。
[INF] ---------- 开始初始化种子数据 ----------
[INF] [模块元数据] 当前共有 4 个子系统、11 个模块、84 个操作。
[INF] [模块元数据] 已确保 admin 角色拥有全部 123 个模块权限。
[INF] [码表种子] 完成：码类别 132 个，码项 327 条
[INF] ========== 数据库迁移与种子数据初始化完成 ==========
```

**② 幂等验证（本章最重要的一条）**

**连续跑两次，对比两次输出的计数 —— 必须完全一致。**

```powershell
dotnet run 2>&1 | Select-String "完成：码类别|当前共有"
dotnet run 2>&1 | Select-String "完成：码类别|当前共有"
```

期望两次输出**逐字相同**，例如都是 `码类别 132 个，码项 327 条`。

如果第二次的数字变大了，说明种子代码不幂等 —— 这是必须在开发期发现的问题，
否则每次发版都会往生产库里灌重复数据。

> **我实际踩过的坑**：观察数字变化时，先确认中间没有跑过集成测试。
> 集成测试会往**同一个库**里写测试数据（创建组织、码表等），
> 数字变化可能是测试残留而不是种子不幂等。判断方法很简单：
> **中间不做任何事，连跑两次**。

**③ 表结构正确**

```powershell
docker exec -it xjgl-postgres psql -U xjgl -d xjgl -c "\dt stu_*"
docker exec -it xjgl-postgres psql -U xjgl -d xjgl -c "\d stu_student"
```

重点看 `stu_student`：

- 列名是 snake_case（`student_no`、`id_card_encrypted`）；
- `is_deleted`、`creation_time`、`creator_id` 这些审计列都在；
- `name` 列的类型是 `varchar(256)`，`remark` 是 `text`。

---

## 6.4 踩坑记录

### 坑 1：Web 进程里顺手做种子，导致多实例打架

早期我把种子放在 `XjglHttpApiHostModule.OnPostApplicationInitialization` 里，
单机开发没问题。但部署两个实例后，两个进程同时插同一批数据，
`sys_subsystem.code` 的唯一索引直接冲突，启动失败。

**结论**：种子只在 DbMigrator 里做。

### 坑 2：全新数据库首次执行时 admin 拿不到任何业务权限

这是本项目一个很隐蔽的问题，值得单独说。

ABP 的 Identity 种子（`IdentityDataSeeder`）会在启动时把**"应用启动那一刻已定义"的权限**
授予 admin 角色。而本项目的业务权限是**动态的** —— 从 `sys_module_action` 表读出来的。

于是全新数据库首次跑 DbMigrator 时：

1. 框架表的种子先跑 → 此时元数据表还没建 → 动态权限快照是**空的** →
   admin 只拿到 ABP 自带的少数权限；
2. 业务元数据种子后跑 → 权限定义才出现，但**授权已经做完了**。

结果：管理员登录后看不到任何业务菜单，报 403。

**修复**：在模块元数据种子写完权限之后，**显式**给 admin 补授一次：

```csharp
private async Task GrantAllModulePermissionsToAdminAsync()
{
    var modules = await _modules.GetListAsync();
    var actions = await _actions.GetListAsync();

    var permissionNames = modules.Select(m => m.PermissionName)
        .Concat(actions.Select(a => a.PermissionName))
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    // IPermissionDataSeeder 内部对已存在的授权不会重复写，所以天然幂等
    await _permissionDataSeeder.SeedAsync(
        RolePermissionValueProvider.ProviderName,
        "admin",
        permissionNames);
}
```

**验证方法**（一定要做一次）：用一个**全新的数据库名**跑一遍
（不要用开发库，那里已经有授权数据了），然后登录 admin，
确认能看到全部业务菜单。

### 坑 3：`MigrateAsync` 与 `EnsureCreated` 混用

`EnsureCreated()` 不会写 `__EFMigrationsHistory`，
之后再想用迁移就会报"表已存在"。**从一开始就用 `Migrate()`**，
不要图省事用 `EnsureCreated`。

### 坑 4：业务表建表报"类型不存在"

如果 `EntityFrameworkCoreXjglDbSchemaMigrator` 的 `Order` 被改成比 SqlSugar 大，
业务表先建，而它引用的框架表还不存在 ——
SqlSugar 建外键时会失败。**保持 EF=10、SqlSugar=20 的顺序**。

---

## 6.5 本章产出

| 项 | 值 |
|---|---|
| 抽象 | `IXjglDbSchemaMigrator`（带 `Order`） |
| 实现 | EF Core（Order=10）、SqlSugar CodeFirst（Order=20） |
| 标记接口 | `IXjglBusinessEntity` —— 加在实体上即自动建表 |
| 幂等 | 连跑两次，计数逐字一致 |
| 命令 | `dotnet run --project src\Xjgl.DbMigrator` |

---

**上一章：** [05 EF Core 接 PostgreSQL](05-EFCore接PostgreSQL.md) ｜ **下一章：** [07 SqlSugar 集成层（一）](07-SqlSugar集成层-客户端工厂与命名约定.md)
