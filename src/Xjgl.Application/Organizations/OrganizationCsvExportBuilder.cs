using Volo.Abp.DependencyInjection;
using Xjgl.Data;
using Xjgl.Text;

namespace Xjgl.Organizations;

/// <summary>组织导出的结果。</summary>
public sealed record OrganizationCsvExportResult(string Csv, int RowCount);

/// <summary>
/// 组织（省 / 市 / 县 / 校）CSV 导出（ROADMAP 第 17 条）。
///
/// 【列与导入模板**完全一致**】导出的文件可以直接改一改再导入、
/// 也可以作为"换一套环境重建组织树"的搬运格式。列不一致的话，
/// 这个来回就得手工对列名，而列名对错只会表现为"缺少必需列"。
/// </summary>
public interface IOrganizationCsvExportBuilder
{
    Task<OrganizationCsvExportResult> BuildAsync(OrgQueryDto query, CancellationToken cancellationToken = default);
}

public class OrganizationCsvExportBuilder : IOrganizationCsvExportBuilder, ITransientDependency
{
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IUserDataScopeProvider _dataScope;

    public OrganizationCsvExportBuilder(IXjglRepository<SysOrg> orgs, IUserDataScopeProvider dataScope)
    {
        _orgs = orgs;
        _dataScope = dataScope;
    }

    public async Task<OrganizationCsvExportResult> BuildAsync(
        OrgQueryDto query,
        CancellationToken cancellationToken = default)
    {
        var all = await _orgs.GetSortedListAsync(null, "PathCode ASC");

        // 编码 → 编码的映射用**全量**建（不是用筛出来的那批）：
        // 否则一个县区管理员导出时，自己那一级往上的"上级组织编码"会全部变成空 ——
        // 而那份文件拿去重建时，这些组织就会被当成根节点，树直接散了。
        var byId = all.ToDictionary(o => o.Id, o => o.Code);

        var scope = await _dataScope.GetAsync();

        var rows = all
            .Where(o => Matches(o, query))
            // 数据范围：导出**不比界面能看到的更多**。
            // 组织本身是参考数据（界面上学校下拉是全省的），但导出的是一份可落盘的文件，
            // 按"你有权管的范围"收口更稳妥 —— 也就不会再有人问"他能导出全省学校名录吗"。
            .Where(o => scope.IsUnrestricted || scope.AllowsEntityPath(o.PathCode))
            .ToList();

        var csv = new List<IEnumerable<string?>>
        {
            new[] { "组织类型", "组织编码", "组织名称", "上级组织编码", "简称", "排序", "是否启用" }
        };

        foreach (var org in rows)
        {
            csv.Add(new[]
            {
                // 与导入模板一致用中文类型名（OrgTypes.GetDisplayName 太长，
                // 模板示例里用的是"省厅 / 地市 / 县区 / 学校"）
                ShortTypeName(org.OrgType),
                org.Code,
                org.Name,
                org.ParentId.HasValue && byId.TryGetValue(org.ParentId.Value, out var parentCode)
                    ? parentCode
                    : string.Empty,
                org.ShortName,
                org.Sort.ToString(),
                org.IsEnabled ? "是" : "否"
            });
        }

        return new OrganizationCsvExportResult(Csv.WriteForExcel(csv), rows.Count);
    }

    /// <summary>导出时用的中文类型名，必须与导入模板接受的写法一致。</summary>
    private static string ShortTypeName(string orgType) => orgType switch
    {
        OrgTypes.Province => "省厅",
        OrgTypes.City => "地市",
        OrgTypes.County => "县区",
        _ => "学校"
    };

    /// <summary>
    /// 是否命中筛选条件。
    ///
    /// 只支持"类型 / 关键字 / 是否启用"三个条件：导出是**整体搬运**用的，
    /// 不支持按父节点导出子树 —— 子树导出出来的文件缺上级，
    /// 拿去重建时会被当成根节点（一个很难看出来的错误结果）。
    /// </summary>
    private static bool Matches(SysOrg org, OrgQueryDto query)
    {
        if (!query.OrgType.IsNullOrWhiteSpace()
            && !string.Equals(org.OrgType, query.OrgType, StringComparison.Ordinal))
        {
            return false;
        }

        if (query.OnlyEnabled && !org.IsEnabled)
        {
            return false;
        }

        if (!query.Keyword.IsNullOrWhiteSpace())
        {
            var keyword = query.Keyword!.Trim();

            if (!org.Name.Contains(keyword, StringComparison.Ordinal)
                && !org.Code.Contains(keyword, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
