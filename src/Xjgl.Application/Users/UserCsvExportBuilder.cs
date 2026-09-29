using System.Globalization;
using Microsoft.Extensions.Configuration;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Identity;
using Xjgl.Data;
using Xjgl.Exporting;
using Xjgl.Organizations;
using Xjgl.Text;

namespace Xjgl.Users;

/// <summary>账号清单导出的结果（与学生/异动导出同一形状：总数 / 实际行数 / 是否触顶）。</summary>
public sealed record UserCsvExportResult(string Csv, long Total, int RowCount, bool IsTruncated);

/// <summary>
/// 账号清单（ABP 身份表里的用户）CSV 导出（ROADMAP 第 17 条）。
///
/// 【为什么这一条和别的导出不一样：它读的是**框架表**】
///   用户/角色在 `AbpUsers` / `AbpUserRoles` 里，归 EF Core；本系统的业务表归 SqlSugar。
///   因此这里不能像组织/微代码那样走 `IXjglRepository`，只能用 ABP 的
///   <see cref="IIdentityUserRepository"/> —— 好处是"页面上能看到的用户"与
///   "导出拿到的用户"天然是同一批（页面调的 `/api/identity/users` 背后就是它）。
///
/// 【数据范围：本种类**刻意不按组织收口**】
///   「用户管理」页本身就不按数据范围过滤（它列出的是全系统的账号），
///   所以导出必须与页面对齐 —— 否则会出现"页面上看得见 200 个人、导出来只有 3 个"，
///   而用户完全不知道为什么少了。口径一句话：**能看到才能导出**。
///   （组织/微代码/异动那些导出按数据范围收口，是因为它们的列表页本身就收口。）
///
/// 【角色怎么取：一次查回来，不按人循环】
///   `IIdentityUserRepository.GetRoleNamesAsync(IEnumerable&lt;Guid&gt;)` 一次把一批
///   "用户 → 角色名"取回（返回 `IdentityUserIdWithRoleNames`），
///   逐个用户调 `GetRolesAsync` 就是 N+1：一页 1000 人 = 1000 次库往返，
///   而那正是导出最容易"看起来能跑、一上量就超时"的地方。
///   （该重载的可用签名是用编译器确认的 —— ABP 的 XML 文档没覆盖它。）
///
/// 【所属组织为什么从预置记录反查】
///   `AbpUsers` 上**没有组织字段**：本系统的组织归属是在「预置 + 本人激活」时
///   由 `SysUserPredefine.OrgId` 写回、并靠 `ActivatedUserId` 关联的
///   （见 `MyProfileAppService.FillOrganizationAsync`，那里也是反查）。
///   所以账号清单里的"所属组织"必然来自预置记录；
///   而**不是每个账号都有预置记录**（admin、演示账号、历史上直接建的账号都没有），
///   这些行的组织列**留空**而不是猜一个 —— 猜出来的归属会被当成事实来用。
/// </summary>
public interface IUserCsvExportBuilder
{
    Task<UserCsvExportResult> BuildAsync(
        UserExportQueryDto query,
        CancellationToken cancellationToken = default);
}

public class UserCsvExportBuilder : IUserCsvExportBuilder, ITransientDependency
{
    /// <summary>与其它导出一致的行数上限配置；触顶会如实写进任务提示，不静默截断。</summary>
    private const string MaxRowsConfigKey = "Xjgl:Export:MaxRows";

    private const int DefaultMaxRows = 100_000;

    /// <summary>
    /// 单页取数上限。ABP 对 `MaxResultCount` 有 1000 的硬上限，
    /// 传更大值会被参数校验直接拒掉，因此只能分页累积。
    /// </summary>
    private const int PageSize = 1000;

    private readonly IIdentityUserRepository _users;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IConfiguration _configuration;

    public UserCsvExportBuilder(
        IIdentityUserRepository users,
        IXjglRepository<SysUserPredefine> predefines,
        IXjglRepository<SysOrg> orgs,
        IConfiguration configuration)
    {
        _users = users;
        _predefines = predefines;
        _orgs = orgs;
        _configuration = configuration;
    }

