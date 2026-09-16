using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;
using Volo.Abp.DependencyInjection;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// <see cref="ISqlSugarClientProvider"/> 的默认实现。
/// </summary>
public class SqlSugarClientProvider : ISqlSugarClientProvider, ISingletonDependency
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SqlSugarClientProvider> _logger;
    private readonly XjglSqlSugarCoreOptions _options;
    private readonly object _sync = new();

    private string? _connectionString;

    public SqlSugarClientProvider(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        IOptions<XjglSqlSugarCoreOptions> options,
        ILogger<SqlSugarClientProvider> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
        _options = options.Value;
    }

    public ISqlSugarClient GetClient()
    {
        // 从当前作用域解析。SqlSugarClientAccessor 注册为 Scoped，
        // 因此同一工作单元内多次调用得到同一个实例，事务状态才能连贯。
        var accessor = _serviceProvider.GetRequiredService<ISqlSugarClientAccessor>();
        return accessor.Client;
    }

    public ISqlSugarClient CreateIndependentClient()
    {
        return BuildClient();
    }

    /// <summary>由 <see cref="SqlSugarClientAccessor"/> 首次创建客户端时调用。</summary>
    internal ISqlSugarClient BuildClient()
    {
        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = GetConnectionString(),
            DbType = _options.DbType,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                // 实体 StuStudent -> 表 stu_student，与 ABP / PostgreSQL 的 snake_case 约定保持一致
                EntityNameService = (_, entityInfo) =>
                {
                    if (_options.UseSnakeCaseTableName)
                    {
                        entityInfo.DbTableName = UtilMethods.ToUnderLine(entityInfo.DbTableName);
                    }
                },
                EntityService = (property, column) =>
                {
                    // 可空引用类型的属性统一建为可空列，避免 CodeFirst 误判
                    if (!column.IsPrimarykey && IsNullable(property.PropertyType))
                    {
                        column.IsNullable = true;
                    }
                }
            }
        });

        client.Ado.CommandTimeOut = _options.CommandTimeoutSeconds;

        if (_options.EnableSqlLog)
        {
            client.Aop.OnLogExecuting = (sql, parameters) =>
            {
                _logger.LogDebug("SqlSugar SQL: {Sql}", sql);
            };
        }

        return client;
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
