# 08 SqlSugar 集成层（二）：仓储、软删除与审计字段

> **对应阶段**：P0 ｜ **预计耗时**：2.5 小时 ｜ **产出**：业务仓储可用了，且软删除、审计字段、排序翻译全自动

---

## 8.1 要解决的问题

有了客户端工厂（第 07 章），还差三件"每个仓储都要做、但谁也不该重复做"的事：

| 横切关注点 | 不做会怎样 |
|---|---|
| **软删除过滤** | 删除后还能查到 —— 用户会认为"删不掉" |
| **审计字段自动填充** | 每个插入都要手写 `CreatorId = CurrentUser.Id; CreationTime = Clock.Now;`，漏一处就丢审计 |
| **排序字符串翻译** | 前端传 `"StudentNo ASC"`，SqlSugar 需要的是列名 `student_no`，而且**绝不能把用户输入直接拼进 SQL** |

---

## 8.2 动手步骤

### 步骤 1：仓储骨架

```csharp
// src/Xjgl.SqlSugarCore/XjglRepository.cs（节选）
public class XjglRepository<TEntity> : ISqlSugarRepository<TEntity>
    where TEntity : class, IEntity, new()
{
    protected ISqlSugarClientAccessor ClientAccessor { get; }
    protected IUnitOfWorkManager UnitOfWorkManager { get; }
    protected IAuditPropertySetter AuditPropertySetter { get; }
    protected ILogger Logger { get; }

    public XjglRepository(
        ISqlSugarClientAccessor clientAccessor,
        IUnitOfWorkManager unitOfWorkManager,
        IAuditPropertySetter auditPropertySetter,
        ILogger<XjglRepository<TEntity>> logger)
    { ... }

    public virtual ISqlSugarClient Client => ClientAccessor.Client;
}
```

**注意 `IAuditPropertySetter`** —— 这是 ABP 提供的审计字段填充器，
它能正确处理"当前没有登录用户""多租户"等边界情况。
**不要自己写 `CreatorId = CurrentUser.Id`**，因为后台任务、种子数据这些场景下没有当前用户，
自己写会抛 `NullReferenceException`。

### 步骤 2：软删除过滤（重点）

```csharp
/// <summary>
/// 链式查询入口。
///
/// ★ 自动附加软删除过滤：实体实现 ISoftDelete 时，
///   所有读操作（Get / Find / List / Count / Any）都只看到未删除的数据。
/// </summary>
public virtual ISugarQueryable<TEntity> Queryable
{
    get
    {
        var query = Client.Queryable<TEntity>();
        var filter = GetSoftDeleteFilter();

        return filter == null ? query : query.Where(filter);
    }
}

/// <summary>不过滤软删除的查询入口（回收站、数据恢复等场景）。</summary>
public virtual ISugarQueryable<TEntity> QueryableIncludingDeleted => Client.Queryable<TEntity>();
```

过滤器表达式**按类型缓存**：

```csharp
private static readonly ConcurrentDictionary<Type, LambdaExpression?> SoftDeleteFilterCache = new();

private static Expression<Func<TEntity, bool>>? GetSoftDeleteFilter()
{
    var cached = SoftDeleteFilterCache.GetOrAdd(typeof(TEntity), static type =>
    {
        if (!typeof(ISoftDelete).IsAssignableFrom(type))
        {
            return null;      // 没实现 ISoftDelete 就不过滤
        }

        var property = type.GetProperty(nameof(ISoftDelete.IsDeleted));
        if (property == null || property.PropertyType != typeof(bool))
        {
            return null;
        }

        // 手工拼 e => !e.IsDeleted
        var parameter = Expression.Parameter(type, "e");
        var body = Expression.Not(Expression.Property(parameter, property));

        return Expression.Lambda(body, parameter);
    });

    return (Expression<Func<TEntity, bool>>?)cached;
}
```

**为什么要缓存**：这个表达式是**按泛型类型**固定的
（`StuStudent` 的过滤器永远一样）。每次查询都反射拼一遍表达式树，
在列表页（一页几十次查询）上是明显的浪费。

