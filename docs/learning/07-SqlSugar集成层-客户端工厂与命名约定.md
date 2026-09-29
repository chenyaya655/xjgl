# 07 SqlSugar 集成层（一）：客户端工厂与命名约定

> **对应阶段**：P0 ｜ **预计耗时**：2 小时 ｜ **产出**：业务实体自动映射成 snake_case 的 PostgreSQL 表，且领域层对 ORM 零感知

---

## 7.1 要解决的问题

用 SqlSugar 取代 ABP 默认的 EF Core 做业务 ORM，会同时冒出来三个问题：

| 问题 | 为什么会成为问题 |
|---|---|
| **SqlSugar 客户端怎么创建？** | SqlSugar 的 `SqlSugarClient` **不是线程安全的**，不能全局共享一个实例；但每个请求都 `new` 一个又不知道连接串从哪来 |
| **表名/列名怎么定？** | SqlSugar 默认用类名当表名（`StuStudent`），而我们要 snake_case（`stu_student`） |
| **领域层要不要打 ORM 特性？** | 打 `[SugarTable("stu_student")]` 最省事，但那意味着 `Xjgl.Domain` 必须引用 SqlSugar —— 第 04 章的铁律 1 就破了 |

本章解决前两个，第三个靠"**约定集中在工厂里**"来解决。

---

## 7.2 关键判断：为什么用「单例工厂 + 作用域访问器」

先说一个我真实踩过的坑，它决定了整套设计的形状。

**最初的写法**：把 `ISqlSugarClient` 直接注册成 Scoped，谁需要谁注入。

```csharp
// 反面教材
context.Services.AddScoped<ISqlSugarClient>(sp => { ... });
```

问题：元数据缓存（`IModuleMetadataCache`）需要**单例**且要查数据库，
它的构造函数里注入了 `ISqlSugarClient` —— 于是**单例捕获了作用域服务**（captive dependency）。
ABP 在启动时会抛 `Cannot consume scoped service from singleton`
（或者更糟：开了 `ValidateScopes=false` 时静默地持有一个永不释放的连接）。

**正确做法：拆成两个类型，各司其职。**

```
ISqlSugarClientFactory   （单例）—— 只负责"按配置创建客户端"，无状态
        ↓ 创建
SqlSugarClientAccessor   （作用域）—— 持有"本次请求/本次工作单元的客户端"
        ↓ 被注入
XjglRepository<T>        （瞬时）—— 用访问器拿到当前客户端
```

- **工厂**：单例，因为它只读配置，不持有连接；
- **访问器**：作用域，一个请求一个客户端，请求结束随 DI 作用域一起释放；
- **仓储**：瞬时，从访问器取客户端，不自己管生命周期。

---

## 7.3 动手步骤

### 步骤 1：配置项

```csharp
// src/Xjgl.SqlSugarCore/XjglSqlSugarCoreOptions.cs
namespace Xjgl.SqlSugarCore;

public class XjglSqlSugarCoreOptions
{
    /// <summary>用哪个连接串（对应 ConnectionStrings 节点下的名字）。</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>数据库类型。目前只用 PostgreSQL，留成配置项是为了将来能换库。</summary>
    public DbType DbType { get; set; } = DbType.PostgreSQL;

    /// <summary>是否把表名/列名转成 snake_case。</summary>
    public bool UseSnakeCaseTableName { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>是否打印 SQL 到日志（只在排查问题时开，生产别开）。</summary>
    public bool EnableSqlLog { get; set; }
}
```

`appsettings.json`：

```json
{
  "Xjgl": {
    "SqlSugar": {
      "ConnectionStringName": "Default",
      "DbType": "PostgreSQL",
      "UseSnakeCaseTableName": true,
      "CommandTimeoutSeconds": 60,
      "EnableSqlLog": false
    }
  }
}
```

### 步骤 2：工厂接口与实现

```csharp
// ISqlSugarClientFactory.cs
public interface ISqlSugarClientFactory
{
    ISqlSugarClient Create();
}
```

实现的核心是 `ConfigureExternalServices` —— **命名约定全在这里**：

```csharp
// SqlSugarClientFactory.cs（节选，完整实现见源码）
public class SqlSugarClientFactory : ISqlSugarClientFactory, ISingletonDependency
{
    public ISqlSugarClient Create()
    {
        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = GetConnectionString(),
            DbType = _options.DbType,
            IsAutoCloseConnection = true,          // 用完即关，避免连接泄漏
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = ConfigureEntityName,     // 表名
                EntityService = ConfigureEntityColumn        // 列名、主键、可空、类型
            }
        });

        client.Ado.CommandTimeOut = _options.CommandTimeoutSeconds;
        return client;
    }

    /// <summary>表名映射：实体 StuStudent → 表 stu_student。</summary>
    private void ConfigureEntityName(Type _, EntityInfo entityInfo)
    {
        if (_options.UseSnakeCaseTableName)
        {
            entityInfo.DbTableName = UtilMethods.ToUnderLine(entityInfo.DbTableName);
        }
    }
}
```

