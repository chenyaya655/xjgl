using Volo.Abp.Application.Services;

namespace Xjgl.Organizations;

/// <summary>
/// 组织机构管理（教育局 / 学校）。
///
/// 组织树是权限与数据范围的锚点：省厅 → 市局 → 县局 → 学校。
/// 年级、班级不在这棵树里，见 <see cref="SysOrg"/> 的类注释。
/// </summary>
public interface IOrgAppService : IApplicationService
{
    /// <summary>获取组织列表（可按类型 / 父节点 / 关键字过滤，可选返回子树）。</summary>
    Task<List<OrgDto>> GetListAsync(OrgQueryDto input);

    /// <summary>获取整棵组织树（节点数量大时不建议使用）。</summary>
    Task<List<OrgDto>> GetTreeAsync(string? rootPathCode = null);

    /// <summary>逐层加载：获取某个节点下的直接子节点。</summary>
    Task<List<OrgTreeNodeDto>> GetChildrenAsync(Guid? parentId, bool onlyEnabled = true);

    Task<OrgDto> GetAsync(Guid id);

    Task<OrgDto> CreateAsync(CreateUpdateOrgDto input);

    Task<OrgDto> UpdateAsync(Guid id, CreateUpdateOrgDto input);

    Task DeleteAsync(Guid id);

    /// <summary>
    /// 移动节点到新的父节点下。
    /// 会级联重写整棵子树的物化路径。
    /// </summary>
    Task MoveAsync(Guid id, MoveOrgDto input);
}
