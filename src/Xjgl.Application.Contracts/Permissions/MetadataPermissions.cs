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

        /// <summary>
        /// 角色管理。
        ///
        /// ⚠ 这里存在**两个权限层次**，是本系统唯一一处「菜单权限」与「接口权限」分离的地方：
        ///   - 菜单能否看见 → 本组业务权限 <c>Sys.Role.*</c>（元数据定义，可正常授权与显示）；
        ///   - 接口能否调用 → ABP 内置 <c>AbpIdentity.Roles*</c>（框架控制器自带的 <c>[Authorize]</c>）。
        ///
        /// 原因见 <c>ModuleMetadataDataSeedContributor</c> 里 Role 模块的注释：
        /// 菜单过滤只认元数据快照里的权限名，而 <c>AbpIdentity.*</c> 不在快照里。
        /// 因此**授权时必须两个都给**，只给本组会出现"菜单看得见、点进去 302"。
        /// </summary>
        public static class Role
        {
            public const string Query = "Sys.Role.Query";
            public const string Create = "Sys.Role.Create";
            public const string Update = "Sys.Role.Update";
            public const string Delete = "Sys.Role.Delete";
        }

        public static class User
        {
            public const string Query = "Sys.User.Query";
            public const string Create = "Sys.User.Create";
            public const string Update = "Sys.User.Update";
            public const string Delete = "Sys.User.Delete";

            // 【这里曾经有 Import / Export 两条权限码，已删除】
            // 它们被种进元数据、也授给了 admin，但**前后端都没有任何实现**：
            // 没有接口、没有按钮。后果是权限管理页出现"勾了却不影响任何行为"的权限项 ——
            // 管理员据此以为可以导入/导出用户，实际点了什么都不会发生。
            // 本项目的约定是"不做文档/界面上看起来有、实际没做的功能"，
            // 因此先**撤掉声明**，等功能真的做了再加回来（见 ROADMAP「用户导入导出」）。
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

            // 微代码导入导出（ROADMAP 第 17 条）：与 Sys.Org.Import/Export 同一批，
            // 也是"实现了才声明"（`CodeImportExportAppService` + `ExportTaskKinds.CodeCsv`）。
            public const string Import = "Sys.Code.Import";
            public const string Export = "Sys.Code.Export";
        }

        public static class Org
        {
            public const string Query = "Sys.Org.Query";
            public const string Create = "Sys.Org.Create";
            public const string Update = "Sys.Org.Update";
            public const string Delete = "Sys.Org.Delete";

            // 组织（学校名录）的导入导出（ROADMAP 第 17 条）。
            //
            // 【为什么它们曾经被删掉又加回来】
            // 上一轮把 Import / Export 从元数据里撤掉了，因为当时**没有任何实现** ——
            // 而"勾了不影响任何行为"的权限项比没有这个权限更坏。
            // 本轮真的做出了导入（`OrganizationImportExportAppService`）与
            // 导出（`ExportTaskKinds.OrganizationCsv`），所以声明与实现重新对齐。
            // 用户 / 微代码 / 异动的导入导出仍未实现，因此**仍然没有**对应的权限码。
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

            // 申请单导出（ROADMAP 第 17 条）：实现了才声明。
            public const string Export = "Chg.Apply.Export";
        }

        public static class Audit
        {
            public const string Query = "Chg.Audit.Query";
            public const string Approve = "Chg.Audit.Approve";
            public const string RevokeApprove = "Chg.Audit.RevokeApprove";

            // 审核**流水**导出（一个审批节点一行）：与申请单导出是两份不同的产物，
            // 权限也分开 —— "能看申请单"不等于"能拉走审核台账"。
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

            /// <summary>触发「降级照片回放 NAS」的补偿操作，属于运维动作，通常只授给管理员。</summary>
            public const string Compensate = "Pho.Photo.Compensate";
        }
    }

    /// <summary>统计报表子系统。</summary>
    public static class Rpt
    {
        /// <summary>
        /// 学生统计（按学校 / 年级 / 班级 / 学籍状态 / 性别 / 学年 / 有无照片）。
        ///
        /// 刻意**不复用** <c>Stu.Student.Query</c>：能看名单不等于能看汇总 ——
        /// 汇总数字本身就是敏感信息（一个县的学生总数、异动趋势）。
        /// 但两者共用同一套数据范围，所以"能看多少"不会被统计接口放大。
        /// </summary>
        public static class Student
        {
            public const string Query = "Rpt.Student.Query";
        }

        /// <summary>异动统计（按异动类型 / 审批状态 / 月份 / 转出校 / 转入校）。</summary>
        public static class Change
        {
            public const string Query = "Rpt.Change.Query";
        }
    }
}
