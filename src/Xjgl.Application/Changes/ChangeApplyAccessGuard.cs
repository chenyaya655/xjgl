using Volo.Abp;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.DependencyInjection;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Permissions;

namespace Xjgl.Changes;

/// <summary>
/// 异动申请单（及其子表：附件）的数据范围守卫。
///
/// 【为什么需要它】
/// `ChangeFlowAppService` 的读路径原来**完全没有数据范围校验**：
/// `GetAppliesAsync` 只按调用方传来的过滤条件查，`GetApplyAsync` 直接按 Id 取。
/// 于是任何一个持有 <c>Chg.Apply.Query</c> 的用户都能按 Id 读到**全省任意一张**
/// 异动申请单（学生姓名、学籍辅号、转出/转入学校、申请单号），
/// 也能在列表里看到全省的单据——这正是本项目为学生、照片、用户预置、
/// 学籍留痕都修过的那一类越权（"只要传一个别的学校的组织 Id 就能看到那个学校的学生"），
/// 而异动模块当时被漏掉了。
///
/// 与 <c>StudentAccessGuard</c> 同一做法：把这段判断集中在一个地方，
/// 因为申请单的**子表（附件）是独立路由**，不会自动继承主档的过滤条件。
///
/// 【可见性口径：申请单的哪一侧算"我的"】
/// 一张转学单同时牵涉两所学校（转出校、转入校）。这里取
/// **任一侧在范围内即可见**：转入县看不到本县要接收的学生，就没法审；
/// 转出县看不到自己送出去的学生，台账也断了。
/// 反过来只认一侧（比如只看转出校），转入县就会"连自己的待办都点不开"。
/// </summary>
public interface IChangeApplyAccessGuard
{
    /// <summary>按 Id 取申请单并校验数据范围；越权抛业务异常。</summary>
    Task<StuChangeApply> GetInScopeAsync(Guid applyId, CancellationToken cancellationToken = default);

    /// <summary>校验一条已取出的申请单是否在当前用户的数据范围内。</summary>
    Task EnsureInScopeAsync(StuChangeApply apply, CancellationToken cancellationToken = default);

