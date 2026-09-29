using System.Globalization;
using Microsoft.Extensions.Configuration;
using Volo.Abp.DependencyInjection;
using Xjgl.Data;
using Xjgl.Text;

namespace Xjgl.Changes;

/// <summary>异动导出的结果（与学生的导出同一形状：总数 / 实际行数 / 是否触顶）。</summary>
public sealed record ChangeCsvExportResult(string Csv, long Total, int RowCount, bool IsTruncated);

/// <summary>
/// 异动申请 / 审核流水的 CSV 导出（ROADMAP 第 17 条）。
///
/// 【为什么是两份产物，而不是一份】
///   - **申请单导出**（`Chg.Apply.Export`）：申请页要的清单 —— 一个人一行，
///     回答"这段时间谁办了转学、办到哪一步了"。
///   - **审核流水导出**（`Chg.Audit.Export`）：审核页要的台账 —— **一个节点一行**，
///     回答"谁在什么时候审的、意见是什么"。这是纪检/复核最常要的那份东西，
///     而它在申请单清单里根本表达不出来（一份申请有多个节点）。
/// 两者的权限码也不同（见 `ExportTaskPermissions`），因此是两种任务种类。
///
/// 【数据范围怎么保证】
/// 审核流水**不自己查申请单**，而是先走 `IChangeFlowAppService.GetAppliesAsync`
/// 拿到"当前身份有权看到的申请单"，再取这些申请单的节点。
/// 这样"能导出哪些"与"界面上能看到哪些"用的是同一处逻辑 ——
/// 自己再写一遍数据范围判断，迟早会与列表页不一致（而不一致的方向往往是放宽）。
/// </summary>
public interface IChangeCsvExportBuilder
{
    Task<ChangeCsvExportResult> BuildAppliesAsync(
        ChangeApplyQueryDto query,
        CancellationToken cancellationToken = default);

    Task<ChangeCsvExportResult> BuildAuditAsync(
        ChangeApplyQueryDto query,
        CancellationToken cancellationToken = default);
}

public class ChangeCsvExportBuilder : IChangeCsvExportBuilder, ITransientDependency
{
    /// <summary>与学生导出一致的行数上限配置（超限会如实写进任务提示，不静默截断）。</summary>
    private const string MaxRowsConfigKey = "Xjgl:Export:MaxRows";

    private const int DefaultMaxRows = 100_000;

    /// <summary>ABP 对 MaxResultCount 有 1000 的硬上限，因此只能分页累积。</summary>
    private const int PageSize = 1000;

    private readonly IChangeFlowAppService _flow;
    private readonly IXjglRepository<StuChangeFlowTask> _tasks;
    private readonly IConfiguration _configuration;

    public ChangeCsvExportBuilder(
        IChangeFlowAppService flow,
        IXjglRepository<StuChangeFlowTask> tasks,
        IConfiguration configuration)
    {
        _flow = flow;
        _tasks = tasks;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public async Task<ChangeCsvExportResult> BuildAppliesAsync(
        ChangeApplyQueryDto query,
        CancellationToken cancellationToken = default)
    {
        var (applies, total, truncated) = await LoadAppliesAsync(query);

        var rows = new List<IEnumerable<string?>>
        {
            new[]
            {
                "申请单号", "异动类型", "学生姓名", "学籍辅号", "申请日期", "状态",
                "转出学校", "转入学校", "当前节点", "当前审核单位", "通过时间"
            }
        };

        rows.AddRange(applies.Select(a => new[]
        {
            a.ApplyNo,
            a.ChangeTypeName,
            a.StudentName,
            a.StudentNo,
            a.ApplyDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            a.StatusName,
            a.SourceSchoolName,
            a.TargetSchoolName,
            a.CurrentNodeName,
            a.CurrentOrgName,
            a.FinalApprovedTime?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        }));

        return new ChangeCsvExportResult(Csv.WriteForExcel(rows), total, applies.Count, truncated);
    }

    /// <inheritdoc />
    public async Task<ChangeCsvExportResult> BuildAuditAsync(
        ChangeApplyQueryDto query,
        CancellationToken cancellationToken = default)
    {
        var (applies, total, truncated) = await LoadAppliesAsync(query);

        var rows = new List<IEnumerable<string?>>
        {
            new[]
            {
                "申请单号", "学生姓名", "学籍辅号", "异动类型", "序号", "审核节点",
                "审核单位", "审批级别", "状态", "处理人", "处理时间", "审核意见", "合并来源序号"
            }
        };

        if (applies.Count == 0)
        {
            return new ChangeCsvExportResult(Csv.WriteForExcel(rows), total, 0, truncated);
        }

        var applyById = applies.ToDictionary(a => a.Id, a => a);
        var applyIds = applyById.Keys.ToList();

        // 一次性把这份清单的节点全取回来（不按申请单逐个查：那是 N+1，
        // 一份 2000 人的名单会打 2000 次库）。
        var tasks = await _tasks.GetListAsync(t => applyIds.Contains(t.ApplyId));

        foreach (var task in tasks
                     .OrderBy(t => t.ApplyId)
                     .ThenBy(t => t.Seq))
        {
            var apply = applyById[task.ApplyId];
            var merged = task.GetMergedFromSeqs();

            rows.Add(new[]
            {
                apply.ApplyNo,
                apply.StudentName,
                apply.StudentNo,
                apply.ChangeTypeName,
                task.Seq.ToString(CultureInfo.InvariantCulture),
                task.NodeName,
                task.OrgName,
                ApproveLevels.GetDisplayName(task.ApproveLevel),
                ChangeTaskStatus.GetDisplayName(task.Status),
                task.HandledByName,
                task.HandledTime?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                task.Opinion,
                // 合并来源：这一行其实是"同一单位只审一次"合并出来的，
                // 复核时要能看出它代表了模板里的哪几步
                merged.Length == 0 ? null : string.Join("、", merged)
            });
        }

        return new ChangeCsvExportResult(Csv.WriteForExcel(rows), total, rows.Count - 1, truncated);
    }

    /// <summary>
    /// 按条件分页累积申请单（复用列表查询 → 数据范围与码值翻译都一致）。
    /// </summary>
    private async Task<(List<ChangeApplyListItemDto> Applies, long Total, bool Truncated)> LoadAppliesAsync(
        ChangeApplyQueryDto query)
    {
        var maxRows = Math.Max(1, _configuration.GetValue(MaxRowsConfigKey, DefaultMaxRows));

        var applies = new List<ChangeApplyListItemDto>();
        long total = 0;

        for (var skip = 0; skip < maxRows; skip += PageSize)
        {
            var take = Math.Min(PageSize, maxRows - applies.Count);

            if (take <= 0)
            {
                break;
            }

            var page = await _flow.GetAppliesAsync(new ChangeApplyQueryDto
            {
                ChangeTypeId = query.ChangeTypeId,
                StudentId = query.StudentId,
                Status = query.Status,
                SchoolOrgId = query.SchoolOrgId,
                Keyword = query.Keyword,
                Sorting = query.Sorting ?? "ApplyDate DESC",
                SkipCount = skip,
                MaxResultCount = take
            });

            total = page.TotalCount;
            applies.AddRange(page.Items);

            if (page.Items.Count < take || applies.Count >= total)
            {
                break;
            }
        }

        return (applies, total, applies.Count < total);
    }
}
