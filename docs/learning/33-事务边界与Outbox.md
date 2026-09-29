# 33 事务边界：多步写怎么做到全成或全败（含 Outbox）

> 对应代码：`src/Xjgl.Domain/Data/IXjglTransactionScope.cs`、
> `src/Xjgl.SqlSugarCore/SqlSugarTransactionScope.cs`、
> `src/Xjgl.Application/Changes/ChangeFlowAppService.cs`（`ApproveAsync`）、
> `src/Xjgl.EntityFrameworkCore/EntityFrameworkCore/XjglDbContext.cs`（Outbox）
> 对应测试：`test/Xjgl.Application.Tests/Changes/ChangeFlowAppServiceTests.cs`（事务三例）、
> `test/Xjgl.Application.Tests/EventBus/EventOutboxTests.cs`

## 33.1 要解决的问题

"审批通过"这一个动作至少要写 **4 张表**：

```
流程任务表（当前节点 → 已通过）
流程日志表（记一条「通过」）
申请单（状态 → 已通过、归档）
学生主档（学籍状态/学校/年级/班级）+ 学籍变更留痕
```

在此之前每一步都是 `autoSave: true` **各自提交**。于是"最后一步之前崩掉"会留下**半成品状态**，
其中最难查的一种是：

> 申请单已是「已通过」、流程也归档了，但**学生的学籍状态没改**。

界面上显示审批完成，学生档案却还是原样，**而且没有任何报错**。

本章有两部分，解决的是**两个不同层次**的原子性：

| 层次 | 问题 | 本项目的做法 |
|---|---|---|
| 业务表之间（SqlSugar） | 上面那段 | `IXjglTransactionScope` 显式事务范围 |
| 业务写 + 事件发布（跨 ORM） | 事件发布了但业务回滚 / 业务成功但事件丢了 | **Outbox**（事务性发件箱） |

## 33.2 第一步：事务抽象上提到领域层

`IXjglTransactionScope` **必须放在 Domain**，而不是实现所在的 `SqlSugarCore`：

```
Xjgl.Domain/Data/IXjglTransactionScope.cs      ← 接口（应用层能引用）
Xjgl.SqlSugarCore/SqlSugarTransactionScope.cs  ← 实现（只有 Host 装配）
```

为什么这是关键：它原先定义在 `Xjgl.SqlSugarCore`，而**应用层不引用那个项目**
（分层约定：Application 只依赖 Domain / Domain.Shared / Application.Contracts）。
结果就是**这个能力存在但没人能用** —— 应用层想给审批加事务时既引用不到接口、
也没别的办法，只能让每一步各自提交。

> **一句话记住**：**能力放在调用方够不着的地方，等于没有这个能力。**

## 33.3 第二步：用显式事务范围包住多步写

```csharp
public async Task<ChangeApplyDto> ApproveAsync(ChangeAuditActionDto input)
{
    ChangeApplyDto result = null!;

    await _tx.ExecuteAsync(async () =>
    {
        result = await ApproveCoreAsync(input);
    });

    return result;
}
```

实现本身很短，但**三个分支都要写对**：

```csharp
public async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
{
    var client = _clientAccessor.Client;

    // 已在事务中（嵌套调用）：由最外层负责提交/回滚。
    // 内层若自己提交，外层的回滚就回不到内层已提交的部分了。
    if (client.Ado.IsAnyTran())
    {
        return await action();
    }

    await client.Ado.BeginTranAsync();
    try
    {
        var result = await action();
        await client.Ado.CommitTranAsync();
        return result;
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "SqlSugar 事务执行失败，正在回滚。");
        if (client.Ado.IsAnyTran()) await client.Ado.RollbackTranAsync();
        throw;
    }
}
```

### 为什么不用 ABP 的 `[UnitOfWork]` 自动事务

**用枚举全部成员的方式核对过**（不是"按名字搜不到就断定没有" —— 本仓库为这种查法栽过）：
`IUnitOfWork` 的公开成员只有

```
Id / IsCompleted / IsDisposed / IsReserved / ReservationName / Items / Options / Outer
CompleteAsync / RollbackAsync / SaveChangesAsync / Initialize / Reserve / 事件
```

**没有 `TransactionApi`，也没有 `ServiceProvider`** —— 第三方 ORM 无法把事务挂到 UoW 的提交链路上。

还有一个被实测否掉的写法："在仓储里 `BeginTran`、在 `uow.OnCompleted` 里 `Commit`"。
它有一个致命的**时序问题**：ABP 的 DataSeeder 会在 UoW 内部再创建一个 DI 子作用域，
SqlSugar 客户端（Scoped）随子作用域**先**被释放，而 `OnCompleted` 要等 UoW 完成才触发 ——
事务在提交前就随连接释放被回滚。表现是：

