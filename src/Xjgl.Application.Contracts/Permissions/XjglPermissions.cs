namespace Xjgl.Permissions;

/// <summary>
/// 权限名常量（静态部分）。
///
/// 本系统最终的权限是「元数据驱动」的：子系统 / 模块 / 操作三级存在数据库里
/// （sys_subsystem / sys_module / sys_module_action），运行期由动态权限定义提供器
/// 注册到 ABP。这里定义的是框架级、不会随业务配置变化的基础权限。
///
/// 命名规范：{子系统}.{模块}.{操作}，例如 Stu.Student.Create
/// </summary>
public static class XjglPermissions
{
    public const string GroupName = "Xjgl";

    /// <summary>系统管理子系统。</summary>
    public static class System
    {
        public const string Default = GroupName + ".System";
        public const string Subsystem = Default + ".Subsystem";
        public const string Module = Default + ".Module";
        public const string Permission = Default + ".Permission";
        public const string User = Default + ".User";
        public const string Code = Default + ".Code";
        public const string Organization = Default + ".Organization";
        public const string AuditLog = Default + ".AuditLog";
    }

    /// <summary>学生管理子系统。</summary>
    public static class Student
    {
        public const string Default = GroupName + ".Student";
        public const string Manage = Default + ".Manage";
    }

    /// <summary>异动管理子系统。</summary>
    public static class Change
    {
        public const string Default = GroupName + ".Change";
        public const string Type = Default + ".Type";
        public const string Apply = Default + ".Apply";
        public const string Audit = Default + ".Audit";
    }

    /// <summary>照片管理子系统。</summary>
    public static class Photo
    {
        public const string Default = GroupName + ".Photo";
        public const string Manage = Default + ".Manage";
    }

    /// <summary>操作的通用后缀，供模块元数据生成权限名时复用。</summary>
    public static class Actions
    {
        public const string Query = "Query";
        public const string Create = "Create";
        public const string Update = "Update";
        public const string Delete = "Delete";
        public const string Import = "Import";
        public const string Export = "Export";
        public const string Print = "Print";
        public const string Approve = "Approve";
        public const string RevokeApprove = "RevokeApprove";
        public const string Upload = "Upload";
        public const string Download = "Download";
    }
}
