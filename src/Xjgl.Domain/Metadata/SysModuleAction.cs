using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Metadata;

/// <summary>
/// 操作类型常量。与微代码 <c>ACTION_TYPE</c> 的码值保持一致。
/// </summary>
public static class ModuleActionTypes
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

    public static string GetDisplayName(string actionType) => actionType switch
    {
        Query => "查看",
        Create => "添加",
        Update => "修改",
        Delete => "删除",
        Import => "导入",
        Export => "导出",
        Print => "打印",
        Approve => "审核",
        RevokeApprove => "撤销审核",
        Upload => "上传",
        Download => "下载",
        _ => actionType
    };
}

/// <summary>
/// 模块操作。三级元数据的第三级，也是权限的最小粒度（按钮级）。
///
/// 表名：sys_module_action
///
/// <see cref="PermissionName"/> 采用 <c>{子系统编码}.{模块编码}.{操作编码}</c>，
/// 例如 <c>Stu.Student.Create</c>、<c>Chg.Audit.Approve</c>。
/// </summary>
public class SysModuleAction : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>所属模块。</summary>
    public Guid ModuleId { get; set; }

    /// <summary>操作编码，如 Query / Create / Approve。</summary>
    public string Code { get; set; } = default!;

    /// <summary>操作名称，如「查看」「审核」。</summary>
    public string Name { get; set; } = default!;

    /// <summary>操作类型，取值见 <see cref="ModuleActionTypes"/>。</summary>
    public string ActionType { get; set; } = default!;

    /// <summary>权限名：{子系统编码}.{模块编码}.{操作编码}。</summary>
    public string PermissionName { get; set; } = default!;

    /// <summary>排序号。</summary>
    public int Sort { get; set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>是否默认授予新建的角色（用于「查看」这类基础操作）。</summary>
    public bool IsDefaultGranted { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public SysModuleAction()
    {
    }

    public SysModuleAction(
        Guid id,
        Guid moduleId,
        string code,
        string name,
        string actionType,
        string permissionName,
        int sort = 0) : base(id)
    {
        ModuleId = moduleId;
        Code = code;
        Name = name;
        ActionType = actionType;
        PermissionName = permissionName;
        Sort = sort;
    }
}
