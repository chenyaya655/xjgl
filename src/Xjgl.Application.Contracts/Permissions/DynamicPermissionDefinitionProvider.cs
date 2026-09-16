using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Localization;
using Xjgl.Localization;
using Xjgl.Metadata;

namespace Xjgl.Permissions;

/// <summary>
/// 元数据驱动的权限定义提供器。
///
/// 它把 <c>sys_subsystem / sys_module / sys_module_action</c> 三级元数据
/// 注册成 ABP 的权限定义，因此：
///   - 在「模块管理」里加一个模块和它的操作，就自动多出对应权限项，**不需要改代码**；
///   - 授权界面直接就是「子系统 → 模块 → 操作」三级树；
///   - 前端按钮用同一套权限码：<c>v-permission="['Stu.Student.Create']"</c>。
///
/// 关于同步读取：ABP 的 <c>PermissionDefinitionProvider.Define</c> 是同步的，
/// 所以元数据必须走 <see cref="IModuleMetadataCache"/>（同步 + 进程内缓存）。
///
/// 关于新增权限的授权：ABP 的 Identity 种子会把**当时已定义的全部权限**授予 admin 角色。
/// 因此新增模块后重跑一次 DbMigrator，admin 就会自动获得新权限；
/// 其它角色需要在「权限管理」里手工勾选。
/// </summary>
public class DynamicPermissionDefinitionProvider : PermissionDefinitionProvider
{
    private readonly IModuleMetadataCache _cache;

    public DynamicPermissionDefinitionProvider(IModuleMetadataCache cache)
    {
        _cache = cache;
    }

    public override void Define(IPermissionDefinitionContext context)
    {
        var snapshot = _cache.GetSnapshot();

        if (snapshot.IsEmpty)
        {
            // 元数据为空（全新数据库尚未建表/尚未种子）时不注册任何动态权限，
            // 由静态权限定义兜底，保证系统仍能启动。
            return;
        }

        foreach (var subsystem in snapshot.Subsystems)
        {
            var group = context.GetGroupOrNull(subsystem.Code)
                        ?? context.AddGroup(subsystem.Code, L(subsystem.Name));

            foreach (var module in subsystem.Modules)
            {
                var modulePermission = group.GetPermissionOrNull(module.PermissionName)
                                       ?? group.AddPermission(module.PermissionName, L(module.Name));

                foreach (var action in module.Actions)
                {
                    // 全局判重：同名权限（例如与静态权限定义撞名）只注册一次
                    if (context.GetPermissionOrNull(action.PermissionName) == null)
                    {
                        modulePermission.AddChild(action.PermissionName, L(action.Name));
                    }
                }
            }
        }
    }

    /// <summary>
    /// 元数据里的名称本身就是中文显示名，这里用它作为本地化 key：
    /// 资源文件里有同名条目就用译名，没有就直接显示原名称。
    /// </summary>
    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<XjglResource>(name);
    }
}
