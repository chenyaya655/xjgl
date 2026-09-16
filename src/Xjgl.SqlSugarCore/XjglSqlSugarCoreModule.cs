using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;
using Xjgl.Data;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// SqlSugar 集成层模块。
/// 与 <c>Xjgl.EntityFrameworkCore</c> 并列：框架表走 EF Core，业务表走 SqlSugar，
/// 两者共用同一个 PostgreSQL 数据库与同一套 ABP 工作单元。
/// </summary>
[DependsOn(typeof(XjglDomainModule))]
public class XjglSqlSugarCoreModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();

        Configure<XjglSqlSugarCoreOptions>(configuration.GetSection("Xjgl:SqlSugar"));

        // ★ 必须显式注册开放泛型仓储。
        //   ABP 的约定式注册（ConventionalRegistrar）只会为「封闭类型」按名字匹配接口，
        //   不会为 XjglRepository<TEntity> 这样的开放泛型自动建立
        //   IXjglRepository<> / ISqlSugarRepository<> 的映射；
        //   漏了这一步的表现是运行时才报：
        //     Cannot resolve parameter 'IXjglRepository`1[Xxx] xxx'
        context.Services.AddTransient(typeof(IXjglRepository<>), typeof(XjglRepository<>));
        context.Services.AddTransient(typeof(ISqlSugarRepository<>), typeof(XjglRepository<>));

        // 另外两个由约定式注册完成：
        //   SqlSugarClientFactory  -> Singleton（ISingletonDependency）
        //   SqlSugarClientAccessor -> Scoped    （IScopedDependency）
    }
}
