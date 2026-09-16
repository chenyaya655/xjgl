using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Modularity;
using Volo.Abp.Testing;

namespace Xjgl;

/// <summary>
/// 集成测试基类。
/// 继承它的测试类可以直接 <c>GetRequiredService&lt;T&gt;()</c> 拿到应用服务，
/// 数据访问由 ABP 的工作单元自动管理。
/// </summary>
public abstract class XjglIntegrationTestBase : AbpIntegratedTest<XjglTestBaseModule>
{
    protected override void SetAbpApplicationCreationOptions(AbpApplicationCreationOptions options)
    {
        options.UseAutofac();
    }

    protected override void BeforeAddApplication(IServiceCollection services)
    {
        services.ReplaceConfiguration(XjglTestConfiguration.Build());
    }
}
