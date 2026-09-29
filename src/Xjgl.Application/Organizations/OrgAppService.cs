using System.Linq.Expressions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Domain.Entities;
using Xjgl.Data;
using Xjgl.Permissions;

namespace Xjgl.Organizations;

/// <summary>
/// 组织机构管理实现。
///
/// 物化路径（PathCode）是本模块的核心：取子树、判层级、做数据范围过滤
/// 都靠前缀匹配完成，不需要递归查询。
/// </summary>
[Authorize]
public class OrgAppService : XjglAppService, IOrgAppService
{
    private const string PathSeparator = ".";
    private const string DefaultSorting = "Sort ASC, Code ASC";

    private readonly IXjglRepository<SysOrg> _orgs;

    public OrgAppService(IXjglRepository<SysOrg> orgs)
    {
        _orgs = orgs;
    }

    // ------------------------------------------------------------------
    // 查询
    // ------------------------------------------------------------------

    public async Task<List<OrgDto>> GetListAsync(OrgQueryDto input)
    {
        var nodes = await QueryNodesAsync(input);

        return nodes.Select(ToDto).ToList();
    }

    public async Task<List<OrgDto>> GetTreeAsync(string? rootPathCode = null)
    {
        List<SysOrg> nodes;

        if (rootPathCode.IsNullOrWhiteSpace())
        {
            nodes = await _orgs.GetSortedListAsync(null, DefaultSorting);
        }
        else
        {
            var prefix = NormalizePrefix(rootPathCode!);
            nodes = await _orgs.GetSortedListAsync(o => o.PathCode.StartsWith(prefix), DefaultSorting);
        }

        return BuildTree(nodes.Select(ToDto).ToList());
    }

    public async Task<List<OrgTreeNodeDto>> GetChildrenAsync(Guid? parentId, bool onlyEnabled = true)
    {
        Expression<Func<SysOrg, bool>> predicate = parentId == null
            ? o => o.ParentId == null && (!onlyEnabled || o.IsEnabled)
            : o => o.ParentId == parentId.Value && (!onlyEnabled || o.IsEnabled);

        var children = await _orgs.GetSortedListAsync(predicate, DefaultSorting);

        if (children.Count == 0)
        {
            return new List<OrgTreeNodeDto>();
        }

        // 一次性查出「这些子节点的下级有哪些父节点」，避免 N+1
        var childIds = children.Select(c => c.Id).ToList();
        var grandChildren = await _orgs.GetListAsync(o => o.ParentId != null && childIds.Contains(o.ParentId.Value));
        var parentsWithChildren = grandChildren.Select(g => g.ParentId!.Value).ToHashSet();

        return children.Select(c => new OrgTreeNodeDto
        {
            Id = c.Id,
            ParentId = c.ParentId,
            OrgType = c.OrgType,
            Code = c.Code,
            Name = c.Name,
            ShortName = c.ShortName,
            PathCode = c.PathCode,
            TreeLevel = c.TreeLevel,
            Sort = c.Sort,
            IsEnabled = c.IsEnabled,
            HasChildren = parentsWithChildren.Contains(c.Id)
        }).ToList();
    }

    public async Task<OrgDto> GetAsync(Guid id)
    {
        return ToDto(await _orgs.GetAsync(id));
    }

    // ------------------------------------------------------------------
    // 写入
    // ------------------------------------------------------------------

