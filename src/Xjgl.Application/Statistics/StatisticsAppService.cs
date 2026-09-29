using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Permissions;
using Xjgl.Statistics;

namespace Xjgl.Statistics;

/// <summary>
/// 统计报表实现。
///
/// 【本类只做三件事】
///   1. 认权限（<c>Rpt.*</c>，与列表接口分开）；
///   2. 算出**有效数据范围路径**（统计绝不能比列表看到更多）；
///   3. 把聚合交给 <see cref="IStatisticsQuery"/>（SqlSugar 的 GROUP BY）。
/// 聚合与范围过滤本身不在这里 —— 一行 SQL 都不该出现在应用层。
/// </summary>
[Authorize]
public class StatisticsAppService : XjglAppService, IStatisticsAppService
{
    /// <summary>与 <c>SqlSugarStatisticsQuery.MaxGroups</c> 保持一致：凑满即视为被截断。</summary>
    private const int MaxGroups = 200;

    private readonly IStatisticsQuery _statistics;
    private readonly IUserDataScopeProvider _dataScope;
    private readonly IXjglRepository<SysOrg> _orgs;

    public StatisticsAppService(
        IStatisticsQuery statistics,
        IUserDataScopeProvider dataScope,
        IXjglRepository<SysOrg> orgs)
    {
        _statistics = statistics;
        _dataScope = dataScope;
        _orgs = orgs;
    }

