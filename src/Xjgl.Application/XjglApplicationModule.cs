using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Account;
using Volo.Abp.FeatureManagement;
using Volo.Abp.Identity;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.SettingManagement;
using Volo.Abp.TenantManagement;
using Volo.Abp.AutoMapper;
using Xjgl.Changes;
using Xjgl.Exporting;
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
        RegisterExportArtifactStore(context.Services);
        RegisterChangeAttachmentStore(context.Services);
    }

    /// <summary>
    /// 附件根目录**在启动时就创建出来**，建不出来就让启动失败。
    ///
    /// 与照片存储**刻意相反**：照片可以在 NAS 未就绪时先落到本地降级区、等补偿回放；
    /// 附件没有那条后路（补偿任务只扫照片表，见 <see cref="IChangeAttachmentStore"/>），
    /// 所以"目录建不出来"只能意味着部署是错的（卷没挂上、路径写错、权限不足）。
    /// 这时宁可**起不来**，也不要等教师传完一学期的材料才发现全都写不进去。
    ///
    /// 放在应用层模块而不是 Host：集成测试用的也是这个模块，
    /// 装配与"启动即校验"只有一份，不会出现"生产配了、测试忘了配"的偏差。
    /// </summary>
    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        context.ServiceProvider.GetRequiredService<IChangeAttachmentStore>();
    }

    /// <summary>
    /// 注册异动附件的存储。
    ///
    /// 与导出产物同样注册在应用层模块（理由见 <see cref="RegisterExportArtifactStore"/>）。
    /// 默认落在**内容根**下的 App_Data/change-attachments，而不是当前工作目录：
    /// 容器里 WORKDIR 与程序目录往往不是同一个，用当前目录会把附件写到一个
    /// "看不见、也挂不进卷"的地方——那正是"传上去了但共享卷里找不到"的成因。
    /// </summary>
    private static void RegisterChangeAttachmentStore(IServiceCollection services)
    {
        services.AddSingleton<IChangeAttachmentStore>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();

            var root = configuration["Xjgl:Change:AttachmentRootPath"];

            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(AppContext.BaseDirectory, "App_Data", "change-attachments");
            }

            return new FileSystemChangeAttachmentStore(root);
        });

        // 附件服务不继承 IApplicationService（入参含 Stream），不会被约定式注册捕捉
        services.AddTransient<IChangeAttachmentAppService, ChangeAttachmentAppService>();
    }

    /// <summary>
    /// 注册导出/打印产物的存储。
    ///
    /// 与照片存储同样注册在应用层模块：集成测试用的也是这个模块，
    /// 装配代码只有一份，不会出现"生产配了、测试忘了配"的偏差。
    /// Host 只需要通过配置提供目录。
    ///
    /// 根目录默认落在**内容根**下的 App_Data/exports，而不是
    /// <c>Directory.GetCurrentDirectory()</c>：容器里 WORKDIR 与程序目录
    /// 往往不是同一个（见 Program.cs 里把 ContentRoot 钉在程序所在目录的原因），
    /// 用当前目录会让产物写到一个"看不见、也挂不进卷"的地方，
    /// 表现为"任务成功但下载 404"。
    /// </summary>
    private static void RegisterExportArtifactStore(IServiceCollection services)
    {
        services.AddSingleton<IExportArtifactStore>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();

            var root = configuration["Xjgl:Export:ArtifactRoot"];

            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(AppContext.BaseDirectory, "App_Data", "exports");
            }

            return new FileSystemExportArtifactStore(root);
        });
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

        // 缩略图补齐同样有两个调用方（Host 的周期任务 + 将来的运维手工入口），
        // 显式注册而不是只靠 ITransientDependency 约定，与补偿服务保持一致
        services.AddTransient<IPhotoThumbnailService, PhotoThumbnailService>();
    }
}
