using Volo.Abp.Autofac;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BackgroundWorkers;
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

        // 迁移工具不跑后台工作者：它不需要（令牌清理等），
        // 且在「先建表、再种子」的流程里跑后台任务没有意义。
        //
        // 注意：全新数据库首次执行时，日志里仍会出现若干
        //   “relation "AbpPermissionGroups" does not exist” 之类的 ERR 记录。
        // 那是 ABP 的动态初始化器（StaticPermissionSaver / StaticSettingSaver /
        // StaticFeatureSaver）在建表完成前访问元数据表导致的，ABP 内部带 Polly 重试，
        // **不影响最终结果**（迁移结束时会看到 “数据库迁移与种子数据初始化完成”，
        // 表与种子数据都齐全）。生产环境建议把这些日志的级别单独调高，避免误判。
        Configure<AbpBackgroundWorkerOptions>(options =>
        {
            options.IsEnabled = false;
        });
    }
}
