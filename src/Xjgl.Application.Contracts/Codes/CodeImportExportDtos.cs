using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Services;
using Xjgl.Students;

namespace Xjgl.Codes;

/// <summary>
/// 微代码（码表 / 码项）批量导入导出（ROADMAP 第 17 条）。
///
/// 【为什么码表需要导入导出】
/// 码表是**环境相关**的数据：测试环境调好的码项要搬到生产、生产新增的码项要同步回测试，
/// 靠界面一条条点必然漏。而且码表是"看起来简单、漏一条影响很隐蔽"的典型 ——
/// 少一个码值，对应的下拉框就少一个选项，业务人员只会说"这里选不到"。
///
/// 【一行 = 一个码项】类别信息在每行重复写。
/// 不做"类别一小节、码项一块"的嵌套格式：CSV 里没有嵌套，
/// 要表达嵌套就得发明缩进/分隔符约定，而那些约定在 Excel 里会被改坏。
/// 重复写类别编码的代价只是文件大一点，换来的是**导入导出都只是按列读**。
/// </summary>
public interface ICodeImportExportAppService : IApplicationService
{
    /// <summary>导入模板（CSV 文本，含表头与示例行）。</summary>
    string GetImportTemplate();

    /// <summary>模板里的列说明。</summary>
    List<ImportColumnDto> GetColumns();

    /// <summary>批量导入。**逐行容错**；类别不存在时按第一行出现的类别名称自动创建。</summary>
    Task<ImportCodesResultDto> ImportAsync(ImportCodesDto input);
}

public class ImportCodesDto
{
    /// <summary>CSV 全文（首行为表头）。</summary>
    [Required]
    public string Content { get; set; } = default!;
}

public class ImportCodesResultDto
{
    public int Total { get; set; }

    /// <summary>本次**新建**的类别数（已存在的类别不重复建）。</summary>
    public int CreatedCategories { get; set; }

    public int Succeeded { get; set; }

    public int Failed { get; set; }

    public List<ImportCodeErrorDto> Errors { get; set; } = new();

    public string Message { get; set; } = default!;
}

public class ImportCodeErrorDto
{
    /// <summary>CSV 里的行号（表头是第 1 行，数据从第 2 行开始）。</summary>
    public int RowIndex { get; set; }

    public string? CategoryCode { get; set; }

    public string? Code { get; set; }

    public string? Name { get; set; }

    public string Message { get; set; } = default!;
}

/// <summary>码表导出的筛选条件（都不填 = 导出全部）。</summary>
public class CodeExportQueryDto
{
    /// <summary>只导出某个类别（类别编码）。</summary>
    [StringLength(64)]
    public string? CategoryCode { get; set; }

    /// <summary>关键字（匹配类别编码 / 类别名称 / 码值 / 码名称）。</summary>
    [StringLength(64)]
    public string? Keyword { get; set; }

    /// <summary>是否只导出启用的（默认 false：导出是搬运/备份，漏掉停用的反而是意外）。</summary>
    public bool OnlyEnabled { get; set; }
}
