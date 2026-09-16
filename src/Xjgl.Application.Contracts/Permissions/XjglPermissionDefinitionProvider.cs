using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Localization;
using Xjgl.Localization;

namespace Xjgl.Permissions;

/// <summary>
/// 静态权限定义。
/// 业务模块的权限（学生管理、异动申请、异动审核、照片管理等）由数据库元数据
/// 动态注册，见后续的动态权限提供器；这里先固定框架级权限，保证系统能跑起来。
/// </summary>
public class XjglPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var system = context.AddGroup(XjglPermissions.System.Default, L("Permission:System"));
        system.AddPermission(XjglPermissions.System.Subsystem, L("Permission:Subsystem"));
        system.AddPermission(XjglPermissions.System.Module, L("Permission:Module"));
        system.AddPermission(XjglPermissions.System.Permission, L("Permission:Permission"));
        system.AddPermission(XjglPermissions.System.User, L("Permission:User"));
        system.AddPermission(XjglPermissions.System.Code, L("Permission:Code"));
        system.AddPermission(XjglPermissions.System.Organization, L("Permission:Org"));

        var student = context.AddGroup(XjglPermissions.Student.Default, L("Menu:Student"));
        student.AddPermission(XjglPermissions.Student.Manage, L("Permission:Student"));

        var change = context.AddGroup(XjglPermissions.Change.Default, L("Menu:Change"));
        change.AddPermission(XjglPermissions.Change.Type, L("Permission:ChangeType"));
        change.AddPermission(XjglPermissions.Change.Apply, L("Permission:ChangeApply"));
        change.AddPermission(XjglPermissions.Change.Audit, L("Permission:ChangeAudit"));

        var photo = context.AddGroup(XjglPermissions.Photo.Default, L("Menu:Photo"));
        photo.AddPermission(XjglPermissions.Photo.Manage, L("Permission:Photo"));
    }

    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<XjglResource>(name);
    }
}
