namespace Xjgl;

/// <summary>
/// 多租户开关。
/// 本系统面向河南省全省统一部署，采用「单租户 + 组织树 + 数据范围」模型，
/// 因此多租户功能整体关闭；保留 AbpTenantManagement 相关包只是为了少改动框架装配。
/// </summary>
public static class MultiTenancyConsts
{
    public const bool IsEnabled = false;
}
