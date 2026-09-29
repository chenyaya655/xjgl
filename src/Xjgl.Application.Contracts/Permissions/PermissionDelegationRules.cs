namespace Xjgl.Permissions;

/// <summary>
/// 哪些权限**可以**被委派（ROADMAP 第 19 条 / 方案文档 §5.2 的 `IsDelegatable`）。
///
/// 【为什么用"允许清单"的思路而不是逐个标 `CanDelegate`】
/// 方案文档设想的是在权限定义上挂一个 `CanDelegate` 属性。但本项目的权限是
/// **元数据驱动**的（子系统/模块/操作三张表 + `MetadataPermissions` 常量），
/// 给每条权限再加一个字段意味着：元数据表加列、种子改一遍、界面加开关、
/// 且**新加的权限默认是"可委派"还是"不可委派"取决于写代码的人记不记得设**。
///
/// 这里反过来做：**默认不可委派，只有明确属于"业务办理"的才可委派**。
/// 方向很重要 —— 记错一个开关的代价是"把系统管理权限借出去了"，
/// 而那种错误在界面上完全看不出来（委派记录看起来一切正常）。
///
/// 判定规则（三条，足够覆盖本系统的权限结构）：
///   1. **导出类不可委派**：`*.Export` 是"把数据拉走"的能力，
///      借出去的后果是数据离库，事后无从追溯；
///   2. **系统管理类不可委派**：`Sys.*`（用户 / 角色 / 权限 / 模块 / 码表 / 预置 / 组织维护）；
///   3. **运维动作不可委派**：如照片存储补偿（`Pho.Photo.Compensate`）——
///      它是排障动作，不是业务办理。
///
/// 其余（学生录入与查询、异动申请与审核、照片查询与上传等）可委派。
/// </summary>
public static class PermissionDelegationRules
{
    /// <summary>该权限是否可以委派。</summary>
    public static bool IsDelegatable(string? permissionName)
    {
        if (string.IsNullOrWhiteSpace(permissionName))
        {
            return false;
        }

        // 1) 导出类一律不可委派
        if (permissionName.EndsWith(".Export", StringComparison.Ordinal))
        {
            return false;
        }

        // 2) 系统管理类不下放
        //    （含 Sys.Org.* 的组织维护：组织树是数据范围的锚点，改它会连带改动可见范围）
        if (permissionName.StartsWith("Sys.", StringComparison.Ordinal))
        {
            return false;
        }

        // 3) 运维动作
        if (string.Equals(permissionName, MetadataPermissions.Pho.Photo.Compensate, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>被拒绝委派时给用户看的原因（不要只说"不允许"）。</summary>
    public static string DescribeWhyNot(string? permissionName)
    {
        if (string.IsNullOrWhiteSpace(permissionName))
        {
            return "未指定权限。";
        }

        if (permissionName.EndsWith(".Export", StringComparison.Ordinal))
        {
            return "导出类权限不能委派：它等于把数据带走，事后无从追溯。";
        }

        if (permissionName.StartsWith("Sys.", StringComparison.Ordinal))
        {
            return "系统管理类权限不能委派：用户 / 角色 / 权限 / 模块 / 码表 / 组织维护属于管理员职责。";
        }

        return "该权限属于运维动作，不能委派。";
    }
}