    /// <inheritdoc />
    [Authorize(MetadataPermissions.Rpt.Student.Query)]
    public async Task<StatisticResultDto> GetStudentStatisticsAsync(StudentStatisticsQueryDto input)
    {
        if (!StudentStatDimensions.IsKnown(input.Dimension))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason",
                    $"未知的统计维度「{input.Dimension}」。可用维度：{string.Join("、", StudentStatDimensions.All)}。");
        }

        // 归一化到规范常量：IsKnown 是大小写不敏感的，但 switch 不是 ——
        // 直接 ToLowerInvariant() 会把 studentStatus 变成 studentstatus 而对不上分支。
        var dimension = StudentStatDimensions.All
            .First(d => string.Equals(d, input.Dimension, StringComparison.OrdinalIgnoreCase));

        var (scopePath, scopeText) = await ResolveScopeAsync(input.SchoolOrgId);

        if (scopePath == NoAccess)
        {
            return Empty(dimension, scopeText);
        }

        var items = await _statistics.CountStudentsAsync(
            dimension,
            scopePath,
            new StudentStatFilter
            {
                IncludeDescendantOrgs = input.IncludeDescendantOrgs,
                AcademicYear = input.AcademicYear,
                GradeId = input.GradeId,
                ClassId = input.ClassId,
                StudentStatusCode = input.StudentStatusCode,
                GenderCode = input.GenderCode
            });

        return ToResult(input.Dimension, items, DescribeScope(scopeText, input.IncludeDescendantOrgs));
    }

    /// <inheritdoc />
    [Authorize(MetadataPermissions.Rpt.Change.Query)]
    public async Task<StatisticResultDto> GetChangeStatisticsAsync(ChangeStatisticsQueryDto input)
    {
        if (!ChangeStatDimensions.IsKnown(input.Dimension))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason",
                    $"未知的统计维度「{input.Dimension}」。可用维度：{string.Join("、", ChangeStatDimensions.All)}。");
        }

        var dimension = ChangeStatDimensions.All
            .First(d => string.Equals(d, input.Dimension, StringComparison.OrdinalIgnoreCase));

        var (scopePath, scopeText) = await ResolveScopeAsync(input.SchoolOrgId);

        if (scopePath == NoAccess)
        {
            return Empty(dimension, scopeText);
        }

        var items = await _statistics.CountChangesAsync(
            dimension,
            scopePath,
            new ChangeStatFilter
            {
                IncludeDescendantOrgs = input.IncludeDescendantOrgs,
                ChangeTypeId = input.ChangeTypeId,
                Status = input.Status,
                From = input.From?.Date,
                // 上界补到当天 23:59:59：用户选"到 3 月 31 日"时，
                // 用 3 月 31 日 00:00 比较会把当天申请全部漏掉 —— 而界面上写着"含"
                To = input.To?.Date.AddDays(1).AddSeconds(-1)
            });

        return ToResult(input.Dimension, items, DescribeScope(scopeText, input.IncludeDescendantOrgs));
    }

    /// <summary>
    /// 把"是否含下级"补进范围说明。
    ///
    /// 必须补：同一个学校 Id，"含下级"与"仅本级"得到的是**不同口径的数字**
    /// （前者含它下面所有学校），而界面上如果两种情况的说明文字一样，
    /// 使用者就无法解释"为什么我关掉开关数字变了"。
    /// </summary>
    private static string DescribeScope(string scopeText, bool includeDescendants) =>
        includeDescendants ? scopeText : scopeText + "；仅本级（不含下级组织）";

    /// <summary>
    /// 表示"什么都看不到"的哨兵值。
    ///
    /// 不能直接用 <c>null</c> 表示它：<c>null</c> 在 <see cref="IStatisticsQuery"/> 里的语义是
    /// **不受限**。两者混用会让"未关联组织的用户"反而看到全省数据 ——
    /// 这是数据范围设计里唯一不能搞错的方向，所以用一个不可能与真实路径冲突的常量显式区分。
    /// </summary>
    private const string NoAccess = "__NO_ACCESS__";

    /// <summary>
    /// 算出本次统计的有效范围路径。
    ///
    /// 规则（与 <c>UserDataScope.AllowsRequestedOrgPath</c> 同一取舍）：
    ///   - 不受限（省厅/admin）：用请求指定的组织；没指定则 null（= 全部）；
    ///   - 有范围的用户：请求的是自身或后代 → 用请求的；请求的是**上级** → 收敛回自己的范围；
    ///     请求的与自己的范围**无交集** → 返回"无权限"哨兵（不报错，返回空统计）。
    ///
    /// 为什么"请求上级"要收敛而不是拒绝：县区管理员看"本市汇总"是正常用法，
    /// 但能看到的仍然只能是本县的数据 —— 收敛成自己的路径即可，不必报错。
    ///
    /// ⚠ 注意这里**不处理"是否含下级"**：那是 <c>IncludeDescendantOrgs</c> 的事，
    /// 由它决定 SQL 用前缀匹配还是等值匹配。早期版本在这里写了一行
    /// `includeDescendants ? org.PathCode : org.PathCode`（两个分支一样），
    /// 看起来像处理了，实际是**参数被整个丢掉** —— 界面上关掉"含下级"后
    /// 返回的仍是整棵子树。三元两边一样的写法本身就是"没实现"的信号，
    /// 不要写。
    /// </summary>
    private async Task<(string? Path, string Text)> ResolveScopeAsync(Guid? requestedOrgId)
    {
        var scope = await _dataScope.GetAsync();

        if (scope.IsEmpty)
        {
            return (NoAccess, "未关联组织，看不到任何数据");
        }

        string? requestedPath = null;

        if (requestedOrgId.HasValue)
        {
            var org = await _orgs.FindAsync(requestedOrgId.Value);

            if (org == null)
            {
                // 组织不存在：与"范围内没有数据"表现一致，返回空而不是报错
                return (NoAccess, "指定的组织不存在");
            }

            requestedPath = org.PathCode;
        }

        if (scope.IsUnrestricted)
        {
            return (requestedPath, requestedPath == null ? "全省（不受限）" : "指定组织（不受限）");
        }

        var mine = scope.OrgPathCode!;

        if (requestedPath == null)
        {
            return (mine, "本级及下级");
        }

        // 请求的是我或我的后代 → 用请求的路径（本来就在我范围内）
        if (requestedPath == mine || requestedPath.StartsWith(mine + ".", StringComparison.Ordinal))
        {
            return (requestedPath, "指定组织（在我范围内）");
        }

        // 请求的是我的上级 → 收敛回我的范围。收敛而不是拒绝：这是正常用法
        if (mine.StartsWith(requestedPath + ".", StringComparison.Ordinal))
        {
            return (mine, "本级及下级（已按你的范围收敛）");
        }

        // 完全无交集：返回空统计
        return (NoAccess, "该组织不在你的数据范围内");
    }

    private static StatisticResultDto ToResult(
        string dimension,
        IReadOnlyList<StatCountItem> items,
        string scopeText)
    {
        return new StatisticResultDto
        {
            Dimension = dimension,
            Total = items.Sum(i => i.Count),
            // 凑满上限即视为可能还有更多：SQL 里是 LIMIT MaxGroups，
            // 正好返回 MaxGroups 行时无法区分"刚好这么多"与"被砍了"
            IsTruncated = items.Count >= MaxGroups,
            ScopeDescription = scopeText,
            Items = items.Select(i => new StatisticItemDto
            {
                Key = i.Key,
                Name = i.Name,
                Count = i.Count
            }).ToList()
        };
    }

    private static StatisticResultDto Empty(string dimension, string scopeText)
    {
        return new StatisticResultDto
        {
            Dimension = dimension,
            Total = 0,
            IsTruncated = false,
            ScopeDescription = scopeText,
            Items = new List<StatisticItemDto>()
        };
    }
}
