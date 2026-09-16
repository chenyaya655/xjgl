using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Metadata;

/// <summary>
/// 模块。三级元数据的第二级。
///
/// 表名：sys_module
///
/// <see cref="PermissionName"/> 采用 <c>{子系统编码}.{模块编码}</c>，
/// 例如 <c>Stu.Student</c>；模块下的每个操作再往后接一级。
/// </summary>
public class SysModule : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>所属子系统。</summary>
    public Guid SubsystemId { get; set; }

    /// <summary>模块编码，如 Student / ChangeApply / ChangeAudit。</summary>
    public string Code { get; set; } = default!;

    /// <summary>模块名称，如「学生管理」。</summary>
    public string Name { get; set; } = default!;

    /// <summary>前端路由路径，如 /student/list。</summary>
    public string? Path { get; set; }

    /// <summary>前端组件路径，如 student/list/index（前端用 import.meta.glob 解析）。</summary>
    public string? Component { get; set; }

    /// <summary>图标。</summary>
    public string? Icon { get; set; }

    /// <summary>排序号。</summary>
    public int Sort { get; set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>是否在菜单中隐藏（有权限但不显示，如详情页）。</summary>
    public bool IsHidden { get; set; }

    /// <summary>是否外链。</summary>
    public bool IsExternal { get; set; }

    /// <summary>权限名：{子系统编码}.{模块编码}。</summary>
    public string PermissionName { get; set; } = default!;

    /// <summary>
    /// 允许访问该模块的组织层级，逗号分隔（如 Province,City,County）。
    /// 为空表示不限。用于「异动审核只给教育局」这类场景。
    /// </summary>
    public string? OrgTypes { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public SysModule()
    {
    }

    public SysModule(
        Guid id,
        Guid subsystemId,
        string code,
        string name,
        string permissionName,
        string? path = null,
        string? component = null,
        int sort = 0) : base(id)
    {
        SubsystemId = subsystemId;
        Code = code;
        Name = name;
        PermissionName = permissionName;
        Path = path;
        Component = component;
        Sort = sort;
    }

    /// <summary>解析允许的组织层级。</summary>
    public string[] GetAllowedOrgTypes()
    {
        return string.IsNullOrWhiteSpace(OrgTypes)
            ? Array.Empty<string>()
            : OrgTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