**为什么不用 `Expression<Func<TEntity,bool>>` 直接写死**：
因为 `TEntity` 是泛型参数，不是所有实体都实现 `ISoftDelete`
（例如纯关联表）。`e => ((ISoftDelete)e).IsDeleted == false` 这种写法
SqlSugar 翻译不出 SQL —— 它会试图把 `ISoftDelete` 当实体类型处理。

### 步骤 3：删除 = 打标记 + 写审计

```csharp
public virtual async Task DeleteAsync(TEntity entity, bool autoSave = false, ...)
{
    if (entity is ISoftDelete)
    {
        SetSoftDeleted(entity);              // 反射写 IsDeleted = true
        SetDeletionAuditProperties(entity);  // DeleterId / DeletionTime
        await Client.Updateable(entity).ExecuteCommandAsync();
    }
    else
    {
        await Client.Deleteable(entity).ExecuteCommandAsync();   // 硬删除
    }

    if (autoSave) { await SaveChangesAsync(cancellationToken); }
}
```

**为什么要反射写 `IsDeleted`**：ABP 10 把 `ISoftDelete.IsDeleted` 定义成了
**只读接口属性**（`bool IsDeleted { get; }`），实体上的实现通常是
`public virtual bool IsDeleted { get; protected set; }` —— 从外部无法赋值。
所以只能反射：

```csharp
private static readonly ConcurrentDictionary<Type, PropertyInfo?> IsDeletedPropertyCache = new();

private static void SetSoftDeleted(object entity)
{
    var property = IsDeletedPropertyCache.GetOrAdd(entity.GetType(),
        type => type.GetProperty(nameof(ISoftDelete.IsDeleted)));

    if (property == null || !property.CanWrite)
    {
        Logger.LogError(
            "实体 {Entity} 实现了 ISoftDelete，但 IsDeleted 属性不可写，软删除标记未生效。",
            entity.GetType().Name);
        return;
    }

    property.SetValue(entity, true);
}
```

> **注意这里 `CanWrite` 为 false 时是记 Error 日志而不是抛异常**。
> 为什么？因为"软删除没生效"这件事如果抛异常，会让调用方看到一个
> 与业务无关的技术错误；记 Error 日志能让运维在日志里发现，
> 而接口本身（用户点的"删除"）仍然表现为成功 —— 这个取舍的前提是
> **日志监控到位**。如果你的环境没人看日志，这里应该改成抛异常。

### 步骤 4：审计字段填充

```csharp
protected virtual void SetCreationAuditProperties(TEntity entity)
{
    TrySetAudit(() => AuditPropertySetter.SetCreationProperties(entity), "创建");
}

protected virtual void SetModificationAuditProperties(TEntity entity)
{
    TrySetAudit(() => AuditPropertySetter.SetModificationProperties(entity), "修改");
}

protected virtual void SetDeletionAuditProperties(TEntity entity)
{
    TrySetAudit(() => AuditPropertySetter.SetDeletionProperties(entity), "删除");
}

/// <summary>
/// 审计字段填充失败不阻断业务。
///
/// 为什么必须包一层：ABP 的 AuditPropertySetter 在"没有当前用户"这类边界下
/// 可能抛异常（例如后台任务、DbMigrator 里的种子数据）。
/// 审计字段是**辅助信息**，不能因为它写不进去就让"新增学生"失败。
/// 但同样地 —— 这里记的是 Error 日志，需要有人看。
/// </summary>
private void TrySetAudit(Action action, string stage)
{
    try
    {
        action();
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "填充{Stage}审计字段失败：{Entity}", stage, typeof(TEntity).Name);
    }
}
```

### 步骤 5：排序字符串翻译（安全）

前端传 `"StudentNo ASC, CreationTime DESC"`。**绝不能直接拼进 SQL**：

