# 09 混合 ORM 的代价：两种 ORM 的事务怎么共存

> **对应阶段**：P0 ｜ **预计耗时**：2 小时 ｜ **产出**：一套明确的事务边界约定，以及一个"日志说插了 153 条、库里却是 0 条"的经典事故复盘

---

## 9.1 要解决的问题

本项目同一个数据库里有两套 ORM：

- **EF Core** 管 ABP 框架表（用户、权限、审计、OpenIddict…）；
- **SqlSugar** 管业务表（学生、异动、照片、码表…）。

ABP 的工作单元（UnitOfWork）是为 EF Core 设计的：它在 `SaveChangesAsync` 时
统一提交、统一发领域事件。**SqlSugar 的变更不在它的管辖范围内。**

于是有一个必须回答的问题：**怎么保证一组写操作要么都成功、要么都失败？**

---

## 9.2 我试过的三种写法，以及为什么前两种都不行

### 写法 A：把 SqlSugar 挂到 ABP 的 UoW 上

```csharp
// 想法：仓储里开事务，UoW 完成时提交
public async Task InsertAsync(TEntity entity, ...)
{
    Client.Ado.BeginTran();
    await Client.Insertable(entity).ExecuteCommandAsync();

    var uow = UnitOfWorkManager.Current;
    uow?.OnCompleted(() => Client.Ado.CommitTran());
}
```

**为什么不行**：我先写了个探针程序把 `IUnitOfWork` 的公开成员全打印出来，结果是：

```
OnCompleted, Failed, Disposed, RollbackAsync, CompleteAsync, Items, Outer
```

**没有 `TransactionApi`、也没有 `ServiceProvider`。**
第三方 ORM 拿不到 ABP 的事务对象，也就没法把自己的提交挂到它的提交链路上 ——
"提交时机"只能靠猜。

> 顺带说一句：**遇到"某个框架能不能这样用"这类问题，写个 10 行的探针程序打印公开成员，
> 比读半小时文档快得多，而且不会读错版本。**

### 写法 B：在 `OnCompleted` 里提交（真正的事故）

上面的写法 B 比 A 更"合理"一些 —— 至少提交时机看起来是对的：

```csharp
// 反面教材
uow.OnCompleted(async () => await Client.Ado.CommitTranAsync());
```

现象：**DbMigrator 的日志说"插入 153 条码表项"，换个连接去查，表里是 0 条。**

排查过程（这是整件事最有价值的部分）：

1. 先确认 SQL 真的执行了 —— 打开 `EnableSqlLog`，看到 153 条 `INSERT` 都发出去了；
2. 再确认连接是不是同一个 —— 加日志打印 `Client.ContextID`，发现**插入用的连接与查询用的连接不同**；
3. 关键线索来自 ABP 的一个行为：**`IDataSeeder` 会在工作单元内部再创建一个 DI 子作用域**；
4. 于是时序变成：
   ```
   UoW 开始
     └─ 子作用域创建 → 解析出 SqlSugar 客户端（Scoped）
          └─ 153 条 INSERT（在事务里，未提交）
     └─ 子作用域销毁 → SqlSugar 客户端被 Dispose → 连接关闭
                          ↓
                    未提交的事务被数据库自动回滚
   UoW 完成 → OnCompleted 触发 → 试图 Commit，但连接已经没了
   ```

**"未提交的事务随连接关闭被回滚"是数据库的正常行为**，
所以没有任何异常被抛出 —— 这是它最难查的地方：**静默失败**。

### 写法 C（采用）：显式事务范围

```csharp
await _tx.ExecuteAsync(async () =>
{
    await _students.InsertAsync(student);
    await _changeLogs.InsertAsync(log);
});
```

```
单条写操作      → 直接自动提交（IsAutoCloseConnection=true，执行完即提交）
多步原子写      → 用 ISqlSugarTransactionScope 显式包一层
跨 ORM 的原子写 → 目前做不到，见 9.4
```

---

## 9.3 动手步骤