> 日志说插了 153 条，换个连接查却是 0 条。

## 33.4 第三步：验证"回滚真的生效"（这一步不能省）

**"包了事务"这件事从代码上看不出来**，所以必须有会失败的用例。
只断言"流程正常走完"是不合格的：**没包事务的实现也能通过**。

```csharp
[Fact]
public async Task 事务范围_中途抛异常时前面的写全部回滚()
{
    var marker = "事务回滚测试" + Guid.NewGuid().ToString("N")[..8];

    (await guardians.GetCountAsync(g => g.Name == marker)).ShouldBe(0);

    await Should.ThrowAsync<InvalidOperationException>(async () =>
    {
        await tx.ExecuteAsync(async () =>
        {
            await guardians.InsertAsync(NewGuardian(marker), autoSave: true);

            // ★ 关键中间断言：**事务内确实能查到自己刚写入的数据**
            //   没有它，"回滚后查不到"无法区分"被回滚了"与"压根没写进去"
            (await guardians.GetCountAsync(g => g.Name == marker)).ShouldBe(1);

            throw new InvalidOperationException("故意失败，用来验证回滚");
        });
    });

    (await guardians.GetCountAsync(g => g.Name == marker))
        .ShouldBe(0, "事务回滚后，前半段写入的数据必须查不到 —— 否则事务等于没包");
}
```

三条用例构成完整证据链：

| 用例 | 守住什么 |
|---|---|
| 中途抛异常 → 全部回滚 | 回滚生效 |
| 正常结束 → 数据确实落库 | 不是"永远不提交" |
| **嵌套时内层不提交** | 外层回滚能覆盖内层（批量操作必需） |

> **用什么表做验证也是有讲究的**：我用 `stu_guardian`（必填列只有 `student_id/seq/name`）。
> 一开始用 `sys_org`，结果红在"插入失败"上 —— 因为 `path_code` 是
> **必须由服务层计算的**物化路径、直接构造入库会撞 NOT NULL。
> **用例红在"插不进去"上，就等于没测到事务语义。**

## 33.5 第四步：跨 ORM 的原子性 —— Outbox

上面解决的是**业务表之间**（都在 SqlSugar 一侧）。真正跨 ORM 的是**事件发布**：

`IXjglTransactionScope` 管不到 EF Core 的框架表（Identity / 权限 / 审计），
因为那套走 ABP 的 UoW，而**本项目的 `AbpUnitOfWorkDefaultOptions` 从未配置过
`IsTransactional`** —— 即 EF Core 的 UoW **默认不开事务**、各自 `SaveChanges` 提交。
两套 ORM 用两条连接、两个事务，没有共享机制。

于是有了这个**会造成静默数据丢失**的缺陷（有完整实测证据）：

```
RabbitMqDistributedEventBus.PublishAsync(...)
  → DistributedEventBusBase.PublishAsync(...)
  → UnitOfWorkEventPublisher.PublishDistributedEventsAsync(...)
  → UnitOfWork.CompleteAsync(...)          ← 提交在这里
  → AbpUnitOfWorkMiddleware.InvokeAsync(HttpContext)   ← 响应早就写出去了
```

实测表现：`POST /api/identity/users` 返回 **200 且带完整用户对象**，
但库里查不到那条用户（`AbpUsers` 计数没变），客户端同时收到
`transfer closed with outstanding read data remaining`。

### Outbox 解决它

开启 Outbox 后，分布式事件变成**与业务数据在同一个 EF Core 事务里的一行数据**，
由 ABP 自带的 `OutboxSender` 后台重发。于是 **broker 是否可达，与业务写入是否成功彻底解耦**。

三步配置（缺任何一步都会**静默退回**老行为，不报错）：

```csharp
// ① DbContext 上实现接口 + 加 DbSet
public class XjglDbContext : AbpDbContext<XjglDbContext>, IHasEventOutbox, IHasEventInbox
{
    public DbSet<OutgoingEventRecord> OutgoingEvents { get; set; } = default!;
    public DbSet<IncomingEventRecord> IncomingEvents { get; set; } = default!;
}

// ② OnModelCreating 里注册表结构
builder.ConfigureEventOutbox();
builder.ConfigureEventInbox();

// ③ 告诉 ABP 用哪个 DbContext 落库（放在**共享的 EF 模块**里，
//    这样集成测试也能走到它）
Configure<AbpDistributedEventBusOptions>(options =>
{
    options.Outboxes.Configure(box => box.UseDbContext<XjglDbContext>());
    options.Inboxes.Configure(box => box.UseDbContext<XjglDbContext>());
});
```

然后生成并应用迁移：

