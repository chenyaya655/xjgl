# ABP + SqlSugar 混合 ORM：边界、事务，以及一个必须知道的死结

> 本文是河南省学籍管理系统的对外技术文章之一，基于真实落地代码。
> 完整代码见仓库 `src/Xjgl.SqlSugarCore/` 与 `src/Xjgl.Domain/Data/`，
> 逐章教程见 [`docs/learning/07~09`](../learning/README.md)。

## 一、为什么会走到"混合 ORM"这一步

选 ABP 的人通常看中它开箱即用的东西：Identity、权限、审计、多语言、工作单元。
但 ABP 的一切持久化设施——仓储、`IUnitOfWork`、软删除过滤器、审计字段赋值——
都是围绕 **EF Core** 设计的。

而国内做政务/教育信息化的团队，业务表往往已经在用 **SqlSugar**（或 FreeSql、
Dos.ORM 一类）：团队熟悉、CodeFirst 建表快、批量操作方便、DBA 也认它。

于是就有了这个很自然的分工：

| 用途 | ORM | 表 |
|---|---|---|
| ABP 框架自带模块（Identity / OpenIddict / 权限 / 审计） | EF Core + Npgsql | `Abp*` |
| 业务表 | SqlSugar | `sys_*` / `stu_*` / `stu_photo` … |

**同一个 PostgreSQL 库，两套 ORM。** 这不是奇技淫巧，而是"既想用 ABP 的生态、
又不想放弃业务侧熟悉的 ORM"时最务实的解法。本文讲清它的边界在哪。

---

## 二、第一件事：把两种实体彻底分开

分层上最容易犯的错是"一套实体两套映射"，那会立刻掉进双向维护的深渊。我们的做法是**按表归属切割**：

```csharp
// 业务实体的基类：只带 SqlSugar 关心的东西
public abstract class XjglBusinessEntity : IEntity<Guid>
{
    public Guid Id { get; set; }
    // 软删除、审计时间、并发戳……
}
```

- 业务实体**继承自我们自己的基类**，不继承 ABP 的 `FullAuditedAggregateRoot`；
- 业务实体**不出现在 `DbContext` 里**，因此 EF 迁移完全看不见它们；
- ABP 的框架实体照旧走 `XjglDbContext`，其迁移由 `dotnet ef migrations` 管理。

**验证方法**：`dotnet ef migrations add` 生成的迁移里**不应该出现任何 `sys_`/`stu_` 表**。
如果出现了，说明有业务实体混进了 `DbContext` —— 这正是"两套 ORM 互相污染"的开端。

### 业务表怎么建

SqlSugar 的 CodeFirst 在迁移器里跑，且**必须显式控制顺序**（有外键依赖）：

```csharp
// 建表：业务表由 SqlSugar CodeFirst 负责
db.CodeFirst.InitTables(
    typeof(SysSubsystem), typeof(SysModule), typeof(SysModuleAction),
    typeof(SysOrg), typeof(SysGrade), typeof(SysClass),
    typeof(StuStudent), /* … */);
```

一个真实踩过的坑：**实体改了字段，SqlSugar 的 `InitTables` 默认不会删列、也不会改列类型**，
它是"缺表建表、缺列加列"的保守策略。所以改字段类型时必须自己写脚本 ——
这一点与 EF 迁移的心智模型**不一样**，团队里必须有人说清楚。

---

## 三、第二件事：仓储抽象必须与 ORM 无关

业务层不能依赖 SqlSugar 的类型，否则"以后想换 ORM"就成了一句空话，
更要命的是**单元测试会被迫造 SqlSugar 的上下文**。

所以我们定义了自己的仓储接口（放在 Domain，不引用任何 ORM）：

```csharp
public interface IXjglRepository<TEntity> where TEntity : class, IEntity<Guid>
{
    Task<TEntity?> FindAsync(Expression<Func<TEntity, bool>> predicate);
    Task<List<TEntity>> GetListAsync(Expression<Func<TEntity, bool>>? predicate = null);
    Task<long> GetCountAsync(Expression<Func<TEntity, bool>>? predicate = null);
    Task<TEntity> InsertAsync(TEntity entity, bool autoSave = false);
    Task<TEntity> UpdateAsync(TEntity entity, bool autoSave = false);
    Task DeleteAsync(TEntity entity, bool autoSave = false);
}
```

SqlSugar 侧实现它，并顺手把三件"每个项目都会重复写"的事收敛掉：

1. **软删除**：查询自动过滤 `is_deleted`，删除变成更新；
2. **审计字段**：`creation_time` / `last_modifier_id` 自动填；
3. **命名约定**：C# 的 `PascalCase` ↔ 数据库的 `snake_case`，在客户端工厂里统一注册。

```csharp
// 客户端工厂里统一约定，业务代码不需要到处写 [SugarColumn]
ConfigureExternalServices = new ConfigureExternalServices
{
    EntityService = (property, column) =>
    {
        column.DbColumnName = ToSnakeCase(property.Name);
        // 可空引用类型 => 允许 NULL，反之 NOT NULL
        if (property.PropertyType.IsGenericType
            && property.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            column.IsNullable = true;
        }
    }
}
```

---

## 四、死结：ABP 的 `IUnitOfWork` 不暴露 `TransactionApi`

这是本文最重要的一节。

在 ABP 里，业务代码习惯这么写：

