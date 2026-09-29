using Volo.Abp.DependencyInjection;
using Xjgl.Text;

namespace Xjgl.Codes;

/// <summary>码表导出的结果。</summary>
public sealed record CodeCsvExportResult(string Csv, int CategoryCount, int ItemCount);

/// <summary>
/// 微代码（码表 / 码项）CSV 导出（ROADMAP 第 17 条）。
///
/// 【列与导入模板完全一致】导出的文件可以直接改一改再导入 ——
/// 这是码表在"测试 → 生产"之间搬运最省事的形态。
/// </summary>
public interface ICodeCsvExportBuilder
{
    Task<CodeCsvExportResult> BuildAsync(CodeExportQueryDto query, CancellationToken cancellationToken = default);
}

public class CodeCsvExportBuilder : ICodeCsvExportBuilder, ITransientDependency
{
    private readonly ICodeAppService _codes;

    public CodeCsvExportBuilder(ICodeAppService codes)
    {
        _codes = codes;
    }

    public async Task<CodeCsvExportResult> BuildAsync(
        CodeExportQueryDto query,
        CancellationToken cancellationToken = default)
    {
        // onlyEnabled: false —— 导出默认带上停用的那些，否则"搬运"会静默少几条，
        // 而少的那几条在界面上只表现为"某处选不到某个选项"。
        var categories = await _codes.GetCategoriesAsync(onlyEnabled: query.OnlyEnabled);

        var csv = new List<IEnumerable<string?>>
        {
            new[] { "类别编码", "类别名称", "码值", "码名称", "排序", "是否启用" }
        };

        var categoryCount = 0;
        var itemCount = 0;

        var hasKeyword = !query.Keyword.IsNullOrWhiteSpace();
        var keyword = query.Keyword?.Trim() ?? string.Empty;

        foreach (var category in categories.OrderBy(c => c.Code, StringComparer.Ordinal))
        {
            if (!query.CategoryCode.IsNullOrWhiteSpace()
                && !string.Equals(category.Code, query.CategoryCode, StringComparison.Ordinal))
            {
                continue;
            }

            // 关键字命中**类别**时，它的全部码项都要带上。
            // 【这里踩过一次】最初写成"类别与码项都必须命中关键字"，
            // 结果是"搜一个类别名 → 出来一个类别、零条码项" ——
            // 而用户搜类别名的意思恰恰是"我要这个类别"。
            var categoryMatches = !hasKeyword
                                  || category.Code.Contains(keyword, StringComparison.Ordinal)
                                  || category.Name.Contains(keyword, StringComparison.Ordinal);

            // asTree: false —— 树形码表也**平铺导出**（见接口注释：CSV 表达不了嵌套）。
            // 代价是树形结构的 ParentId 不在文件里；导入端因此只建平铺码项。
            // 这是明确取舍：树形码表只占少数，为它发明一套缩进约定不值当。
            var items = await _codes.GetItemsAsync(category.Id, onlyEnabled: query.OnlyEnabled);

            var included = items
                .OrderBy(i => i.Sort)
                .ThenBy(i => i.Code, StringComparer.Ordinal)
                .Where(i => categoryMatches
                            || !hasKeyword
                            || i.Code.Contains(keyword, StringComparison.Ordinal)
                            || i.Name.Contains(keyword, StringComparison.Ordinal))
                .ToList();

            if (included.Count == 0)
            {
                continue;
            }

            categoryCount++;

            foreach (var item in included)
            {
                itemCount++;

                csv.Add(new[]
                {
                    category.Code,
                    category.Name,
                    item.Code,
                    item.Name,
                    item.Sort.ToString(),
                    item.IsEnabled ? "是" : "否"
                });
            }
        }

        return new CodeCsvExportResult(Csv.WriteForExcel(csv), categoryCount, itemCount);
    }
}