    /// <summary>
    /// 当前用户数据范围覆盖的组织 Id 列表；<c>null</c> 表示**不受限**（省厅 / admin / All）。
    ///
    /// 列表查询用它做一次 <c>IN</c> 过滤。之所以能这么算：申请单上只冗余了学校的
    /// **Id**（<c>SourceSchoolOrgId</c> / <c>TargetSchoolOrgId</c>），没有冗余物化路径，
    /// 因此"我的子树"必须先翻译成组织 Id 集合。代价是范围越大这个集合越大
    /// （省厅走 <c>null</c> 分支，不受影响；县区只有几十个）。
    /// 更优的做法是给申请单冗余 <c>SourceSchoolPathCode</c> / <c>TargetSchoolPathCode</c>，
    /// 那样谓词里只剩两次前缀比较——但那需要给存量数据回填，且回填前的老单据
    /// 会静默变成"谁都看不见"，不适合与本次功能一起做。
    /// </summary>
    Task<List<Guid>?> GetVisibleOrgIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取申请单（含其证明材料）的权限：<c>Chg.Apply.Query</c> **或** <c>Chg.Audit.Query</c>，任一即可。
    ///
    /// 【为什么需要"或"这个语义】详情接口原来只认 <c>Chg.Apply.Query</c>，
    /// 而审核页（待办 → 点开一条 → 看清材料再批）走的是**同一个**详情接口。
    /// 于是一个只被授了 <c>Chg.Audit.*</c> 的审核员能看见待办列表，却**打不开任何一条**：
    /// 表现是"点详情 403"，而权限管理页上他明明有审核权限 —— 这类
    /// "勾了权限却按不出功能"的失败最难解释，因为它看起来像 bug 又找不到原因。
    /// 审核人必须先看得见材料才能批，所以读权限取"申请查询或审核查询"。
    ///
    /// ⚠ 写权限（上传 / 删除附件）**不在此列**：那仍然只认 <c>Chg.Apply.Update</c>。
    /// 审核人只读材料、不能替换别人的材料，否则"审核时看到的是什么"就没有定论了。
    /// </summary>
    Task EnsureReadPermissionAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class ChangeApplyAccessGuard : IChangeApplyAccessGuard, ITransientDependency
{
    private readonly IXjglRepository<StuChangeApply> _applies;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IUserDataScopeProvider _dataScopeProvider;
    private readonly IPermissionChecker _permissionChecker;

    public ChangeApplyAccessGuard(
        IXjglRepository<StuChangeApply> applies,
        IXjglRepository<SysOrg> orgs,
        IUserDataScopeProvider dataScopeProvider,
        IPermissionChecker permissionChecker)
    {
        _applies = applies;
        _orgs = orgs;
        _dataScopeProvider = dataScopeProvider;
        _permissionChecker = permissionChecker;
    }

    /// <inheritdoc />
    public async Task EnsureReadPermissionAsync(CancellationToken cancellationToken = default)
    {
        // 逐条判而不是用 IsGrantedAsync(string[])：后者的"任一 / 全部"语义在各版本之间
        // 并不一致（ExportTaskAppService 里就记过一次踩坑），而"任一即可"是本条的业务规则，
        // 写清楚比省一行更值。
        if (await _permissionChecker.IsGrantedAsync(MetadataPermissions.Chg.Apply.Query)
            || await _permissionChecker.IsGrantedAsync(MetadataPermissions.Chg.Audit.Query))
        {
            return;
        }

        throw new BusinessException(XjglDomainErrorCodes.PermissionNotOwned)
            .WithData("reason", "没有「异动申请查询」或「异动审核查询」权限，无法查看该申请单");
    }

    /// <inheritdoc />
    public async Task<StuChangeApply> GetInScopeAsync(
        Guid applyId,
        CancellationToken cancellationToken = default)
    {
        var apply = await _applies.GetAsync(applyId, cancellationToken);

        await EnsureInScopeAsync(apply, cancellationToken);

        return apply;
    }

    /// <inheritdoc />
    public async Task EnsureInScopeAsync(
        StuChangeApply apply,
        CancellationToken cancellationToken = default)
    {
        var scope = await _dataScopeProvider.GetAsync();

        if (scope.IsUnrestricted)
        {
            return;
        }

        // 越权访问明确抛异常而不是返回空/404：调用方需要能区分
        // 「这张单子存在但你看不到」与「这张单子不存在」
        // （与 StudentAccessGuard 同一取舍）。
        if (await IsInScopeAsync(scope, apply, cancellationToken))
        {
            return;
        }

        throw new BusinessException(XjglDomainErrorCodes.UserNotInDataScope)
            .WithData("reason", "该异动申请单不在您的数据范围内");
    }

    /// <inheritdoc />
    public async Task<List<Guid>?> GetVisibleOrgIdsAsync(CancellationToken cancellationToken = default)
    {
        var scope = await _dataScopeProvider.GetAsync();

        // 不受限：调用方按 null 处理，不加任何范围条件
        if (scope.IsUnrestricted)
        {
            return null;
        }

        // 未关联组织（IsEmpty）：**什么都看不到**，返回空集合而不是 null。
        // 这一步不能省——null 在调用方那里表示"不加过滤"。
        if (scope.IsEmpty || scope.OrgPathCode.IsNullOrWhiteSpace())
        {
            return new List<Guid>();
        }

        var (scopePath, childPrefix) = BuildScopePaths(scope);

        var orgs = await _orgs.GetListAsync(
            o => o.PathCode == scopePath || o.PathCode.StartsWith(childPrefix),
            cancellationToken);

        return orgs.Select(o => o.Id).ToList();
    }

    private async Task<bool> IsInScopeAsync(
        UserDataScope scope,
        StuChangeApply apply,
        CancellationToken cancellationToken)
    {
        // 同校异动时两侧是同一个 Id，去重后只查一次
        var schoolIds = new List<Guid> { apply.SourceSchoolOrgId };

        if (apply.TargetSchoolOrgId.HasValue && apply.TargetSchoolOrgId.Value != apply.SourceSchoolOrgId)
        {
            schoolIds.Add(apply.TargetSchoolOrgId.Value);
        }

        var schools = await _orgs.GetListAsync(o => schoolIds.Contains(o.Id), cancellationToken);

        return schools.Any(s => scope.AllowsEntityPath(s.PathCode));
    }

    /// <summary>
    /// 把生效范围折算成「本级路径」+「后代前缀」两个字符串。
    ///
    /// 「仅本级」用一个**不可能出现的字符**当前缀，于是 <c>StartsWith</c> 永不命中、
    /// 只剩"路径完全相等"生效——这样就不必在 SQL 谓词里写三元表达式
    /// （SqlSugar 会把三元翻成 <c>IIF</c>，嵌套的路径比较它翻译不了，会直接抛异常）。
    /// 与 <c>StudentAppService</c> 的取法完全一致，哨兵值也共用同一个常量。
    /// </summary>
    private static (string ScopePath, string ChildPrefix) BuildScopePaths(UserDataScope scope)
    {
        var scopePath = scope.OrgPathCode!;

        var childPrefix = scope.Scope == DataScopes.OrgOnly
            ? DataScopes.ImpossiblePathPrefix
            : scopePath + ".";

        return (scopePath, childPrefix);
    }
}