    [Authorize(MetadataPermissions.Sys.Org.Create)]
    public async Task<OrgDto> CreateAsync(CreateUpdateOrgDto input)
    {
        var code = input.Code.Trim();

        if (!OrgTypes.All.Contains(input.OrgType))
        {
            throw new BusinessException(XjglDomainErrorCodes.DataNotFound)
                .WithData("reason", $"未知的组织类型“{input.OrgType}”");
        }

        if (await _orgs.AnyAsync(o => o.Code == code))
        {
            throw new BusinessException(XjglDomainErrorCodes.OrgCodeDuplicated).WithData("code", code);
        }

        SysOrg? parent = null;
        if (input.ParentId.HasValue)
        {
            parent = await _orgs.FindAsync(input.ParentId.Value)
                     ?? throw new BusinessException(XjglDomainErrorCodes.OrgNotFound);
        }

        // ★ 与移动共用同一条层级规则：指定了父节点就必须挂在对的那一级。
        // 少了它，"把学校直接建在省厅下"会成功，而物化路径 / TreeLevel /
        // 数据范围 / 审核级别截断都会跟着静默算错（见 EnsureHierarchy 的说明）。
        EnsureHierarchy(input.OrgType, parent);

        var entity = new SysOrg(
            GuidGenerator.Create(),
            input.OrgType,
            code,
            input.Name.Trim(),
            input.ParentId,
            input.ShortName,
            input.Sort)
        {
            IsEnabled = input.IsEnabled,
            Remark = input.Remark
        };

        entity.SetPathFrom(parent);

        await _orgs.InsertAsync(entity, autoSave: true);

        return ToDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.Org.Update)]
    public async Task<OrgDto> UpdateAsync(Guid id, CreateUpdateOrgDto input)
    {
        var entity = await _orgs.GetAsync(id);
        var code = input.Code.Trim();

        if (!string.Equals(entity.Code, code, StringComparison.Ordinal) &&
            await _orgs.AnyAsync(o => o.Code == code && o.Id != id))
        {
            throw new BusinessException(XjglDomainErrorCodes.OrgCodeDuplicated).WithData("code", code);
        }

        // 父节点变更走 MoveAsync，保证子树路径被级联重写
        if (entity.ParentId != input.ParentId)
        {
            await MoveAsync(id, new MoveOrgDto { NewParentId = input.ParentId });
            entity = await _orgs.GetAsync(id);
        }

        // 编码变更会影响自身与整棵子树的物化路径
        var oldPath = entity.PathCode;
        var codeChanged = !string.Equals(entity.Code, code, StringComparison.Ordinal);

        entity.Name = input.Name.Trim();
        entity.ShortName = input.ShortName;
        entity.Sort = input.Sort;
        entity.IsEnabled = input.IsEnabled;
        entity.Remark = input.Remark;
        entity.Code = code;

        if (codeChanged)
        {
            var parent = entity.ParentId.HasValue ? await _orgs.FindAsync(entity.ParentId.Value) : null;
            entity.SetPathFrom(parent);

            await RewriteDescendantPathsAsync(oldPath!, entity.PathCode);
        }

        await _orgs.UpdateAsync(entity, autoSave: true);

        return ToDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.Org.Delete)]
    public async Task DeleteAsync(Guid id)
    {
        var entity = await _orgs.GetAsync(id);

        if (await _orgs.AnyAsync(o => o.ParentId == id))
        {
            throw new BusinessException(XjglDomainErrorCodes.OrgHasChildren)
                .WithData("reason", "该组织下还有下级组织，请先删除下级");
        }

        // 说明：还应校验该组织下是否有学生、用户等业务数据；
        // 学生模块（P2）完成后在这里补充引用检查。

        await _orgs.DeleteAsync(entity, autoSave: true);
    }

    [Authorize(MetadataPermissions.Sys.Org.Update)]
    public async Task MoveAsync(Guid id, MoveOrgDto input)
    {
        var entity = await _orgs.GetAsync(id);

        if (input.NewParentId == id)
        {
            throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                .WithData("reason", "不能把组织挂到自己下面");
        }

        SysOrg? newParent = null;
        if (input.NewParentId.HasValue)
        {
            newParent = await _orgs.FindAsync(input.NewParentId.Value)
                        ?? throw new BusinessException(XjglDomainErrorCodes.OrgNotFound);

            // 防止把节点移进自己的子树，形成环
            if (newParent.PathCode.StartsWith(NormalizePrefix(entity.PathCode), StringComparison.Ordinal))
            {
                throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                    .WithData("reason", "不能把组织移动到它自己的下级");
            }
        }

        if (entity.ParentId == input.NewParentId)
        {
            return;
        }

        // ★ 层级校验（放在自环/子孙检查**之后**：那两条的错误信息更具体，
        //   例如"不能移动到自己的下级"比"层级不对"更能说明问题）。
        //
        // 少了这一步，"把学校挂到省厅下"这种跳级移动会**成功且不报错**：
        // 物化路径、TreeLevel、数据范围、审核级别截断都会跟着算错。
        // 前端虽然按层级灰掉了不合法的父节点，但接口是公开契约，不能只靠界面拦。
        EnsureHierarchy(entity.OrgType, newParent);

        var oldPath = entity.PathCode;
        var oldLevel = entity.TreeLevel;

        entity.ParentId = input.NewParentId;
        entity.SetPathFrom(newParent);

        await _orgs.UpdateAsync(entity, autoSave: false);

        await RewriteDescendantPathsAsync(oldPath, entity.PathCode, entity.TreeLevel - oldLevel);

        await _orgs.SaveChangesAsync();
    }

    // ------------------------------------------------------------------
    // 内部实现
    // ------------------------------------------------------------------

    /// <summary>
    /// 校验"这个类型的组织能不能挂在这个父节点下"（四级树：省 → 市 → 县 → 校）。
    ///
    /// 规则（新增与移动**共用同一条**，避免出现"新增允许、移动不允许"这种自相矛盾）：
    ///   - **没有父节点**（作为根节点）：允许任意类型。
    ///     界面上新增下级时会按层级自动推断类型，用户碰不到"省厅下面凭空建一所学校"；
    ///     而导入/测试会直接造单点数据，把它当错误挡掉只会逼出一堆无意义的脚手架。
    ///     真正要挡的是**层级挂错**，也就是下面那条。
    ///   - **有父节点**：父节点的类型必须正好是 <see cref="OrgTypes.ExpectedParentOf"/> 要求的那一级。
    ///     这条既挡住"把学校挂到省厅下"，也顺带挡住了"省厅挂在任何节点下"
    ///     （省厅的期望上级是 null，与任何实际父节点都不匹配）。
    ///
    /// 【为什么必须挡】
    /// 四级树被三处**按层级推导**的东西依赖：物化路径与 `TreeLevel`、数据范围、
    /// 异动审核级别的截断。跳级挂靠不会立刻报错，只会让这三处静默算错。
    /// </summary>
    private static void EnsureHierarchy(string orgType, SysOrg? parent)
    {
        // 作为根节点：允许（见上面的说明）
        if (parent == null)
        {
            return;
        }

        var expected = OrgTypes.ExpectedParentOf(orgType);

        if (expected == null)
        {
            throw new BusinessException(XjglDomainErrorCodes.OrgHierarchyInvalid)
                .WithData("reason",
                    $"「{OrgTypes.GetDisplayName(orgType)}」只能作为根节点，不能挂在「{parent.Name}」下面");
        }

        if (!string.Equals(parent.OrgType, expected, StringComparison.Ordinal))
        {
            throw new BusinessException(XjglDomainErrorCodes.OrgHierarchyInvalid)
                .WithData("reason",
                    $"「{OrgTypes.GetDisplayName(orgType)}」只能挂在「{OrgTypes.GetDisplayName(expected)}」下面，" +
                    $"而「{parent.Name}」是「{OrgTypes.GetDisplayName(parent.OrgType)}」");
        }
    }

    private async Task<List<SysOrg>> QueryNodesAsync(OrgQueryDto input)
    {
        var predicate = BuildPredicate(input);

        if (input.IncludeDescendants && input.ParentId.HasValue)
        {
            var parent = await _orgs.FindAsync(input.ParentId.Value);
            if (parent == null)
            {
                return new List<SysOrg>();
            }

            var prefix = NormalizePrefix(parent.PathCode);
            return await _orgs.GetSortedListAsync(
                o => o.PathCode.StartsWith(prefix) && o.Id != parent.Id, DefaultSorting);
        }

        return await _orgs.GetSortedListAsync(predicate, DefaultSorting);
    }

    private static Expression<Func<SysOrg, bool>>? BuildPredicate(OrgQueryDto input)
    {
        var hasParent = input.ParentId.HasValue;
        var hasType = !input.OrgType.IsNullOrWhiteSpace();
        var hasKeyword = !input.Keyword.IsNullOrWhiteSpace();
        var onlyEnabled = input.OnlyEnabled;

        if (!hasParent && !hasType && !hasKeyword && !onlyEnabled)
        {
            return null;
        }

        var parentId = input.ParentId;
        var orgType = input.OrgType;
        var keyword = input.Keyword?.Trim();

        return o =>
            (!hasParent || o.ParentId == parentId) &&
            (!hasType || o.OrgType == orgType) &&
            (!hasKeyword || o.Name.Contains(keyword!) || o.Code.Contains(keyword!)) &&
            (!onlyEnabled || o.IsEnabled);
    }

    /// <summary>
    /// 级联重写子树的物化路径。
    /// 移动/改编码后必须调用，否则子树的前缀查询会失效。
    /// </summary>
    private async Task RewriteDescendantPathsAsync(string oldPath, string newPath, int levelDelta = 0)
    {
        var prefix = NormalizePrefix(oldPath);

        var descendants = await _orgs.GetListAsync(o => o.PathCode.StartsWith(prefix));

        if (descendants.Count == 0)
        {
            return;
        }

        foreach (var descendant in descendants)
        {
            var suffix = descendant.PathCode[oldPath.Length..]; // 以 "." 开头
            descendant.RenamePath(newPath + suffix, descendant.TreeLevel + levelDelta);
        }

        await _orgs.UpdateManyAsync(descendants, autoSave: false);
    }

    private static string NormalizePrefix(string pathCode)
    {
        return pathCode.EndsWith(PathSeparator, StringComparison.Ordinal)
            ? pathCode
            : pathCode + PathSeparator;
    }

    private static List<OrgDto> BuildTree(List<OrgDto> all)
    {
        var byId = all.ToDictionary(o => o.Id);
        var roots = new List<OrgDto>();

        foreach (var node in all)
        {
            if (node.ParentId.HasValue && byId.TryGetValue(node.ParentId.Value, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        return roots;
    }

    private static OrgDto ToDto(SysOrg o) => new()
    {
        Id = o.Id,
        ParentId = o.ParentId,
        OrgType = o.OrgType,
        OrgTypeName = OrgTypes.GetDisplayName(o.OrgType),
        Code = o.Code,
        Name = o.Name,
        ShortName = o.ShortName,
        PathCode = o.PathCode,
        TreeLevel = o.TreeLevel,
        Sort = o.Sort,
        IsEnabled = o.IsEnabled,
        Remark = o.Remark
    };
}
