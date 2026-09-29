using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// <see cref="ISqlSugarClientFactory"/> 的默认实现。
///
/// 同时承担「实体 ↔ 数据库」的映射约定：
/// 因为领域层刻意不引用任何 ORM（实体上没有 SqlSugar 特性），
/// 表名、列名、主键、可空性、列类型等规则全部集中在这里推导。
/// </summary>
public class SqlSugarClientFactory : ISqlSugarClientFactory, ISingletonDependency
{
    /// <summary>这些列名使用 PostgreSQL 的 text 类型（不限长度）。</summary>
    private static readonly HashSet<string> TextColumnNames = new(StringComparer.Ordinal)
    {
        "remark", "description", "content", "opinion", "reason", "address"
    };

    /// <summary>这些后缀的列使用 512 长度（路径、URL、编码、名称等）。</summary>
    private static readonly string[] LongColumnSuffixes = { "_path", "_url", "_code", "_name" };

    private readonly IConfiguration _configuration;
    private readonly ILogger<SqlSugarClientFactory> _logger;
    private readonly XjglSqlSugarCoreOptions _options;
    private readonly object _sync = new();

    private string? _connectionString;

    public SqlSugarClientFactory(
        IConfiguration configuration,
        IOptions<XjglSqlSugarCoreOptions> options,
        ILogger<SqlSugarClientFactory> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _options = options.Value;
    }

