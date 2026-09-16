using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Account;
using Volo.Abp.FeatureManagement;
using Volo.Abp.Identity;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.SettingManagement;
using Volo.Abp.TenantManagement;
using Volo.Abp.AutoMapper;
using Xjgl.Photos;

namespace Xjgl;

/// <summary>
/// 应用服务实现层。
/// </summary>
[DependsOn(
    typeof(XjglDomainModule),
    typeof(XjglApplicationContractsModule),
    typeof(AbpAutoMapperModule),
    typeof(AbpAccountApplicationModule),
    typeof(AbpIdentityApplicationModule),
    typeof(AbpPermissionManagementApplicationModule),
    typeof(AbpSettingManagementApplicationModule),
    typeof(AbpFeatureManagementApplicationModule),
    typeof(AbpTenantManagementApplicationModule)
)]
public class XjglApplicationModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpAutoMapperOptions>(options =>
        {
            options.AddMaps<XjglApplicationModule>(validate: true);
        });

        RegisterPhotoStorage(context.Services);
    }

    /// <summary>
    /// 注册照片存储链路：NAS 主存储 → 本地降级存储，外面套一层熔断/舱壁/超时。
    ///
    /// 为什么注册在应用层模块而不是 Host：
    ///   集成测试用的也是这个模块，注册在这里，测试与生产走的是**同一套**装配代码，
    ///   不会出现「生产配了熔断、测试忘了配」的偏差。
    ///   Host 只需要通过配置提供 NAS 挂载路径即可。
    /// </summary>
    private static void RegisterPhotoStorage(IServiceCollection services)
    {
        // 主存储：NAS 挂载点。默认值只是让开发机也能跑起来，生产必须在配置里指定。
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();

            var root = configuration["Xjgl:Photo:NasRoot"];

            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "photos");
            }

            return new NasPhotoStorage(root);
        });

        // 降级存储：本机磁盘。NAS 熔断期间照片先落这里。
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();

            var root = configuration["Xjgl:Photo:FallbackRoot"];

            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "photos-fallback");
            }

            return new LocalFallbackPhotoStorage(root);
        });

        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();

            var options = new PhotoCircuitBreakerOptions
            {
                FailureRateThreshold = configuration.GetValue("Xjgl:Photo:FailureRateThreshold", 0.5),
                MinimumSamples = configuration.GetValue("Xjgl:Photo:MinimumSamples", 20),
                OpenDuration = TimeSpan.FromSeconds(configuration.GetValue("Xjgl:Photo:OpenSeconds", 30))
            };

            return new PhotoCircuitBreaker(options);
        });

        // 具体类型单独注册一份：健康度探针与补偿回放都要**绕过熔断器**
        // 直接打主/降级存储本体，否则熔断打开后会永远探不出主存储已恢复。
        services.AddSingleton(sp => new ResilientPhotoStorage(
            sp.GetRequiredService<NasPhotoStorage>(),
            sp.GetRequiredService<LocalFallbackPhotoStorage>(),
            sp.GetRequiredService<PhotoCircuitBreaker>(),
            sp.GetRequiredService<IConfiguration>().GetValue("Xjgl:Photo:BulkheadSize", 8),
            TimeSpan.FromSeconds(sp.GetRequiredService<IConfiguration>().GetValue("Xjgl:Photo:TimeoutSeconds", 5))));

        services.AddSingleton<IPhotoStorage>(sp => sp.GetRequiredService<ResilientPhotoStorage>());

        // 上传服务不继承 IApplicationService（入参含 Stream），不会被约定式注册捕捉
        services.AddTransient<IPhotoUploadService, PhotoUploadService>();
        services.AddTransient<IPhotoCompensationService, PhotoCompensationService>();
    }
}
