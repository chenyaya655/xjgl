using System.Text;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Xjgl.Data;
using Xjgl.Permissions;
using Xjgl.Students;
using Xjgl.Text;

namespace Xjgl.Organizations;

/// <summary>
/// 组织（省 / 市 / 县 / 校）批量导入（ROADMAP 第 17 条）。
///
/// 导出不在这里：导出走后台任务（`ExportTaskKinds.OrganizationCsv` →
/// `OrganizationCsvExportBuilder`），与其它导出同一条链路 ——
/// 上万所学校的 CSV 不该在请求线程里拼。
/// </summary>
[Authorize]
public class OrganizationImportExportAppService : XjglAppService, IOrganizationImportExportAppService
{
    /// <summary>模板里的一行示例。</summary>
    private static readonly string[] TemplateHeader =
    {
        "组织类型", "组织编码", "组织名称", "上级组织编码", "简称", "排序", "是否启用"
    };

    /// <summary>
    /// 四行示例，**覆盖四个层级**。
    ///
    /// 只给一行示例的话，用户看不出"上级组织编码"该填什么 ——
    /// 而各级之间靠编码挂接正是这份模板唯一需要理解的地方。
    /// </summary>
    private static readonly string[][] TemplateSamples =
    {
        new[] { "省厅", "4100000000", "河南省教育厅", "", "省教育厅", "10", "是" },
        new[] { "地市", "4101000000", "郑州市教育局", "4100000000", "郑州市局", "10", "是" },
        new[] { "县区", "4101020000", "郑州市金水区教育局", "4101000000", "金水区局", "10", "是" },
        new[] { "学校", "4101020001", "郑州市金水区实验小学", "4101020000", "实验小学", "10", "是" }
    };

    private readonly IOrgAppService _orgAppService;
    private readonly IXjglRepository<SysOrg> _orgs;

    public OrganizationImportExportAppService(
        IOrgAppService orgAppService,
        IXjglRepository<SysOrg> orgs)
    {
        _orgAppService = orgAppService;
        _orgs = orgs;
    }

    /// <inheritdoc />
    [Authorize(MetadataPermissions.Sys.Org.Import)]
    public string GetImportTemplate()
    {
        var rows = new List<IEnumerable<string?>> { TemplateHeader };
        rows.AddRange(TemplateSamples.Select(s => s.AsEnumerable()));

        // WriteForExcel 会加 UTF-8 BOM：不加的话 Excel 打开中文是乱码
        // （MS Excel 对无 BOM 的 UTF-8 按本地代码页解释）
        return Csv.WriteForExcel(rows);
    }

    /// <inheritdoc />
    [Authorize(MetadataPermissions.Sys.Org.Import)]
    public List<ImportColumnDto> GetColumns() => new()
    {
        new ImportColumnDto { Name = "组织类型", Required = true, Remark = "省厅 / 地市 / 县区 / 学校（也接受 Province / City / County / School）" },
        new ImportColumnDto { Name = "组织编码", Required = true, Remark = "教育局用行政区划码（6 位），学校用**学校标识码（10 位）**——学籍辅号的前 10 位就是它" },
        new ImportColumnDto { Name = "组织名称", Required = true },
        new ImportColumnDto { Name = "上级组织编码", Required = false, Remark = "省厅留空；其余填上级的**组织编码**（不是名称）" },
        new ImportColumnDto { Name = "简称", Required = false },
        new ImportColumnDto { Name = "排序", Required = false, Remark = "整数，越小越靠前，默认 0" },
        new ImportColumnDto { Name = "是否启用", Required = false, Remark = "是 / 否，默认「是」" }
    };

