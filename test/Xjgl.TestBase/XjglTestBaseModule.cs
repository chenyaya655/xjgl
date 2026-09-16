using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Autofac;
using Volo.Abp.Caching;
using Volo.Abp.Modularity;
using Volo.Abp.Security.Claims;
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
        // ★ 只绕过「方法级鉴权」，**不**替换 IPermissionChecker。
        //
        // ABP 自带的 AddAlwaysAllowAuthorization() 会同时把 IPermissionChecker
        // 换成永远返回「已授权」，那样「按权限过滤菜单」这类逻辑就完全测不出来了。
        // 这里只替换 IMethodInvocationAuthorizationService，效果是：
        //   - 应用服务上的 [Authorize(...)] 一律放行（测试不必先造授权数据）；
        //   - IPermissionChecker 仍是真实实现（MyProfile 的菜单过滤因此可被验证）。
        context.Services.Replace(ServiceDescriptor.Singleton<
            IMethodInvocationAuthorizationService,
            AlwaysAllowMethodInvocationAuthorizationService>());

        // 用可写的当前主体访问器替换 ABP 默认的线程访问器，
        // 让测试可以模拟「已登录用户」（见 FakeCurrentPrincipalAccessor）。
        // 本模块的 ConfigureServices 在依赖模块之后执行，因此后注册的实现生效。
        context.Services.AddSingleton<FakeCurrentPrincipalAccessor>();
        context.Services.AddSingleton<ICurrentPrincipalAccessor>(
            sp => sp.GetRequiredService<FakeCurrentPrincipalAccessor>());

        Configure<AbpDistributedCacheOptions>(options =>
        {
            options.KeyPrefix = "XjglTest:";
            options.HideErrors = true;
        });
    }
}