```csharp
using var uow = _unitOfWorkManager.Begin(requiresNew: true);
await _studentRepo.InsertAsync(student);        // SqlSugar
await _roleManager.CreateAsync(new IdentityRole(…)); // EF Core
await uow.CompleteAsync();
```

**期望**：要么一起成功，要么一起回滚。
**实际**：`uow.CompleteAsync()` 只提交 **EF Core** 那一侧的事务；
SqlSugar 的操作在它自己的连接上、通过 `autoSave` 已经提交了。

根因：`IUnitOfWork` 的公开接口里**没有** `TransactionApi` / `GetDbTransaction()` 这类成员。
第三方 ORM 无法挂接到 ABP 的提交链路上。想共享事务，只能让两套 ORM
**共用同一个 `DbConnection` + `DbTransaction`**，而 ABP 并没有为第三方 ORM
留出这个口子（EF Core 侧是它自己实现的 `EfCoreTransactionApi`）。

> 如果你见过"混合 ORM 共享事务"的例子，多半是把 SqlSugar 挂到 EF Core 的
> `DbContext.Database.CurrentTransaction` 上，或者反过来让 SqlSugar 用 EF 的连接。
> 这在本项目里评估过：它要求两套 ORM 生命周期完全同步，且一旦 ABP 内部
> 提前开启事务（它的 UoW 是**隐式**的），你的挂接就会失效 —— 属于"看起来能用、
> 边界情况下静默失效"的方案。政务系统里这种不确定性比"承认做不到"更危险。

### 我们能做到什么：单侧显式事务 + 跨侧缓解

**做得到的**：把**同侧**（都是 SqlSugar 表）的多步写操作包进显式事务。
接口上提到 Domain，应用层就能用：

```csharp
public interface IXjglTransactionScope : ITransientDependency
{
    Task<T> ExecuteAsync<T>(Func<Task<T>> action);
}
```

一个真实例子是"异动审批"：它要写 4 张表（申请单状态、审核流水、学生学籍状态、变更留痕）。
这 4 张都是业务表，因此包进一个事务是**真原子**的：

```csharp
return await _tx.ExecuteAsync(async () =>
{
    apply.Approve(node, operatorId, comment);
    await _applies.UpdateAsync(apply);

    student.ApplyChange(apply.AfterStatusCode);
    await _students.UpdateAsync(student);

    await _changeLogs.InsertAsync(BuildLog(apply, before, after));
    return ObjectMapper.Map<ChangeApply, ChangeApplyDto>(apply);
});
```

> ⚠️ 一个 ABP 约定式注册的坑：把接口上提到 Domain 之后，DI 会报
> `Cannot resolve parameter 'IXjglTransactionScope tx'`。探针显示**实现类能解析、接口不能**。
> 原因是 ABP 对"实现了 `ITransientDependency` 的类"默认**只注册成它自己**。
> 修法是显式声明：
> ```csharp
> [ExposeServices(typeof(IXjglTransactionScope))]
> public class SqlSugarTransactionScope : IXjglTransactionScope, ITransientDependency
> ```
> 在接口上继承 `ITransientDependency` **实测无效**。

**做不到的**（跨 ORM 写操作真原子）用三条工程手段缓解，而不是假装解决了：

| 手段 | 具体做法 |
|---|---|
| **顺序** | 把"不可逆"的操作放最后。例如先写业务表、最后写框架表，失败时业务表可重放 |
| **幂等** | 所有跨侧操作都设计成可重复执行（用自然键判存在，而不是"跑一次打个标记"） |
| **流水** | 每一步都写一条变更留痕。半成品状态可以被**发现并修复**，而不是永远沉默 |

这三条不是理论：项目里的"用户预置 → 激活"链路就是跨 Identity 表与业务表的，
实际出现过"账号建好了、预置记录没关联"的半成品，靠流水与幂等修复脚本兜住。

---

## 五、什么时候不该这么做

诚实地讲，以下情况**不要**选混合 ORM：

1. **团队只有一套 ORM 的经验**：两套的心智负担是真实成本，省下的迁移时间会加倍还回去；
2. **业务表与框架表需要强一致的跨表写入**：比如"扣款 + 记账"这种，混合 ORM 会让你很痛苦；
3. **能用 ABP 原生 EF Core 解决**：SqlSugar 的优势主要在批量与 CodeFirst 便利，
   如果这两点你用不上，就别引入第二套 ORM。

所以本文的结论不是"混合 ORM 真香"，而是：

> **混合 ORM 可以做，边界必须画在"表"上，而不是"模块"上；
> 并且要提前承认"跨 ORM 没有共享事务"这件事，用顺序 + 幂等 + 流水去缓解，
> 而不是指望某天找到一个巧妙写法。**

---

## 附：本项目的相关位置

| 内容 | 位置 |
|---|---|
| 仓储抽象（ORM 无关） | `src/Xjgl.Domain/Data/IXjglRepository.cs` |
| 事务抽象 | `src/Xjgl.Domain/Data/IXjglTransactionScope.cs` |
| SqlSugar 实现 | `src/Xjgl.SqlSugarCore/` |
| 单侧事务的真实用例 | `src/Xjgl.Application/Changes/ChangeFlowAppService.cs` |
| 逐章教程 | [`docs/learning/07`](../learning/07-SqlSugar集成层-客户端工厂与命名约定.md)、[`08`](../learning/08-SqlSugar集成层-仓储与软删除.md)、[`09`](../learning/09-混合ORM的事务怎么共存.md) |
