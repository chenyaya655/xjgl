using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Metadata;

/// <summary>
/// 子系统。
///
/// 三级元数据模型的第一级：子系统 → 模块 → 操作。
/// 它同时驱动四件事：前端动态菜单、前端动态路由、按钮级权限、授权界面。
///
/// 表名：sys_subsystem
/// </summary>
public class SysSubsystem : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>子系统编码，同时作为权限组名，如 Sys / Stu / Chg / Pho。</summary>
    public string Code { get; set; } = default!;

    /// <summary>子系统名称，如「学生管理」。</summary>
    public string Name { get; set; } = default!;

    /// <summary>图标（前端 Element Plus 图标名）。</summary>
    public string? Icon { get; set; }

    /// <summary>排序号。</summary>
    public int Sort { get; set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public SysSubsystem()
    {
    }

    public SysSubsystem(Guid id, string code, string name, string? icon = null, int sort = 0) : base(id)
    {
        Code = code;
        Name = name;
        Icon = icon;
        Sort = sort;
    }
}