```csharp
protected virtual ISugarQueryable<TEntity> ApplySorting(ISugarQueryable<TEntity> query, string? sorting)
{
    if (string.IsNullOrWhiteSpace(sorting))
    {
        return query;         // ★ 空排序必须直接返回，不能拼出空的 ORDER BY
    }

    foreach (var part in sorting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) continue;

        var propertyName = tokens[0];
        var descending = tokens.Length > 1 &&
                         tokens[1].Equals("DESC", StringComparison.OrdinalIgnoreCase);

        // ★ 只允许实体上真实存在的属性，避免排序字段被拼进 SQL（注入）
        var property = typeof(TEntity).GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (property == null)
        {
            throw new BusinessException(XjglDomainErrorCodes.DataNotFound)
                .WithData("reason", $"排序字段"{propertyName}"不是 {typeof(TEntity).Name} 的属性");
        }

        // 用属性名（而非用户输入）去排序，SqlSugar 会按映射规则翻成列名
        query = query.OrderByPropertyName(property.Name, descending ? OrderByType.Desc : OrderByType.Asc);
    }

    return query;
}
```

**关键点**：先校验属性存在、再用**属性名**（来自反射结果，不是用户输入）排序。
这样即使前端传 `"1; DROP TABLE stu_student--"`，也只会得到一个"排序字段不合法"的业务错误。

---

## 8.3 验证方法

**① 软删除真的生效**

```powershell
dotnet test test\Xjgl.Application.Tests --filter-class "*CodeAppServiceTests*"
```

这些集成测试里有"删除后列表里查不到"的断言。
更直接的验证是手工做一遍：

```powershell
# 1) 建一条码表
curl -X POST http://localhost:44300/api/app/code/category -H "Authorization: Bearer $TOKEN" `
     -H "Content-Type: application/json" -d '{"code":"TMP01","name":"临时码表"}'

# 2) 删掉它
curl -X DELETE http://localhost:44300/api/app/code/category/<id> -H "Authorization: Bearer $TOKEN"

# 3) 列表里应该查不到
curl "http://localhost:44300/api/app/code/category?Keyword=TMP01" -H "Authorization: Bearer $TOKEN"

# 4) 但数据库里还在，且 is_deleted = true
docker exec -it xjgl-postgres psql -U xjgl -d xjgl `
  -c "SELECT code, is_deleted, deleter_id, deletion_time FROM sys_code_category WHERE code='TMP01';"
```

第 4 步是**最关键的验证**：它同时证明了
"软删除确实写了标记" + "删除审计字段被填了"。

**② 审计字段自动填充**

```powershell
docker exec -it xjgl-postgres psql -U xjgl -d xjgl `
  -c "SELECT student_no, creator_id, creation_time FROM stu_student LIMIT 3;"
```

`creator_id` 不应该为空（除非是在后台任务/种子数据里创建的）。

**③ 排序字段校验生效**

```powershell
curl "http://localhost:44300/api/app/student?Sorting=1;DROP%20TABLE%20stu_student--" -H "Authorization: Bearer $TOKEN"
```

期望返回一个业务错误（"排序字段...不是 StuStudent 的属性"），
而**不是** 500，更不是表被删掉。

---

## 8.4 踩坑记录

### 坑 1：「删除成功但还能查到」——软删除过滤漏了

最早的 `Queryable` 没有附加过滤，`DeleteAsync` 老老实实把 `is_deleted` 置了 true，
但所有的读操作都在读全部数据。用户看到的现象是"点了删除，刷新一下它又回来了"。

**修复**：把过滤放在 `Queryable` 属性里（而不是放在每个查询方法里）。
**为什么放在属性里**：因为所有查询方法（`GetAsync`/`FindAsync`/`GetListAsync`/
`GetPagedListAsync`/`GetCountAsync`/`AnyAsync`）都从 `Queryable` 出发 ——
只要这一个入口加了过滤，就没有遗漏的可能。
如果分散写在各方法里，早晚会漏一个。

### 坑 2：`GetCountAsync(null)` 拼出残缺 SQL（本项目最值得记住的一个 bug）

#### 现象

- `GET /api/app/student`（不带任何过滤条件）→ **500**
- `GET /api/app/student?SchoolOrgId=xxx`（带条件）→ **200 正常**
- 数据库日志：`42601: syntax error at end of input`

#### 根因

列表接口的过滤条件是**可选**的。应用服务里是这样拼谓词的：

```csharp
var predicate = BuildPredicate(input);   // 没有任何过滤条件时返回 null
var totalCount = await _students.GetCountAsync(predicate);   // ← 传了 null
```

而仓储实现是：

```csharp
// 修复前
public virtual async Task<long> GetCountAsync(
    Expression<Func<TEntity, bool>> predicate, ...)
{
    return await Queryable.Where(predicate).CountAsync();
}
```

`Queryable.Where(null)` 不会报错，SqlSugar 会拼出一条以 `WHERE` 结尾的 SQL：

```sql
SELECT COUNT(*) FROM stu_student WHERE
```

PostgreSQL 直接判定语法错误。

#### 为什么 138 个集成测试一个都没发现

**因为所有集成测试都带了过滤条件。**
测试数据是自己造的，天然就有 `schoolOrgId`、`studentId` 之类的条件可传 ——
于是"谓词为 null"这条代码路径**从来没有被走过**。

#### 更值得反思的一点

编译期其实早就指出了位置：

```
warning CS8604: 形参 "predicate" 可能传入 null 引用实参。
```

我在第一次构建时就看到了这 4 条警告，判断成"噪音"忽略了。
**可空引用类型的警告不是风格建议，它是在说"这里可能传 null"。**
后来我把 `Directory.Build.props` 里的注释改成了一句提醒
（见第 02 章 `TreatWarningsAsErrors` 那一行的说明），
并在修复时把这个签名显式改成可空：

```csharp
// 修复后：接口与实现都把谓词声明为可空，并在实现里跳过 Where
Task<long> GetCountAsync(Expression<Func<TEntity, bool>>? predicate, ...);

