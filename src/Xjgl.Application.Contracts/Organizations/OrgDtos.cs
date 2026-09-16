using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Dtos;

namespace Xjgl.Organizations;

/// <summary>组织节点 DTO（树形，带 Children）。</summary>
public class OrgDto : EntityDto<Guid>
{
    public Guid? ParentId { get; set; }
    public string OrgType { get; set; } = default!;
    public string OrgTypeName { get; set; } = default!;
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? ShortName { get; set; }
    public string PathCode { get; set; } = default!;
    public int TreeLevel { get; set; }
    public int Sort { get; set; }
    public bool IsEnabled { get; set; }
    public string? Remark { get; set; }

    public List<OrgDto> Children { get; set; } = new();
}

/// <summary>
/// 组织树节点（轻量），供前端树选择器与级联下拉使用。
/// 大组织树（上万所学校）请配合 <c>parentId</c> 参数逐层加载。
/// </summary>
public class OrgTreeNodeDto
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string OrgType { get; set; } = default!;
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? ShortName { get; set; }
    public string PathCode { get; set; } = default!;
    public int TreeLevel { get; set; }
    public int Sort { get; set; }
    public bool IsEnabled { get; set; }

    /// <summary>是否还有下级（前端据此显示展开箭头，避免一次性加载整棵树）。</summary>
    public bool HasChildren { get; set; }

    public List<OrgTreeNodeDto> Children { get; set; } = new();
}

/// <summary>新增 / 修改组织。</summary>
public class CreateUpdateOrgDto
{
    /// <summary>父节点；新增省厅时留空。</summary>
    public Guid? ParentId { get; set; }

    [Required(ErrorMessage = "组织类型不能为空")]
    [StringLength(32)]
    public string OrgType { get; set; } = default!;

    [Required(ErrorMessage = "组织编码不能为空")]
    [StringLength(32)]
    public string Code { get; set; } = default!;

    [Required(ErrorMessage = "组织名称不能为空")]
    [StringLength(128)]
    public string Name { get; set; } = default!;

    [StringLength(64)]
    public string? ShortName { get; set; }

    public int Sort { get; set; }

    public bool IsEnabled { get; set; } = true;

    [StringLength(1024)]
    public string? Remark { get; set; }
}

/// <summary>组织查询条件。</summary>
public class OrgQueryDto
{
    public string? OrgType { get; set; }

    /// <summary>只取该节点的直接下级；为空表示取根（省厅）。</summary>
    public Guid? ParentId { get; set; }

    public string? Keyword { get; set; }

    public bool OnlyEnabled { get; set; } = true;

    /// <summary>是否返回整棵子树（数据量大时不建议）。</summary>
    public bool IncludeDescendants { get; set; }
}

/// <summary>移动节点。</summary>
public class MoveOrgDto
{
    /// <summary>新的父节点；为空表示移动到根。</summary>
    public Guid? NewParentId { get; set; }
}