```powershell
dotnet ef migrations add AddEventOutboxInbox --project src\Xjgl.EntityFrameworkCore --startup-project src\Xjgl.DbMigrator
# ⚠ 生成前必须先 build，否则迁移是**空的**（我踩过：用了 --no-build，迁移里什么都没有）
```

### 类型的真实位置（两次猜错）

`IHasEventOutbox` / `IHasEventInbox` / `OutgoingEventRecord` / `ConfigureEventOutbox()`
都在 **`Volo.Abp.EntityFrameworkCore`** 的
`Volo.Abp.EntityFrameworkCore.DistributedEvents` 命名空间下。

我先后按命名习惯猜过：

- `Volo.Abp.EventBus.EntityFrameworkCore` → **NuGet 上 404**
- `Volo.Abp.EventBus` → 能引用，但**不含这些类型**

最后靠在 nuget 缓存里**全量搜符号**才定位到。

> **教训**：找类型不要靠包名猜。用能**枚举全部成员**的手段
> （搜程序集里的类型名字符串）—— 这与第 10 章那条"没搜到不等于不存在"是同一个教训的两个方向。

## 33.6 第五步：验证事件真的落库了（并且事务性）

```csharp
[Fact]
public async Task 工作单元内发布的事件_会落进发件箱表()
{
    var marker = "outbox-" + Guid.NewGuid().ToString("N")[..8];

    using (var uow = _uowManager.Begin(requiresNew: true))
    {
        await _eventBus.PublishAsync(new OutboxProbeEto { Marker = marker });
        await uow.CompleteAsync();
    }

    // 直接查 AbpEventOutbox 表 —— 这是"事件真的被持久化了"的唯一硬证据
    (await ReadOutboxPayloadsAsync()).ShouldContain(p => p.Contains(marker));
}

[Fact]
public async Task 工作单元回滚时_事件不会留在发件箱()
{
    // 反向用例：只有"能落库"一条，会让"立刻提交"的实现也通过 ——
    // 那种实现下业务回滚了事件却还在，收件方会收到一条**从未发生**的变更。
}
```

**为什么必须查表、不能只断言"没抛异常"**：
少了 `UseDbContext`、少了 `ConfigureEventOutbox()`、少了迁移，
**三者都会静默退回"直接发 broker"的老行为，且不报任何错**。
我做过变异验证：把 `options.Outboxes.Configure(...)` 注释掉，
"事件落进发件箱"那条立刻变红（另一条回滚用例仍然绿）—— 说明用例真的盯着这件事。

读取时注意 **`EventData` 是 `byte[]`**（事件体按 UTF-8 字节存），
`EventName` 才是字符串：

```csharp
var rows = await db.OutgoingEvents
    .Where(e => e.EventName == "Xjgl.Tests.OutboxProbe")
    .Select(e => e.EventData)      // byte[]
    .ToListAsync();
```

## 33.7 当前状态与**明确未做**的部分

| 项 | 状态 |
|---|---|
| 业务表之间的多步原子性 | ✅ 已实现并验证（三条用例） |
| Outbox / Inbox 表与配置 | ✅ 已接入并验证（含变异测试） |
| RabbitMQ 接线 | ❌ **刻意不做**：Outbox 的**表与分发器已就位**，但 `src/` 里**没有任何生产代码调用 `PublishAsync`** —— 也就是没有"事件源"；而且接线必须现场有可达 broker 才能验证 |
| 跨 ORM（业务写 + 框架表）真原子 | ❌ **未实现** —— 需要让两套 ORM 共享事务，或全面 Outbox 化 |
| 多实例下的 Outbox 并发 | ⚠ 依赖 ABP 的分布式锁；本项目用默认实现（单实例足够） |

不接 MQ 的理由：**没有业务逻辑依赖它**（因此也没有谁往 Outbox 里写事件），
而接线必须现场有可达 broker 才能验证。
**不做"配了但没验过"的接线** —— 那正是本仓库一直在避免的交付方式。

## 33.8 本章要点

1. 事务抽象要放在**应用层够得着**的层（Domain）；放实现所在项目等于没有这个能力。
2. 显式事务范围的三个分支：嵌套不提交、成功提交、失败回滚 —— 缺一个都会留半成品。
3. 验证必须包含**"中途抛异常 → 前面全回滚"**，并且要在事务内断言"确实写进去了"。
4. 选验证用的表要小心：必填列太多会让你红在"插不进去"上，等于没测事务。
5. Outbox 解决的是"业务写 + 事件发布"的原子性；**少任何一步配置都会静默退回老行为**。
6. 找类型/包**不要按命名习惯猜**，要用能枚举全部成员的手段。
7. `dotnet ef migrations add` 前**必须先 build**，否则迁移是空的。

**下一篇：** [37 照片存储抽象：主存储与降级存储](37-照片存储抽象.md)