```csharp
// src/Xjgl.SqlSugarCore/ISqlSugarTransactionScope.cs
public interface ISqlSugarTransactionScope
{
    Task<T> ExecuteAsync<T>(Func<Task<T>> action);
    Task ExecuteAsync(Func<Task> action);
}

public class SqlSugarTransactionScope : ISqlSugarTransactionScope, ITransientDependency
{
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
    {
        var client = _clientAccessor.Client;

        // 嵌套调用：由最外层负责提交/回滚。
        // 若不做这个判断，内层 Commit 会把外层的事务一起提交掉，
        // 外层后续失败时就无法回滚了 —— 这是"部分提交"的经典来源。
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

            if (client.Ado.IsAnyTran())
            {
                await client.Ado.RollbackTranAsync();
            }

            throw;      // 异常必须继续往上抛，否则上层会以为成功了
        }
    }
}
```

**三个细节值得注意：**

1. `IsAnyTran()` 判断嵌套 —— 保证"只有最外层提交"；
2. 回滚前再判一次 `IsAnyTran()` —— 因为异常可能来自"提交本身失败"，此时事务可能已结束；
3. `throw;` 而不是 `throw ex;` —— 保留原始调用栈。

---

## 9.4 代价：跨 ORM 写操作不原子

**这是本项目一个明确的、尚未消除的代价。** 举个真实场景 —— 审核通过一个转学申请：

```csharp
// 现在是这样写的（两步，非原子）
// 1) 先改学生（SqlSugar 业务表）
student.SchoolOrgId = apply.ToSchoolOrgId;
student.StudentStatusCode = StudentStatuses.Studying;
await _students.UpdateAsync(student, autoSave: true);

// 2) 再改申请单状态（也是 SqlSugar，但这里仅示意）
apply.Status = ChangeApplyStatuses.Approved;
await _applies.UpdateAsync(apply, autoSave: true);
```

这两步都在 SqlSugar 里，可以用 `ISqlSugarTransactionScope` 包起来解决。

**但如果是"改学籍（SqlSugar）+ 写 ABP 审计/权限（EF Core）"这种组合，就无法原子。**
当前的应对策略有三条：

| 策略 | 说明 |
|---|---|
| **把业务写放在最后** | 先做可以重试的、幂等的准备工作，最后一步才做不可逆的业务写入。这样"半成品"最少 |
| **让操作幂等** | 审核操作重复执行不会产生两份数据（用状态机挡住：已审核的任务不能再次审核） |
| **记录足够的信息以便人工核对** | 每一步都写业务流水表（`stu_change_flow_log`），出现半成品时能看出来到哪一步了 |

**未来怎么彻底解决**（记在这里，别忘）：
把事务抽象上提到 Domain 层（例如定义 `IXjglTransactionScope` 放在 `Xjgl.Domain/Data/`），
然后做一个"同时接管 EF Core 与 SqlSugar"的实现 ——
两者连的是同一个 PostgreSQL 库，理论上可以用**同一个 `NpgsqlConnection`**
构造两个 ORM 的上下文，从而真正共享一个事务。这需要：
EF Core 侧用 `UseNpgsql(existingConnection)`，SqlSugar 侧用 `Ado.Connection` 注入同一个连接，
并且事务由最外层统一 `Begin/Commit`。工作量约 1~2 天。

---

## 9.5 一个附带的分层问题（已知技术债）

`ISqlSugarTransactionScope` 位于 `Xjgl.SqlSugarCore`，
而 `Xjgl.Application` **不引用** `Xjgl.SqlSugarCore`（第 04 章的铁律 2）。

结果是：**应用服务里用不到事务范围**，只能靠"单条写自动提交"。

这其实是个信号：**事务边界本来就是领域概念，不该由 ORM 决定它住在哪一层。**
正确的位置是 `Xjgl.Domain/Data/IXjglTransactionScope.cs`。
等上面 9.4 的"共享连接"方案落地时会一并调整。

---

## 9.6 验证方法

**① 事务真的能回滚**

写一个临时测试：