### 步骤 3：列映射的六条规则（这一节是本章重点）

```csharp
private void ConfigureEntityColumn(PropertyInfo property, EntityColumnInfo column)
{
    // 规则 1：ABP 自己托管的属性不进数据库
    if (property.Name == "ExtraProperties")
    {
        column.IsIgnore = true;
        return;
    }

    // 规则 2：只读属性不进数据库
    if (!property.CanWrite)
    {
        column.IsIgnore = true;
        return;
    }

    // 规则 3：列名转 snake_case（显式写了 SugarColumn(ColumnName=...) 的除外）
    var explicitColumn = property.GetCustomAttribute<SugarColumn>();
    var hasExplicitName = explicitColumn is { ColumnName: not null };
    if (_options.UseSnakeCaseTableName && !hasExplicitName)
    {
        column.DbColumnName = UtilMethods.ToUnderLine(column.DbColumnName);
    }

    // 规则 4：主键按名字识别
    if (property.Name == nameof(IEntity<Guid>.Id))
    {
        column.IsPrimarykey = true;
        column.IsIdentity = false;      // Guid 主键由应用生成，不是数据库自增
    }

    // 规则 5：可空性按 C# 可空注解判断
    if (!column.IsPrimarykey)
    {
        column.IsNullable = IsPropertyNullable(property);
    }

    // 规则 6：字符串列的类型与长度按列名约定
    if (property.PropertyType == typeof(string))
    {
        ApplyStringColumnConvention(column);
    }
}
```

### 规则 1 详解：为什么必须忽略 `ExtraProperties`

ABP 的实体基类都带一个 `ExtraProperties`（类型 `ExtraPropertyDictionary`）。
如果不忽略它，SqlSugar 会把它当普通列读写，插入时报：

```
InvalidCastException: Writing values of 'Volo.Abp.Data.ExtraPropertyDictionary'
is not supported for parameters having DataTypeName 'text'
```

**约定：业务实体的扩展字段一律用显式列表达**（例如 `ext_json` 存 JSON 字符串），
不要依赖 `ExtraProperties`。

### 规则 5 详解：可空性必须用 `NullabilityInfoContext`

这是本项目第二个"只有跑起来才发现"的坑。

最初用 `property.PropertyType.IsValueType` 判断可空性 —— 于是所有 `string` 都被建成**可空列**。
问题在于领域实体的写法是：

```csharp
public string Code { get; set; } = default!;   // 业务上必填
public string? Remark { get; set; }            // 业务上可空
```

两个都是 `string`，`IsValueType` 都是 `false`，于是都建成了可空列。
**结果是数据库层面完全失去了"必填"约束**，脏数据可以长驱直入。

正确做法是用 `NullabilityInfoContext` 读 C# 的可空注解：

```csharp
private static readonly NullabilityInfoContext NullabilityContext = new();

private static bool IsPropertyNullable(PropertyInfo property)
{
    if (property.PropertyType.IsValueType)
    {
        return Nullable.GetUnderlyingType(property.PropertyType) != null;
    }

    try
    {
        return NullabilityContext.Create(property).WriteState == NullabilityState.Nullable;
    }
    catch
    {
        // 泛型等取不到注解的场景，保守地建为可空
        return true;
    }
}
```

### 规则 6 详解：字符串列长的约定

```csharp
private static void ApplyStringColumnConvention(EntityColumnInfo column)
{
    if (!string.IsNullOrEmpty(column.DataType) || column.Length > 0)
    {
        return;      // 显式指定过就不覆盖
    }

    var name = column.DbColumnName;

    // 以 _json 结尾 → text。**刻意不用 jsonb**，原因见下方踩坑记录
    if (name.EndsWith("_json", StringComparison.Ordinal))
    {
        column.DataType = "text";
        return;
    }

    // 备注/描述类 → text
    if (TextColumnNames.Contains(name))   // remark, description, content, opinion, reason, address
    {
        column.DataType = "text";
        return;
    }

    // 路径/URL/编码/名称类 → 512
    if (LongColumnSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)))
    {
        column.Length = 512;              // _path, _url, _code, _name
        return;
    }

    // 其余 → 256
    column.Length = 256;
}
```

> **这套"按列名约定类型"的做法是有争议的**：它把"列有多长"这件事从实体定义里拿走了。
> 代价是：想知道 `student_no` 是多长，得去读工厂代码。
> 收益是：**领域层保持对 ORM 完全无感知**，实体上没有任何 `[SugarColumn]`，
> 换 ORM（甚至换数据库）时实体一行都不用改。
>
> 如果你更看重"就地可读"，可以改成在实体上打特性 —— 那是另一种合理取舍，
> 但**不要两者混用**，否则"这个列到底多长"会有两个来源。

### 步骤 4：作用域访问器

```csharp
// SqlSugarClientAccessor.cs
public class SqlSugarClientAccessor : ISqlSugarClientAccessor, IScopedDependency
{
    private ISqlSugarClient? _client;

    public ISqlSugarClient Client =>
        _client ??= _factory.Create();      // 懒创建：本次请求不用数据库就不建连接
}
```