    public async Task<UserCsvExportResult> BuildAsync(
        UserExportQueryDto query,
        CancellationToken cancellationToken = default)
    {
        var maxRows = Math.Max(1, _configuration.GetValue(MaxRowsConfigKey, DefaultMaxRows));

        // 与页面对齐：关键字是同一个 `filter`，排序也按用户名（页面上就是 `sorting: 'userName'`），
        // 这样"导出文件的第 N 行"与"列表第 N 行"是同一行，核对时不用来回找。
        var filter = query.Filter.IsNullOrWhiteSpace() ? null : query.Filter!.Trim();

        var users = new List<IdentityUser>();
        long total = 0;

        for (var skip = 0; skip < maxRows; skip += PageSize)
        {
            var take = Math.Min(PageSize, maxRows - users.Count);

            if (take <= 0)
            {
                break;
            }

            var page = await _users.GetListAsync(
                sorting: "userName",
                maxResultCount: take,
                skipCount: skip,
                filter: filter,
                includeDetails: false,
                cancellationToken: cancellationToken);

            users.AddRange(page);

            // 总数只在第一页时取一次：每次循环都 count 一遍是白白的全表扫描
            if (skip == 0)
            {
                total = await _users.GetCountAsync(filter);
            }

            // ★ 债 23：每取完一页上报一次进度。分母取 `min(总数, 上限)` ——
            // 这一种会被 `MaxRows` 截断，用原总数会让截断场景下的进度永远差最后几个百分点。
            await ExportProgress.ReportAsync(users.Count, Math.Min(total, maxRows), cancellationToken);

            if (page.Count < take || users.Count >= total)
            {
                break;
            }
        }

        var userIds = users.Select(u => u.Id).ToList();

        var roleNames = await LoadRoleNamesAsync(userIds, cancellationToken);
        var orgByUser = await LoadOrganizationsAsync(userIds, cancellationToken);

        var rows = new List<IEnumerable<string?>>
        {
            new[]
            {
                "用户名", "姓名", "手机号", "邮箱", "是否激活", "角色",
                "所属组织编码", "所属组织名称", "创建时间"
            }
        };

        foreach (var user in users)
        {
            orgByUser.TryGetValue(user.Id, out var org);

            rows.Add(new[]
            {
                user.UserName,
                user.Name,
                user.PhoneNumber,
                user.Email,
                // 与其它导出一样用「是 / 否」而不是「启用 / 停用」：
                // 这一列的表头是"是否激活"，两套措辞混在一份文件里只会让人怀疑哪个是准的
                user.IsActive ? "是" : "否",
                roleNames.TryGetValue(user.Id, out var names) && names.Length > 0
                    ? string.Join("、", names)
                    : null,
                org?.Code,
                org?.Name,
                user.CreationTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            });
        }

        return new UserCsvExportResult(
            Csv.WriteForExcel(rows),
            total,
            users.Count,
            IsTruncated: users.Count < total);
    }

    /// <summary>一次取回这批用户的角色名（避免 N+1）。</summary>
    private async Task<Dictionary<Guid, string[]>> LoadRoleNamesAsync(
        List<Guid> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, string[]>();
        }

        var rows = await _users.GetRoleNamesAsync(userIds, cancellationToken);

        return rows.ToDictionary(r => r.Id, r => r.RoleNames ?? Array.Empty<string>());
    }

    /// <summary>
    /// 反查"用户 → 所属组织"：先按 `ActivatedUserId` 取出这批人的预置记录，再一次性取组织。
    ///
    /// 两步都是**批量**：逐人查预置记录、逐条查组织，在 1000 人的页面上就是 2000 次往返。
    /// </summary>
    private async Task<Dictionary<Guid, SysOrg>> LoadOrganizationsAsync(
        List<Guid> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, SysOrg>();
        }

        var predefines = await _predefines.GetListAsync(
            p => p.ActivatedUserId != null && userIds.Contains(p.ActivatedUserId.Value),
            cancellationToken);

        if (predefines.Count == 0)
        {
            return new Dictionary<Guid, SysOrg>();
        }

        var orgIds = predefines.Select(p => p.OrgId).Distinct().ToList();

        var orgs = await _orgs.GetListAsync(o => orgIds.Contains(o.Id), cancellationToken);
        var orgById = orgs.ToDictionary(o => o.Id);

        // 同一个用户理论上只应有一条已激活的预置记录；真出现多条时取任意一条并保持稳定，
        // 而不是抛异常 —— 一条历史脏数据不该让整次导出失败。
        var result = new Dictionary<Guid, SysOrg>();

        foreach (var predefine in predefines.OrderBy(p => p.CreationTime))
        {
            if (predefine.ActivatedUserId is { } userId
                && !result.ContainsKey(userId)
                && orgById.TryGetValue(predefine.OrgId, out var org))
            {
                result[userId] = org;
            }
        }

        return result;
    }
}
