namespace Xjgl.Permissions;

/// <summary>
/// 元数据驱动的权限名常量。
///
/// 与数据库里的 <c>sys_module_action.permission_name</c> 一一对应，
/// 规则是 <c>{子系统编码}.{模块编码}.{操作编码}</c>。
///
/// 为什么要有这个类：应用服务上的 <c>[Authorize]</c> 需要编译期常量，
/// 而权限本身是运行期由元数据生成的。把用到的权限码在这里固化下来，
/// 既保证编译期安全，又能与「模块管理」界面里看到的权限对齐。
///
/// 约定：**新增业务模块时，权限码必须同时出现在这里和种子数据里**，
/// 否则 <c>[Authorize]</c> 会引用一个不存在的权限（表现为所有人都被拒绝）。
/// </summary>
public static class MetadataPermissions
{
    /// <summary>系统管理子系统。</summary>
    public static class Sys
    {
        public static class Module
        {
            public const string Query = "Sys.Module.Query";
            public const string Create = "Sys.Module.Create";
            public const string Update = "Sys.Module.Update";
            public const string Delete = "Sys.Module.Delete";
        }

        public static class Permission
        {
            public const string Query = "Sys.Permission.Query";
            public const string Assign = "Sys.Permission.Assign";
        }

        public static class User
        {
            public const string Query = "Sys.User.Query";
            public const string Create = "Sys.User.Create";
            public const string Update = "Sys.User.Update";
            public const string Delete = "Sys.User.Delete";
            public const string Import = "Sys.User.Import";
            public const string Export = "Sys.User.Export";
        }

        public static class UserProvision
        {
            public const string Query = "Sys.UserProvision.Query";
            public const string Create = "Sys.UserProvision.Create";
            public const string Update = "Sys.UserProvision.Update";
            public const string Delete = "Sys.UserProvision.Delete";
            public const string Import = "Sys.UserProvision.Import";
        }

        public static class Code
        {
            public const string Query = "Sys.Code.Query";
            public const string Create = "Sys.Code.Create";
            public const string Update = "Sys.Code.Update";
            public const string Delete = "Sys.Code.Delete";
            public const string Import = "Sys.Code.Import";
            public const string Export = "Sys.Code.Export";
        }

        public static class Org
        {
            public const string Query = "Sys.Org.Query";
            public const string Create = "Sys.Org.Create";
            public const string Update = "Sys.Org.Update";
            public const string Delete = "Sys.Org.Delete";
            public const string Import = "Sys.Org.Import";
            public const string Export = "Sys.Org.Export";
        }
    }

    /// <summary>学生管理子系统。</summary>
    public static class Stu
    {
        public static class Student
        {
            public const string Query = "Stu.Student.Query";
            public const string Create = "Stu.Student.Create";
            public const string Update = "Stu.Student.Update";
            public const string Delete = "Stu.Student.Delete";
            public const string Import = "Stu.Student.Import";
            public const string Export = "Stu.Student.Export";
            public const string Print = "Stu.Student.Print";
        }
    }

    /// <summary>异动管理子系统。</summary>
    public static class Chg
    {
        public static class Type
        {
            public const string Query = "Chg.Type.Query";
            public const string Create = "Chg.Type.Create";
            public const string Update = "Chg.Type.Update";
            public const string Delete = "Chg.Type.Delete";
        }

        public static class Apply
        {
            public const string Query = "Chg.Apply.Query";
            public const string Create = "Chg.Apply.Create";
            public const string Update = "Chg.Apply.Update";
            public const string Delete = "Chg.Apply.Delete";
            public const string Export = "Chg.Apply.Export";
        }

        public static class Audit
        {
            public const string Query = "Chg.Audit.Query";
            public const string Approve = "Chg.Audit.Approve";
            public const string RevokeApprove = "Chg.Audit.RevokeApprove";
            public const string Export = "Chg.Audit.Export";
        }
    }

    /// <summary>照片管理子系统。</summary>
    public static class Pho
    {
        public static class Photo
        {
            public const string Query = "Pho.Photo.Query";
            public const string Upload = "Pho.Photo.Upload";
            public const string Delete = "Pho.Photo.Delete";
            public const string Export = "Pho.Photo.Export";
        }
    }
}