```csharp
[Fact]
public async Task 事务范围内抛异常_已插入的数据应被回滚()
{
    var tx = GetRequiredService<ISqlSugarTransactionScope>();
    var repo = GetRequiredService<IXjglRepository<SysCodeCategory>>();
    var code = "TX_TEST_" + Guid.NewGuid().ToString("N")[..8];

    await Should.ThrowAsync<InvalidOperationException>(async () =>
    {
        await tx.ExecuteAsync(async () =>
        {
            await repo.InsertAsync(new SysCodeCategory(
                Guid.NewGuid(), code, "事务测试", null, false, false, 9999), autoSave: false);

            throw new InvalidOperationException("模拟失败");
        });
    });

    // 关键断言：回滚后数据库里查不到
    var found = await repo.FindAsync(c => c.Code == code);
    found.ShouldBeNull();
}
```

**② 嵌套事务不会被内层提前提交**

```csharp
[Fact]
public async Task 嵌套事务_内层异常应回滚外层()
{
    var code = "NEST_" + Guid.NewGuid().ToString("N")[..8];

    await Should.ThrowAsync<InvalidOperationException>(async () =>
    {
        await tx.ExecuteAsync(async () =>
        {
            await repo.InsertAsync(outer, autoSave: false);      // 外层先插

            await tx.ExecuteAsync(async () =>                    // 内层
            {
                await repo.InsertAsync(inner, autoSave: false);
                throw new InvalidOperationException("内层失败");
            });
        });
    });

    // 两个都该没了
    (await repo.FindAsync(c => c.Code == code)).ShouldBeNull();
}
```

**③ 幂等验证（复现那次事故的场景）**

```powershell
# 连跑两次 DbMigrator，计数必须一致（第 06 章验证过）
# 如果种子数据"看起来插了但查不到"，就是这个坑又回来了
dotnet run --project src\Xjgl.DbMigrator 2>&1 | Select-String "码类别"
dotnet run --project src\Xjgl.DbMigrator 2>&1 | Select-String "码类别"
```

---

## 9.7 踩坑记录

### 坑 1：以为"没有异常 = 提交成功"

这次的 153/0 事故里，**没有任何异常**。
因为"连接关闭导致事务回滚"是数据库的正常语义，不是错误。

**通用教训**：**写完数据后，一定要用一个"全新的连接"去读一次做验证。**
在同一个连接/事务里读到自己刚写的数据，**不能证明数据已提交**
（读到的是自己未提交的变更）。

这也是为什么本项目的集成测试都用 `XjglIntegrationTestBase`，
每个测试方法结束前用仓储的**新查询**去断言 —— 而不是断言内存里的对象状态。

### 坑 2：在 `OnCompleted` 里做异步 IO

`uow.OnCompleted(async () => ...)` 传进去的是 `async void` 语义的委托，
异常会被吞掉（没有地方 await 它）。**任何在 UoW 回调里做的异步操作都是不可靠的**，
包括提交事务、写审计、发消息。

### 坑 3：`IsAutoCloseConnection = true` 与显式事务的配合

开了自动关连接后，**执行完命令连接就关**。
所以显式事务必须在**同一个访问器实例**上做 `Begin → 执行 → Commit`，
中途不能换客户端。本项目的 `SqlSugarClientAccessor` 是 Scoped，
同一个请求内拿到的永远是同一个客户端 —— 这是事务能正常工作的前提。

### 坑 4：把 `ISqlSugarTransactionScope` 注入成单例

它是 `ITransientDependency`，注入到单例里会形成 captive dependency
（第 07 章的坑）。**只在应用服务、领域服务这类作用域内使用。**

---

## 9.8 本章产出

| 项 | 值 |
|---|---|
| 抽象 | `ISqlSugarTransactionScope`（`ExecuteAsync`，支持嵌套） |
| 约定 | 单条写 → 自动提交；多步原子写 → 显式包一层 |
| 代价 | 跨 ORM（SqlSugar + EF Core）暂不能原子，靠"顺序 + 幂等 + 流水日志"缓解 |
| 事故复盘 | 「日志说插了 153 条、库里 0 条」——`DataSeeder` 的子作用域先释放了 Scoped 客户端 |
| 技术债 | 事务抽象应上提到 Domain 层；彻底方案是共享 `NpgsqlConnection` |

---

**上一章：** [08 SqlSugar 集成层（二）](08-SqlSugar集成层-仓储与软删除.md) ｜ **下一章：** [10 登录打通：OpenIddict 密码模式](10-登录打通-OpenIddict密码模式.md)
