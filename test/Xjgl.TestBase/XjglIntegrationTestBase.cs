using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Modularity;
using Volo.Abp.Testing;

namespace Xjgl;

/// <summary>
/// 集成测试基类。
/// 继承它的测试类可以直接 <c>GetRequiredService&lt;T&gt;()</c> 拿到应用服务，
/// 数据访问由 ABP 的工作单元自动管理。
///
/// **默认以超级管理员身份运行**：绝大多数测试只关心业务行为（能不能建学生、
/// 流程走得对不对），不关心"当前是谁"。而数据范围一开启，
/// 没有身份的调用会解析不出组织、进而什么都看不到 ——
/// 那会让所有与身份无关的用例一起失败，掩盖真正的回归。
/// 需要差异身份的测试（如数据范围）自己在用例里覆盖即可。
/// </summary>
public abstract class XjglIntegrationTestBase : AbpIntegratedTest<XjglTestBaseModule>
{
    protected XjglIntegrationTestBase()
    {
        GetRequiredService<FakeCurrentPrincipalAccessor>()
            .SetUser(Guid.NewGuid(), "test-admin", "admin");
    }

    protected override void SetAbpApplicationCreationOptions(AbpApplicationCreationOptions options)
    {
        options.UseAutofac();
    }

    protected override void BeforeAddApplication(IServiceCollection services)
    {
        services.ReplaceConfiguration(XjglTestConfiguration.Build());
    }
}
