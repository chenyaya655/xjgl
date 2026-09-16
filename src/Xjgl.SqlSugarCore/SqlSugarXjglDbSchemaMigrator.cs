using System.Reflection;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Xjgl.Data;
using Xjgl.SqlSugarCore;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// 业务表的建表迁移器（SqlSugar CodeFirst）。
/// 在 EF Core 迁移之后执行（Order = 20），负责创建 / 更新业务表。
/// </summary>
public class SqlSugarXjglDbSchemaMigrator : IXjglDbSchemaMigrator, ITransientDependency
{
    /// <summary>EF Core 迁移用 10，业务表建表用 20，保证顺序。</summary>
    public int Order => 20;

    private readonly ISqlSugarClientProvider _clientProvider;
    private readonly ILogger<SqlSugarXjglDbSchemaMigrator> _logger;

    public SqlSugarXjglDbSchemaMigrator(
        ISqlSugarClientProvider clientProvider,
        ILogger<SqlSugarXjglDbSchemaMigrator> logger)
    {
        _clientProvider = clientProvider;
        _logger = logger;
    }

    public async Task MigrateAsync()
    {
        var entityTypes = DiscoverBusinessEntityTypes();
        if (entityTypes.Length == 0)
        {
            _logger.LogInformation("没有发现业务实体（IXjglBusinessEntity），跳过业务表建表。");
            return;
        }

        _logger.LogInformation("发现 {Count} 个业务实体，开始 CodeFirst 建表 ...", entityTypes.Length);

        var client = _clientProvider.CreateIndependentClient();
        try
        {
            // 数据库不存在时自动创建（PostgreSQL；已存在则忽略）
            try
            {
                client.DbMaintenance.CreateDatabase();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CreateDatabase 跳过（数据库可能已存在）。");
            }

            // InitTables 幂等：表不存在则建表，字段缺失则补字段
            client.CodeFirst.InitTables(entityTypes);

            _logger.LogInformation("业务表建表完成。");
        }
        finally
        {
            client.Dispose();
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 扫描业务实体。扫描范围由 Xjgl:SqlSugar:BusinessEntityAssemblies 配置，
    /// 未配置时默认扫描领域层程序集。
    /// </summary>
    private Type[] DiscoverBusinessEntityTypes()
    {
        var assemblies = new List<Assembly> { typeof(XjglDomainModule).Assembly };

        // 允许通过配置追加程序集（例如后续拆分业务模块时）
        // 配置形如：Xjgl:SqlSugar:BusinessEntityAssemblies:0 = Xjgl.Modules.Student
        var extra = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Xjgl.", StringComparison.Ordinal) == true)
            .ToList();

        foreach (var asm in extra)
        {
            if (!assemblies.Contains(asm))
            {
                assemblies.Add(asm);
            }
        }

        return assemblies
            .Distinct()
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericType: false })
            .Where(t => typeof(IXjglBusinessEntity).IsAssignableFrom(t))
            .Distinct()
            .OrderBy(t => t.FullName)
            .ToArray();
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }
}
