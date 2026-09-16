using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;

namespace Xjgl.Data;

/// <summary>
/// 数据库迁移总入口：按顺序跑完所有 <see cref="IXjglDbSchemaMigrator"/>，再执行种子数据。
/// </summary>
public class XjglDbMigrationService : ITransientDependency
{
    public ILogger<XjglDbMigrationService> Logger { get; set; }

    private readonly IDataSeeder _dataSeeder;
    private readonly IEnumerable<IXjglDbSchemaMigrator> _dbSchemaMigrators;

    public XjglDbMigrationService(
        IDataSeeder dataSeeder,
        IEnumerable<IXjglDbSchemaMigrator> dbSchemaMigrators)
    {
        _dataSeeder = dataSeeder;
        _dbSchemaMigrators = dbSchemaMigrators;
        Logger = NullLogger<XjglDbMigrationService>.Instance;
    }

    public async Task MigrateAsync()
    {
        Logger.LogInformation("========== 开始数据库迁移 ==========");

        foreach (var migrator in _dbSchemaMigrators.OrderBy(m => m.Order))
        {
            Logger.LogInformation("执行迁移器：{Migrator}（Order={Order}）",
                migrator.GetType().Name, migrator.Order);
            await migrator.MigrateAsync();
        }

        Logger.LogInformation("---------- 开始初始化种子数据 ----------");
        await _dataSeeder.SeedAsync();
        Logger.LogInformation("========== 数据库迁移与种子数据初始化完成 ==========");
    }
}
