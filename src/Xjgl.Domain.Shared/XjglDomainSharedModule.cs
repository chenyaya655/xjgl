using Volo.Abp.AuditLogging;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.FeatureManagement;
using Volo.Abp.Identity;
using Volo.Abp.Localization;
using Volo.Abp.Localization.ExceptionHandling;
using Volo.Abp.Modularity;
using Volo.Abp.OpenIddict;
using Volo.Abp.PermissionManagement;
using Volo.Abp.SettingManagement;
using Volo.Abp.TenantManagement;
using Volo.Abp.Validation;
using Volo.Abp.Validation.Localization;
using Volo.Abp.VirtualFileSystem;
using Xjgl.Localization;

namespace Xjgl;

/// <summary>
/// 领域共享层模块。
/// 放三类东西：常量、枚举、错误码、本地化资源、DTO 共用的校验规则。
/// 这里同时把 ABP 各框架模块的 Domain.Shared 拉进来，统一收口依赖。
/// </summary>
[DependsOn(
    typeof(AbpValidationModule),
    typeof(AbpIdentityDomainSharedModule),
    typeof(AbpPermissionManagementDomainSharedModule),
    typeof(AbpSettingManagementDomainSharedModule),
    typeof(AbpFeatureManagementDomainSharedModule),
    typeof(AbpAuditLoggingDomainSharedModule),
    typeof(AbpBackgroundJobsDomainSharedModule),
    typeof(AbpTenantManagementDomainSharedModule),
    typeof(AbpOpenIddictDomainSharedModule)
)]
public class XjglDomainSharedModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            // 第二个参数是“基命名空间”：ABP 会用它在嵌入资源名里做前缀剥离。
            // 本工程 RootNamespace 是 Xjgl，因此嵌入资源名形如
            //   Xjgl.Localization.Xjgl.zh-Hans.json
            // 剥离 "Xjgl." 后正好映射到虚拟路径 /Localization/Xjgl/zh-Hans.json
            options.FileSets.AddEmbedded<XjglDomainSharedModule>("Xjgl");
        });

        Configure<AbpLocalizationOptions>(options =>
        {
            options.Resources
                .Add<XjglResource>("zh-Hans")
                .AddBaseTypes(typeof(AbpValidationResource))
                .AddVirtualJson("/Localization/Xjgl");

            options.DefaultResourceType = typeof(XjglResource);
        });

        Configure<AbpExceptionLocalizationOptions>(options =>
        {
            options.MapCodeNamespace("Xjgl", typeof(XjglResource));
        });
    }
}
