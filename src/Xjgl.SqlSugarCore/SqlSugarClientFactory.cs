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
        // 1) 只读属性不进数据库。
        //    典型例子是 ABP 实体基类上的 ExtraProperties（只有 getter），
        //    它由 ABP 自己序列化，不能让 SqlSugar 当成普通列。
        if (!property.CanWrite)
        {
            column.IsIgnore = true;
            return;
        }

        // 2) 列名同样转 snake_case（属性上显式写了 ColumnName 的除外）
        var explicitColumn = property.GetCustomAttribute<SugarColumn>();
        var hasExplicitName = explicitColumn is { ColumnName: not null };

        if (_options.UseSnakeCaseTableName && !hasExplicitName)
        {
            column.DbColumnName = UtilMethods.ToUnderLine(column.DbColumnName);
        }

        // 3) 主键：ABP 的主键叫 Id 且定义在框架基类里，无法打特性，这里按名字识别
        if (property.Name == nameof(IEntity<Guid>.Id))
        {
            column.IsPrimarykey = true;
            column.IsIdentity = false;
        }

        // 4) 可空引用类型统一建为可空列，避免 CodeFirst 误判
        if (!column.IsPrimarykey && IsNullable(property.PropertyType))
        {
            column.IsNullable = true;
        }

        // 5) 字符串列的类型与长度约定（领域层不带 ORM 特性，规则集中在这里）
        if (property.PropertyType == typeof(string))
        {
            ApplyStringColumnConvention(column);
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

        // 以 _json 结尾 → jsonb
        if (name.EndsWith("_json", StringComparison.Ordinal))
        {
            column.DataType = "jsonb";
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

    private static bool IsNullable(Type type)
    {
        return !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
    }
}