public virtual async Task<long> GetCountAsync(
    Expression<Func<TEntity, bool>>? predicate, ...)
{
    return predicate == null
        ? await Queryable.CountAsync()
        : await Queryable.Where(predicate).CountAsync();
}
```

并补了一个回归用例：

```csharp
[Fact]
public async Task 不带任何过滤条件查询列表_不应报SQL语法错误()
{
    var photos = await _photoAppService.GetListAsync(new PhotoQueryDto { MaxResultCount = 5 });
    photos.TotalCount.ShouldBeGreaterThanOrEqualTo(0);

    var students = await _studentAppService.GetListAsync(new StudentQueryDto { MaxResultCount = 5 });
    students.TotalCount.ShouldBeGreaterThanOrEqualTo(0);
}
```

#### 这件事的通用教训

> **"没测过的输入组合，就是没覆盖。"**
> 写集成测试时，除了"正常带条件查询"，一定要补一条"不带任何条件查询全部"。
> 后者往往才是用户第一次打开页面时真正走的路径。

### 坑 3：`FirstAsync()` 的返回

SqlSugar 的 `FirstAsync()` 在无匹配时返回 `null`（不是抛异常）。
所以 `GetAsync(predicate)` 里必须自己判断并抛 `EntityNotFoundException`，
否则调用方会拿到 null 然后 `NullReferenceException`。

### 坑 4：批量更新时 `Updateable(list)` 的列范围

`Client.Updateable(list).ExecuteCommandAsync()` 默认更新**全部列**。
如果列表里的实体是从别处查出来又改了两个字段，
其余字段可能带着旧值一起写回去（丢失并发修改）。
本项目当前的用法都是从库里查出来立刻改，所以没问题；
但如果将来做"部分更新"，要用 `Updateable(list).UpdateColumns(...)` 显式指定列。

---

## 8.5 本章产出

| 项 | 值 |
|---|---|
| 软删除 | 过滤放在 `Queryable` 属性里，表达式按类型缓存；`QueryableIncludingDeleted` 供回收站用 |
| 审计 | 交给 ABP 的 `IAuditPropertySetter`，失败只记日志不阻断业务 |
| 排序 | 先校验属性存在，再用属性名排序（防注入） |
| 修复 | `GetCountAsync` 的谓词改为可空并跳过 `Where`；补回归用例 |
| 教训 | 编译期 CS8604 不是噪音；没测过的输入组合就是没覆盖 |

---

**上一章：** [07 SqlSugar 集成层（一）](07-SqlSugar集成层-客户端工厂与命名约定.md) ｜ **下一章：** [09 混合 ORM 的事务](09-混合ORM的事务怎么共存.md)
