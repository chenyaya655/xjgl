using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

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

        // 说明：
        //   SqlSugarClientFactory  -> Singleton（实现 ISingletonDependency，约定式注册）
        //   SqlSugarClientAccessor -> Scoped    （实现 IScopedDependency，约定式注册）
        //   XjglRepository<T>      -> Transient （接口继承 ITransientDependency，约定式注册）
        // 都由 ABP 的约定式注册完成，这里不需要手写 AddXxx。
    }
}
