using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.DependencyInjection;
using Xjgl.Data;

namespace Xjgl.EntityFrameworkCore;

/// <summary>
/// 框架表的 EF Core 迁移器（Order = 10，先于业务表建表）。
/// </summary>
public class EntityFrameworkCoreXjglDbSchemaMigrator : IXjglDbSchemaMigrator, ITransientDependency
{
    public int Order => 10;

    private readonly IServiceProvider _serviceProvider;

    public EntityFrameworkCoreXjglDbSchemaMigrator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task MigrateAsync()
    {
        // 从容器解析（而不是直接注入），以便拿到当前作用域下正确的连接串
        await _serviceProvider
            .GetRequiredService<XjglDbContext>()
            .Database
            .MigrateAsync();
    }
}
