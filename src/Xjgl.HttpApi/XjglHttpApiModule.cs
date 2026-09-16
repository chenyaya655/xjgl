using Volo.Abp.Account;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.FeatureManagement;
using Volo.Abp.Identity;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.PermissionManagement.HttpApi;
using Volo.Abp.SettingManagement;
using Volo.Abp.TenantManagement;

namespace Xjgl;

/// <summary>
/// HTTP API 层：只放 Controller，不含任何业务逻辑。
/// </summary>
[DependsOn(
    typeof(XjglApplicationContractsModule),
    typeof(AbpAspNetCoreMvcModule),
    typeof(AbpAccountHttpApiModule),
    typeof(AbpIdentityHttpApiModule),
    typeof(AbpPermissionManagementHttpApiModule),
    typeof(AbpSettingManagementHttpApiModule),
    typeof(AbpFeatureManagementHttpApiModule),
    typeof(AbpTenantManagementHttpApiModule)
)]
public class XjglHttpApiModule : AbpModule
{
    // 约定式 Controller 的注册放在宿主里做（宿主才引用了 Application 程序集），
    // 见 XjglHttpApiHostModule.ConfigureConventionalControllers
}
