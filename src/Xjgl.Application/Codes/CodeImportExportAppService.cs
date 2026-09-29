using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Xjgl.Permissions;
using Xjgl.Students;
using Xjgl.Text;

namespace Xjgl.Codes;

/// <summary>
/// 微代码（码表 / 码项）批量导入（ROADMAP 第 17 条）。
///
/// 导出走后台任务（`ExportTaskKinds.CodeCsv` → `CodeCsvExportBuilder`），与其它导出同一条链。
/// </summary>
[Authorize]
public class CodeImportExportAppService : XjglAppService, ICodeImportExportAppService
{
    private static readonly string[] TemplateHeader =
    {
        "类别编码", "类别名称", "码值", "码名称", "排序", "是否启用"
    };

    /// <summary>
    /// 示例行：**同一个类别两行**。
    ///
    /// 只给一行的话，用户看不出"类别编码在多行里要重复写" ——
    /// 而这是这份文件唯一需要理解的约定（见接口注释里"一行 = 一个码项"的说明）。
    /// </summary>
    private static readonly string[][] TemplateSamples =
    {
        new[] { "EXAM_TYPE", "考试类型", "01", "期中考试", "10", "是" },
        new[] { "EXAM_TYPE", "考试类型", "02", "期末考试", "20", "是" }
    };

    private readonly ICodeAppService _codes;

    public CodeImportExportAppService(ICodeAppService codes)
    {
        _codes = codes;
    }

    /// <inheritdoc />
    [Authorize(MetadataPermissions.Sys.Code.Import)]
    public string GetImportTemplate()
    {
        var rows = new List<IEnumerable<string?>> { TemplateHeader };
        rows.AddRange(TemplateSamples.Select(s => s.AsEnumerable()));

        return Csv.WriteForExcel(rows);
    }

    /// <inheritdoc />
    [Authorize(MetadataPermissions.Sys.Code.Import)]
    public List<ImportColumnDto> GetColumns() => new()
    {
        new ImportColumnDto { Name = "类别编码", Required = true, Remark = "如 EXAM_TYPE；同一个类别的多行要重复写这个编码" },
        new ImportColumnDto { Name = "类别名称", Required = true, Remark = "类别不存在时会按这里的名称自动创建；已存在的类别以库里的名称为准（不会被覆盖）" },
        new ImportColumnDto { Name = "码值", Required = true, Remark = "存进库的值，如 01" },
        new ImportColumnDto { Name = "码名称", Required = true, Remark = "界面上显示的中文，如 期中考试" },
        new ImportColumnDto { Name = "排序", Required = false, Remark = "整数，越小越靠前，默认 0" },
        new ImportColumnDto { Name = "是否启用", Required = false, Remark = "是 / 否，默认「是」" }
    };

    /// <inheritdoc />
    [Authorize(MetadataPermissions.Sys.Code.Import)]
    public async Task<ImportCodesResultDto> ImportAsync(ImportCodesDto input)
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

        // 已有类别：按编码索引。已存在的类别**不重建、也不改名** ——
        // 改名是有风险的动作（引用它的地方看到的中文会变），导入不该顺手做这种事。
        var categories = (await _codes.GetCategoriesAsync(onlyEnabled: false))
            .ToDictionary(c => c.Code, c => c, StringComparer.Ordinal);

        var result = new ImportCodesResultDto();
        var errors = new List<ImportCodeErrorDto>();

        for (var i = 1; i < rows.Count; i++)
        {
            var rowIndex = i + 1;
            var row = rows[i];

            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            result.Total++;

            var categoryCode = Csv.Cell(row, header, "类别编码")?.Trim() ?? string.Empty;
            var categoryName = Csv.Cell(row, header, "类别名称")?.Trim() ?? string.Empty;
            var code = Csv.Cell(row, header, "码值")?.Trim() ?? string.Empty;
            var name = Csv.Cell(row, header, "码名称")?.Trim() ?? string.Empty;

            try
            {
                if (categoryCode.Length == 0 || categoryName.Length == 0)
                {
                    throw new UserFriendlyException("类别编码与类别名称都不能为空。");
                }

                if (code.Length == 0 || name.Length == 0)
                {
                    throw new UserFriendlyException("码值与码名称都不能为空。");
                }

                if (!categories.TryGetValue(categoryCode, out var category))
                {
                    // 第一行出现的类别名用来建类别；后续行沿用（不再看名称列）
                    category = await _codes.CreateCategoryAsync(new CreateUpdateCodeCategoryDto
                    {
                        Code = categoryCode,
                        Name = categoryName,
                        IsEnabled = true,
                        Cacheable = true
                    });

                    categories[categoryCode] = category;
                    result.CreatedCategories++;
                }

                // 复用码项新增接口：码值唯一、长度等校验都走同一条路
                await _codes.CreateItemAsync(new CreateUpdateCodeItemDto
                {
                    CategoryId = category.Id,
                    Code = code,
                    Name = name,
                    Sort = int.TryParse(Csv.Cell(row, header, "排序")?.Trim(), out var sort) ? sort : 0,
                    IsEnabled = ParseEnabled(Csv.Cell(row, header, "是否启用"))
                });

                result.Succeeded++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                errors.Add(new ImportCodeErrorDto
                {
                    RowIndex = rowIndex,
                    CategoryCode = categoryCode,
                    Code = code,
                    Name = name,
                    Message = DescribeRowError(ex)
                });
            }
        }

        // 与组织导入一致：失败明细按**文件行号**排序，用户对着表格从上往下改
        result.Errors = errors.OrderBy(e => e.RowIndex).ToList();

        result.Message = result.Failed == 0
            ? $"全部导入成功：{result.Succeeded} 条码项（新建类别 {result.CreatedCategories} 个）。"
            : $"共 {result.Total} 行：成功 {result.Succeeded} 条，失败 {result.Failed} 行（见下方明细）。";

        return result;
    }

    /// <summary>「是否启用」：空 = 启用；认不出来的写法按启用处理（这一列是可选的辅助列）。</summary>
    private static bool ParseEnabled(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return text.Trim().ToLowerInvariant() is not ("否" or "n" or "no" or "false" or "0" or "停用");
    }

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

        // UserFriendlyException 也是 BusinessException 的兄弟类型，单独判一次：
        // 它承载的是"给用户看的一句话"（如"码值与码名称都不能为空"）。
        if (ex is UserFriendlyException ufe)
        {
            return ufe.Message;
        }

        return ex.Message;
    }
}
