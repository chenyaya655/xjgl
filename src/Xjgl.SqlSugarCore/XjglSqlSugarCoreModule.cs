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

        // Scoped：同一工作单元内复用同一个 SqlSugar 客户端，事务状态才能连贯
        context.Services.AddScoped<ISqlSugarClientAccessor, SqlSugarClientAccessor>();
    }
}
