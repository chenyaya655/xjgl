using Volo.Abp.Autofac;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Modularity;
using Xjgl.EntityFrameworkCore;
using Xjgl.SqlSugarCore;

namespace Xjgl;

[DependsOn(
    typeof(AbpAutofacModule),
    typeof(XjglApplicationContractsModule),
    typeof(XjglEntityFrameworkCoreModule),
    typeof(XjglSqlSugarCoreModule)
)]
public class XjglDbMigratorModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // 迁移工具不执行后台作业
        Configure<AbpBackgroundJobOptions>(options =>
        {
            options.IsJobExecutionEnabled = false;
        });
    }
}
