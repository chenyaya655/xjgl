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
            public const string Export = "Sys.User.Export";

            // 【这里曾经有 Import / Export 两条权限码，Import 被删除、Export 这一轮才补上实现】
            //
            // 上一轮把两条都撤掉了：它们被种进元数据、也授给了 admin，但**前后端都没有任何实现**，
            // 后果是权限管理页出现"勾了却不影响任何行为"的权限项 —— 管理员据此以为可以导入/导出用户。
            // 本轮账号清单导出（`ExportTaskKinds.UserCsv` + 「用户管理」页的导出按钮）真的做了，
            // 所以 `Export` 带着实现重新声明；`Import` **仍然没有、而且刻意不做**。
            //
            // 【为什么导入是"刻意不做"而不是"还没做"】
            //   本系统的账号来源只有一个：管理员预置名单（姓名 + 手机号 + 组织，可选身份证），
            //   本人凭「手机号 + 姓名 + 身份证 + 激活码」自助激活并自己设置密码。
            //   这条链路是"外人无法注册"的**唯一**保障（见 SysUserPredefine 的注释）。
            //   而直接导入账号 = 由管理员批量写死用户名与初始密码，等于绕过整条校验：
            //     · 身份证/手机号核对不再发生 —— 谁都能被"预置"成一个账号；
            //     · 初始口令攥在导入者手里，"管理员不知道任何人的密码"这条承诺作废。
            //   替代通道已经有了且够用：「预置与激活」页支持从 Excel 粘贴批量预置。
            //   因此**不声明 `Sys.User.Import`** —— 声明了却没有实现，正是上面刚修掉的那个错误。
        }

        public static class UserProvision
        {
            public const string Query = "Sys.UserProvision.Query";
            public const string Create = "Sys.UserProvision.Create";
            public const string Update = "Sys.UserProvision.Update";
            public const string Delete = "Sys.UserProvision.Delete";
            public const string Import = "Sys.UserProvision.Import";

            /// <summary>
            /// 预置名单导出（ROADMAP 第 17 条）。
            ///
            /// 【为什么与 Query 分开】导出的是**一份可以落盘带走的文件**（含手机号），
            /// 而"能在界面上翻着看"与"能整份拉走"不是同一件事 —— 与组织/异动导出同一条口径。
            /// 但导出实现复用了列表查询（数据范围与脱敏都只有一处），
            /// 所以 `ExportTaskPermissions` 里这个种类要求 **Export + Query 两个都有**，
            /// 否则会在内部调用处炸出 403（"能提交任务、后台却失败"，最难解释的失败方式）。
            /// </summary>
            public const string Export = "Sys.UserProvision.Export";
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
            // 微代码 / 异动 / 用户那几条后来也陆续做了（见各自的注释）；
            // 唯一**刻意不做**的是 `Sys.User.Import`，理由写在 Sys.User 里。
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