    public ISqlSugarClient Create()
    {
        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = GetConnectionString(),
            DbType = _options.DbType,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = ConfigureEntityName,
                EntityService = ConfigureEntityColumn
            }
        });

        client.Ado.CommandTimeOut = _options.CommandTimeoutSeconds;

        if (_options.EnableSqlLog)
        {
            client.Aop.OnLogExecuting = (sql, _) =>
            {
                _logger.LogDebug("SqlSugar SQL: {Sql}", sql);
            };
        }

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

    /// <summary>列映射规则，见方法内注释。</summary>
    private void ConfigureEntityColumn(PropertyInfo property, EntityColumnInfo column)
    {
        // 1) ABP 框架自己托管的属性不进数据库。
        //    ExtraProperties 的类型是 Volo.Abp.Data.ExtraPropertyDictionary，
        //    它由 ABP 自身维护；若让 SqlSugar 当普通列读写，会在插入时报
        //      InvalidCastException: Writing values of 'ExtraPropertyDictionary'
        //      is not supported for parameters having DataTypeName 'text'
        //    业务实体请用显式列表达扩展字段，不要依赖 ExtraProperties。
        if (property.Name == "ExtraProperties")
        {
            column.IsIgnore = true;
            return;
        }

        // 2) 其它只读属性同样不进数据库
        if (!property.CanWrite)
        {
            column.IsIgnore = true;
            return;
        }

        // 3) 列名同样转 snake_case（属性上显式写了 ColumnName 的除外）
        var explicitColumn = property.GetCustomAttribute<SugarColumn>();
        var hasExplicitName = explicitColumn is { ColumnName: not null };

        if (_options.UseSnakeCaseTableName && !hasExplicitName)
        {
            column.DbColumnName = UtilMethods.ToUnderLine(column.DbColumnName);
        }

        // 4) 主键：ABP 的主键叫 Id 且定义在框架基类里，无法打特性，这里按名字识别
        if (property.Name == nameof(IEntity<Guid>.Id))
        {
            column.IsPrimarykey = true;
            column.IsIdentity = false;
        }

        // 5) 可空性：引用类型必须按 C# 可空注解判断，
        //    否则 string 和 string? 会被一律建成可空列（code / name 就吃过这个亏）
        if (!column.IsPrimarykey)
        {
            column.IsNullable = IsPropertyNullable(property);
        }

        // 6) 字符串列的类型与长度约定（领域层不带 ORM 特性，规则集中在这里）
        if (property.PropertyType == typeof(string))
        {
            ApplyStringColumnConvention(column);
        }

        // 7) 非空值类型列带上「与 CLR 默认值一致」的数据库默认值。
        //
        // 【为什么必须有】CodeFirst 给**已有数据的表**补列时执行的是
        //     ALTER TABLE ... ADD COLUMN x boolean NOT NULL
        // 而 PostgreSQL 要求"新增的 NOT NULL 列必须带默认值"，否则报
        //     23502: column "x" of relation "t" contains null values
        // 带 DEFAULT 之后 PG 会把存量行**回填**成该默认值，补列即可成功。
        //
        // 本项目真的撞上了：把 `stu_change_type.require_attachment` 加回来时
        // DbMigrator 直接失败（迁移停在半路，随后 63 个集成测试全红）。
        // 这不是"某一张表的问题"：任何"给已上线的表补一个非空列"都会踩到，
        // 所以修在映射约定里，而不是给那一个字段打特性 ——
        // 领域层要保持对 ORM 无感知（见 ApplyStringColumnConvention 的说明）。
        //
        // 【为什么取 CLR 默认值而不是业务默认值】"数据库里没有这个字段时它等于什么"
        // 唯一不需要额外知识的答案是 C# 的 default；业务默认值（例如 IsEnabled 默认 true）
        // 由属性初始化器在插入时表达 —— 本项目所有写入都是整行列全写，
        // 列默认值只影响 DDL 与裸 SQL，不会被业务读到。
        if (!column.IsPrimarykey
            && !column.IsNullable
            && ValueTypeDefaultLiterals.TryGetValue(property.PropertyType, out var defaultLiteral))
        {
            column.DefaultValue = defaultLiteral;
        }
    }

    /// <summary>
    /// 非空值类型列的数据库默认值，字面量与 C# 的 <c>default(T)</c> 一致。
    /// 未列出的类型（枚举等）不设默认值：拿不准的类型宁可让建表报错，
    /// 也不要写一个语义可疑的默认值进库。
    /// </summary>
    private static readonly Dictionary<Type, string> ValueTypeDefaultLiterals = new()
    {
        [typeof(bool)] = "false",
        [typeof(int)] = "0",
        [typeof(long)] = "0",
        [typeof(short)] = "0",
        [typeof(byte)] = "0",
        [typeof(decimal)] = "0",
        [typeof(double)] = "0",
        [typeof(float)] = "0",

        // Guid 与时间类型的字面量**不能自带单引号**：
        // SqlSugar 生成 DDL 时会把这些默认值当参数传给数据库，自带引号会被
        // 一起塞进值里，PG 报 22P02: invalid input syntax for type uuid: "'0000...'"
        // （实测踩到过）。而 bool / 数字不带引号本来就是合法字面量。
        [typeof(Guid)] = "00000000-0000-0000-0000-000000000000",
        [typeof(DateTime)] = "0001-01-01 00:00:00",
        [typeof(DateTimeOffset)] = "0001-01-01 00:00:00+00"
    };

    private static readonly NullabilityInfoContext NullabilityContext = new();

    /// <summary>按 C# 可空引用类型注解判断属性是否可空。</summary>
    private static bool IsPropertyNullable(PropertyInfo property)
    {
        // 值类型：Nullable<T> 才可空
        if (property.PropertyType.IsValueType)
        {
            return Nullable.GetUnderlyingType(property.PropertyType) != null;
        }

        // 引用类型：看 ? 注解
        try
        {
            return NullabilityContext.Create(property).WriteState == NullabilityState.Nullable;
        }
        catch
        {
            // 泛型等场景下取不到注解时，保守地建为可空列
            return true;
        }
    }

    /// <summary>
    /// 字符串列的建表约定（列名此时已是 snake_case）。
    /// 之所以用约定而不是特性：领域层要保持对 ORM 无感知。
    /// </summary>
    private static void ApplyStringColumnConvention(EntityColumnInfo column)
    {
        // 显式指定过类型或长度就不覆盖
        if (!string.IsNullOrEmpty(column.DataType) || column.Length > 0)
        {
            return;
        }

        var name = column.DbColumnName;

        // 以 _json 结尾 → text（存放 JSON 字符串）
        //
        // 注意：这里刻意**不用 jsonb**。C# 属性是 string 时，Npgsql 会把参数按 text 发送，
        // 而 jsonb 列不接受隐式 text→jsonb 转换，插入时会报
        //   42804: column "ext_json" is of type jsonb but expression is of type text
        // 若将来确实需要 jsonb 的索引与运算符，应把实体属性改成强类型对象
        // （SqlSugar 会按 JSON 序列化并绑定 jsonb），而不是用 string 硬塞。
        if (name.EndsWith("_json", StringComparison.Ordinal))
        {
            column.DataType = "text";
            return;
        }

        // 备注、描述类 → text
        if (TextColumnNames.Contains(name))
        {
            column.DataType = "text";
            return;
        }

        // 路径、URL、编码、名称类 → 512
        if (LongColumnSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)))
        {
            column.Length = 512;
            return;
        }

        // 其余字符串 → 256
        column.Length = 256;
    }

    private string GetConnectionString()
    {
        if (_connectionString != null)
        {
            return _connectionString;
        }

        lock (_sync)
        {
            if (_connectionString != null)
            {
                return _connectionString;
            }

            var name = _options.ConnectionStringName;
            var cs = _configuration.GetConnectionString(name);
            if (cs.IsNullOrWhiteSpace())
            {
                throw new InvalidOperationException(
                    $"未找到名为 “{name}” 的连接串，请检查 appsettings.json 的 ConnectionStrings 节点。");
            }

            _connectionString = cs!;
            return _connectionString;
        }
    }
}
