using System.Globalization;
using Microsoft.Extensions.Configuration;
using Volo.Abp.DependencyInjection;
using Xjgl.Data;
using Xjgl.Exporting;
using Xjgl.Organizations;
using Xjgl.Text;

namespace Xjgl.Users;

/// <summary>预置名单导出的结果。</summary>
public sealed record UserProvisionCsvExportResult(string Csv, long Total, int RowCount, bool IsTruncated);

/// <summary>
/// 预置名单（"待激活的人"）CSV 导出（ROADMAP 第 17 条）。
///
/// 【为什么复用列表查询，而不是自己写一遍 where】
///   预置名单是**受数据范围约束**的（一个县区管理员只能看到本级的预置记录），
///   而这条约束在 `UserProvisionAppService.GetListAsync` 里的写法很有讲究
///   （"仅本级"要用一个不可能命中的哨兵前缀关掉"后代"那一半，否则范围会比配置更宽）。
///   导出自己再写一遍 = 迟早与页面不一致，而不一致的方向通常是**放宽**。
///   所以这里分页累积地调列表查询：数据范围、关键字、状态、组织、手机号脱敏
///   全部只有一处实现。代价见 `ExportTaskPermissions`：该种类要求 Export + Query 两个权限。
///
/// 【身份证：只出后 4 位，明文绝不落盘】
///   本项目的身份证是**加密存储**的（`SysUserPredefine.IdCardEncrypted`）。
///   导出明文等于把加密白做一遍：加密防的是"拖库即得"，而一份躺在下载目录里
///   的通讯录比数据库更容易被拿到手。因此这一列走 `PredefineUserDto.MaskedIdCard`
///   （前 6 位也一并掩掉，只留后 4 位 —— 比学生名单那条更严，理由见 DTO 上的注释）。
///
/// 【代价必须写在文件里，而不是只写在代码注释里】
///   掩码之后这份文件**不能用来回导**：激活校验要的是完整身份证号。
///   如果只在界面上提示一句，文件被转发给别人时那句话就丢了 ——
///   拿到文件的人会以为"照这份填一遍就能导入"，然后在激活环节被拒。
///   所以 CSV 的**第一行是一行 `#` 开头的说明**，跟着文件一起走。
///   （这是刻意的取舍：Excel 打开时表头会落到第 2 行，看起来"不那么整齐"，
///   但那行字恰好在那时最显眼 —— 而整齐换不来"不会误用"，见 ROADMAP 第 17 条。）
/// </summary>
public interface IUserProvisionCsvExportBuilder
{
    Task<UserProvisionCsvExportResult> BuildAsync(
        PredefineUserQueryDto query,
        CancellationToken cancellationToken = default);
}

public class UserProvisionCsvExportBuilder : IUserProvisionCsvExportBuilder, ITransientDependency
{
    /// <summary>与其它导出一致的行数上限配置。</summary>
    private const string MaxRowsConfigKey = "Xjgl:Export:MaxRows";

    private const int DefaultMaxRows = 100_000;

    /// <summary>单页取数上限（ABP 对 `MaxResultCount` 的硬上限是 1000）。</summary>
    private const int PageSize = 1000;

    /// <summary>跟着文件走的那句交代。改这里等于改用户看到的说明，不要只改措辞的一半。</summary>
    private const string MaskNotice =
        "# 说明：身份证号已掩码（只保留后 4 位），本文件不能用于回导 —— " +
        "激活校验需要完整身份证号，导入时必须重新填写。";

    private readonly IUserProvisionAppService _provision;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IConfiguration _configuration;

    public UserProvisionCsvExportBuilder(
        IUserProvisionAppService provision,
        IXjglRepository<SysOrg> orgs,
        IConfiguration configuration)
    {
        _provision = provision;
        _orgs = orgs;
        _configuration = configuration;
    }

    public async Task<UserProvisionCsvExportResult> BuildAsync(
        PredefineUserQueryDto query,
        CancellationToken cancellationToken = default)
    {
        var maxRows = Math.Max(1, _configuration.GetValue(MaxRowsConfigKey, DefaultMaxRows));

        var items = new List<PredefineUserDto>();
        long total = 0;

        for (var skip = 0; skip < maxRows; skip += PageSize)
        {
            var take = Math.Min(PageSize, maxRows - items.Count);

            if (take <= 0)
            {
                break;
            }

            var page = await _provision.GetListAsync(new PredefineUserQueryDto
            {
                OrgId = query.OrgId,
                IncludeDescendantOrgs = query.IncludeDescendantOrgs,
                Keyword = query.Keyword,
                Status = query.Status,
                BatchNo = query.BatchNo,
                Sorting = query.Sorting ?? "CreationTime DESC",
                SkipCount = skip,
                MaxResultCount = take
            });

            total = page.TotalCount;
            items.AddRange(page.Items);

            // ★ 债 23：每取完一页上报一次进度。分母取 `min(总数, 上限)` ——
            // 这一种会被 `MaxRows` 截断，用原总数会让截断场景下的进度永远差最后几个百分点。
            await ExportProgress.ReportAsync(items.Count, Math.Min(total, maxRows), cancellationToken);

            if (page.Items.Count < take || items.Count >= total)
            {
                break;
            }
        }

        var orgNames = await LoadOrgNamesAsync(items, cancellationToken);

        var rows = new List<IEnumerable<string?>>
        {
            // 第一行是说明、不是表头：理由见类注释（它必须跟着文件走）
            new[] { MaskNotice },
            new[]
            {
                "姓名", "手机号", "身份证后4位", "组织编码", "组织名称",
                "角色", "岗位", "状态", "激活有效期"
            }
        };

        foreach (var item in items)
        {
            orgNames.TryGetValue(item.OrgId, out var org);

            rows.Add(new[]
            {
                item.RealName,
                // 手机号走列表 DTO 的脱敏值（138****8000）：与页面上看到的一致。
                // 「导出不比页面多」在这一列上同样成立 —— 见 ExportTaskPermissions 的说明。
                item.PhoneNumber,
                // 已掩码；空（未登记身份证）时留空而不是填一串星号 ——
                // 星号会被读成"登记过但看不清"，而事实是"没登记"。
                item.MaskedIdCard,
                org?.Code,
                org?.Name,
                item.RoleNames,
                item.Position,
                item.StatusName,
                item.ActivationExpiresAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            });
        }

        return new UserProvisionCsvExportResult(
            Csv.WriteForExcel(rows),
            total,
            items.Count,
            IsTruncated: items.Count < total);
    }

    /// <summary>组织编码与名称（列表 DTO 上只有 `OrgPathCode` 物化路径，而文件里要的是这两列）。</summary>
    private sealed record OrgRef(string Code, string Name);

    /// <summary>
    /// 组织 Id → 编码 / 名称，按 OrgId **批量**补一次
    /// （逐条查就是 N+1；一页 1000 人 = 1000 次往返）。
    /// </summary>
    private async Task<Dictionary<Guid, OrgRef>> LoadOrgNamesAsync(
        List<PredefineUserDto> items,
        CancellationToken cancellationToken)
    {
        var orgIds = items.Select(i => i.OrgId).Distinct().ToList();

        if (orgIds.Count == 0)
        {
            return new Dictionary<Guid, OrgRef>();
        }

        var orgs = await _orgs.GetListAsync(o => orgIds.Contains(o.Id), cancellationToken);

        return orgs.ToDictionary(o => o.Id, o => new OrgRef(o.Code, o.Name));
    }
}