### 步骤 5：模块注册

```csharp
// XjglSqlSugarCoreModule.cs
[DependsOn(typeof(XjglDomainModule))]
public class XjglSqlSugarCoreModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();

        Configure<XjglSqlSugarCoreOptions>(configuration.GetSection("Xjgl:SqlSugar"));

        // 开放泛型仓储必须显式注册：ABP 的约定式注册只认它自己那套 IRepository<>，
        // 不认识我们的 IXjglRepository<>，不写这行解析时会报
        // "No service for type 'Xjgl.Data.IXjglRepository`1[...]'"
        context.Services.AddTransient(
            typeof(IXjglRepository<>), typeof(XjglRepository<>));

        context.Services.AddTransient(
            typeof(ISqlSugarRepository<>), typeof(XjglRepository<>));
    }
}
```

---

## 7.4 验证方法

**① 表名与列名映射正确**

跑完 DbMigrator 后：

```powershell
docker exec -it xjgl-postgres psql -U xjgl -d xjgl -c "\d stu_student"
```

逐条核对：

| 期望 | 为什么 |
|---|---|
| 表名 `stu_student` | 实体 `StuStudent` → snake_case |
| 列 `student_no`、`id_card_hash` | 属性 `StudentNo` → snake_case |
| `id` 是主键、无自增（无 `nextval`） | Guid 主键由应用生成 |
| `name` 是 `varchar(256)` | 默认字符串长度 |
| `remark` 是 `text` | 列名在 `TextColumnNames` 里 |
| **没有** `extra_properties` 列 | 规则 1 生效 |
| `name` 是 `NOT NULL`，`remark` 可空 | 规则 5 按可空注解判断 |

**② 领域层确实没引用 SqlSugar**

```powershell
dotnet list src\Xjgl.Domain\Xjgl.Domain.csproj reference
Select-String -Path src\Xjgl.Domain\**\*.cs -Pattern "using SqlSugar"
# 期望：都没有（除了 obj 目录下的自动生成文件）
```

**③ 单例没有捕获作用域服务**

```powershell
dotnet run --project src\Xjgl.HttpApi.Host
```

在 Development 环境下，DI 容器**默认会校验作用域**，
captive dependency 会在第一次解析单例时抛异常。启动成功即说明这条没问题。
（如果为了性能关掉了校验，就要靠人工 review 构造函数。）

---

## 7.5 踩坑记录

### 坑 1：`jsonb` 列写入报 42804

我把一个注释列命名成 `ext_json`，想当然地用 `jsonb` 类型。插入时报：

```
42804: column "ext_json" is of type jsonb but expression is of type text
```

原因：C# 属性是 `string` 时，Npgsql 按 `text` 发送参数，
而 PostgreSQL **不会**把 `text` 隐式转成 `jsonb`。

**处理**：统一用 `text`。若确实需要 `jsonb` 的索引与运算符，
应把实体属性改成**强类型对象**（让 SqlSugar 按 JSON 序列化并绑定 `jsonb`），
而不是用 `string` 硬塞。

### 坑 2：开放泛型仓储不注册，运行时报找不到服务

ABP 的约定式 DI 只注册它认识的接口（`IRepository<>`、`IApplicationService` 等）。
自定义的 `IXjglRepository<>` 必须显式 `AddTransient`。
**注意是开放泛型**（`typeof(IXjglRepository<>)`），不是每个实体注册一次。

### 坑 3：`InitKeyType` 设错导致主键识别失败

`InitKeyType.Attribute` 表示"从特性读主键信息"。
但我们**故意不给实体打特性**，所以主键是靠规则 4 按属性名 `Id` 识别的。
如果写成 `InitKeyType.SystemTable`，SqlSugar 会去查数据库元数据 ——
建表阶段表还不存在，行为会变得难以预测。**保持 `Attribute`**。

### 坑 4：每个请求 `new SqlSugarClient` 忘了关连接

`IsAutoCloseConnection = true` 是必须的（本项目已设）。
不设的话，一次请求里的多次查询会共用一个连接，
请求结束若没走到 `Dispose`，连接会一直挂着，直到连接池耗尽，
表现为"系统跑几小时后突然全部超时"。

---

## 7.6 本章产出

| 项 | 值 |
|---|---|
| 类型 | `ISqlSugarClientFactory`（单例）、`SqlSugarClientAccessor`（作用域）、`XjglRepository<T>`（瞬时） |
| 命名 | 表名/列名 snake_case，主键按 `Id` 识别，可空性按 C# 注解，字符串长度按列名约定 |
| 关键取舍 | 领域层**零 ORM 特性**，映射规则集中在工厂 |
| 注册 | 开放泛型仓储显式注册 |

---

**上一章：** [06 DbMigrator](06-DbMigrator迁移器.md) ｜ **下一章：** [08 SqlSugar 集成层（二）：仓储、软删除与审计字段](08-SqlSugar集成层-仓储与软删除.md)