    /// <inheritdoc />
    [Authorize(MetadataPermissions.Sys.Org.Import)]
    public async Task<ImportOrganizationsResultDto> ImportAsync(ImportOrganizationsDto input)
    {
        var rows = Csv.Parse(input.Content);

        if (rows.Count == 0)
        {
            throw new UserFriendlyException("导入内容为空，请先下载模板并按模板填写。");
        }

        if (rows.Count == 1)
        {
            throw new UserFriendlyException("导入内容只有表头，没有数据行。");
        }

        var header = Csv.BuildHeaderIndex(rows[0]);

        var missingColumns = GetColumns()
            .Where(c => c.Required && !header.ContainsKey(c.Name))
            .Select(c => c.Name)
            .ToList();

        if (missingColumns.Count > 0)
        {
            throw new UserFriendlyException($"导入文件缺少必需列：{string.Join("、", missingColumns)}。请重新下载模板填写。");
        }

        // 编码 → 组织：既要能挂到**库里已有的**组织下（例如先手工建了省厅），
        // 也要能挂到**本次刚导入的**组织下（同一份文件里省级在前、学校在后）。
        var byCode = (await _orgs.GetListAsync())
            .ToDictionary(o => o.Code, o => o, StringComparer.Ordinal);

        var parsed = new List<PendingRow>();
        var result = new ImportOrganizationsResultDto();

        for (var i = 1; i < rows.Count; i++)
        {
            // 行号按用户看到的 Excel 行号报（表头是第 1 行，数据从第 2 行开始）
            var raw = rows[i];

            // 整行为空：Excel 末尾常有空行，直接跳过而不是报错
            if (raw.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            result.Total++;

            var typeText = Csv.Cell(raw, header, "组织类型");
            var code = Csv.Cell(raw, header, "组织编码")?.Trim() ?? string.Empty;
            var name = Csv.Cell(raw, header, "组织名称")?.Trim() ?? string.Empty;

            if (!OrgTypes.TryParse(typeText, out var orgType))
            {
                result.Failed++;
                result.Errors.Add(Error(i + 1, typeText, code, name,
                    $"无法识别的组织类型「{typeText}」，请填 省厅 / 地市 / 县区 / 学校。"));

                continue;
            }

            if (code.Length == 0 || name.Length == 0)
            {
                result.Failed++;
                result.Errors.Add(Error(i + 1, typeText, code, name, "组织编码与组织名称都不能为空。"));

                continue;
            }

            parsed.Add(new PendingRow
            {
                RowIndex = i + 1,
                OrgType = orgType,
                Code = code,
                Name = name,
                ParentCode = Csv.Cell(raw, header, "上级组织编码")?.Trim(),
                ShortName = Csv.Cell(raw, header, "简称")?.Trim(),
                Sort = ParseSort(Csv.Cell(raw, header, "排序")),
                IsEnabled = ParseEnabled(Csv.Cell(raw, header, "是否启用"))
            });
        }

        // ★ 按层级排序后再处理：上级必须**先生成**，下级才挂得上去。
        //
        // 不要求用户在文件里排好序 —— 那是把实现的约束推给使用者，
        // 而"学校写在县区前面"在真实文件里非常常见（按学校标识码排就会这样）。
        // 同层内保持原顺序，失败明细里的行号才对得上用户的文件。
        foreach (var row in parsed.OrderBy(r => LevelOf(r.OrgType)).ThenBy(r => r.RowIndex))
        {
            try
            {
                Guid? parentId = null;

                if (!string.IsNullOrWhiteSpace(row.ParentCode))
                {
                    if (!byCode.TryGetValue(row.ParentCode!, out var parent))
                    {
                        throw new UserFriendlyException(
                            $"找不到上级组织编码「{row.ParentCode}」。请确认上级写在文件里，或者已经在系统里存在。");
                    }

                    parentId = parent.Id;
                }

                // 复用新增接口：编码唯一、层级必须对（EnsureHierarchy）、
                // 物化路径与 TreeLevel 推导 —— 与学生导入同一条理由，
                // 导入不能成为绕过校验的后门。
                var created = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
                {
                    OrgType = row.OrgType,
                    Code = row.Code,
                    Name = row.Name,
                    ParentId = parentId,
                    ShortName = row.ShortName,
                    Sort = row.Sort,
                    IsEnabled = row.IsEnabled
                });

                // 让同一份文件里后面的行能挂到它下面
                byCode[row.Code] = new SysOrg(created.Id, row.OrgType, row.Code, row.Name, parentId);

                result.Succeeded++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add(Error(row.RowIndex, row.OrgType, row.Code, row.Name, DescribeRowError(ex)));
            }
        }

        result.Message = result.Failed == 0
            ? $"全部导入成功，共 {result.Succeeded} 个组织。"
            : $"共 {result.Total} 行：成功 {result.Succeeded} 个，失败 {result.Failed} 行（见下方明细）。";

        // 失败明细按**文件行号**排序，而不是按处理顺序。
        //
        // 处理顺序是按层级重排过的（上级要先生成），于是"县区那行"会排在
        // "类型写错的那行"前面 —— 用户对着表格一行行修的时候，
        // 错误列表的顺序与表格对不上，很容易改漏或改错行。
        // 排序同时也让输出稳定（同一份文件每次得到同样的明细）。
        result.Errors = result.Errors.OrderBy(e => e.RowIndex).ToList();

        return result;
    }

    /// <summary>层级序号：省 1 → 校 4。用来决定处理顺序（不是 TreeLevel，那是从路径算出来的）。</summary>
    private static int LevelOf(string orgType) => orgType switch
    {
        OrgTypes.Province => 1,
        OrgTypes.City => 2,
        OrgTypes.County => 3,
        _ => 4
    };

    private static int ParseSort(string? text)
        => int.TryParse(text?.Trim(), out var value) ? value : 0;

    /// <summary>
    /// 「是否启用」：空 = 启用（模板里没填通常是想建一个正常组织）。
    /// 认不出来的写法按**启用**处理而不是报错 —— 这一列是可选的辅助列，
    /// 为了它把整行判失败不划算。
    /// </summary>
    private static bool ParseEnabled(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var value = text.Trim().ToLowerInvariant();

        return value is not ("否" or "n" or "no" or "false" or "0" or "停用");
    }

    private static ImportOrganizationErrorDto Error(
        int rowIndex, string? orgType, string code, string name, string message) => new()
    {
        RowIndex = rowIndex,
        OrgType = orgType,
        Code = code,
        Name = name,
        Message = message
    };

    /// <summary>
    /// 把行内异常翻译成用户能照着改的一句话。
    /// 业务异常自带 reason（例如"「学校」只能挂在「县级教育行政部门」下面…"），优先用它 ——
    /// 那句提示正是本功能最需要的：层级填错时，用户要知道**该填哪一级**。
    /// </summary>
    private static string DescribeRowError(Exception ex)
    {
        if (ex is BusinessException be)
        {
            if (be.Data.Contains("reason") && be.Data["reason"] is string reason && reason.Length > 0)
            {
                return reason;
            }

            return be.Message;
        }

        if (ex is UserFriendlyException ufe)
        {
            return ufe.Message;
        }

        return ex.Message;
    }

    private sealed class PendingRow
    {
        public int RowIndex { get; init; }
        public string OrgType { get; init; } = default!;
        public string Code { get; init; } = default!;
        public string Name { get; init; } = default!;
        public string? ParentCode { get; init; }
        public string? ShortName { get; init; }
        public int Sort { get; init; }
        public bool IsEnabled { get; init; }
    }
}
