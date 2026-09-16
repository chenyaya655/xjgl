using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.Caching;
using Volo.Abp.Modularity;
using Xjgl.EntityFrameworkCore;
using Xjgl.SqlSugarCore;

namespace Xjgl;

/// <summary>
/// 集成测试模块：装配真实应用服务 + 两套 ORM。
/// </summary>
[DependsOn(
    typeof(AbpAutofacModule),
    typeof(AbpTestBaseModule),
    typeof(XjglApplicationModule),
    typeof(XjglEntityFrameworkCoreModule),
    typeof(XjglSqlSugarCoreModule)
)]
public class XjglTestBaseModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // 测试聚焦业务逻辑，不做权限校验
        // （权限本身的测试留到专门的用例里，见方案第 5 章的授权约束）
        context.Services.AddAlwaysAllowAuthorization();

        Configure<AbpDistributedCacheOptions>(options =>
        {
            options.KeyPrefix = "XjglTest:";
            options.HideErrors = true;
        });
    }
}
