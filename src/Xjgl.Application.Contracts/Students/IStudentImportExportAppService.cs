using Volo.Abp.Application.Services;

namespace Xjgl.Students;

/// <summary>
/// 学生批量导入导出。
///
/// 【为什么单独一个应用服务，而不是塞进 StudentAppService】
///   导入导出有自己的复杂度：CSV 解析、按表头映射、逐行容错、错误明细、
///   模板生成、导出时的数据范围过滤。塞进 StudentAppService 会让那个类
///   既管学籍业务又管文件格式，两边都变难改。
///
/// 【为什么导入复用 CreateAsync 而不是自己拼 SQL】
///   导入必须与手工录入**走完全相同的校验**：身份证校验位、跨校查重、
///   生僻字姓名规则、学籍辅号生成、数据范围。否则"导入进来的数据"
///   会变成绕过校验的后门 —— 这是学籍系统里最危险的一类不一致。
///   因此导入就是一个循环：逐行组装 DTO → 调 CreateAsync → 收集失败原因。
/// </summary>
public interface IStudentImportExportAppService : IApplicationService
{
    /// <summary>导入模板（CSV 文本，含表头与一行示例）。</summary>
    string GetImportTemplate();

    /// <summary>
    /// 批量导入。**逐行容错**：单行失败不影响其它行，最后统一返回失败明细。
    /// </summary>
    Task<ImportStudentsResultDto> ImportAsync(ImportStudentsDto input);

    /// <summary>
    /// 按当前查询条件导出（**受数据范围约束**）。
    /// 返回 CSV 文本，含 UTF-8 BOM 以便 Excel 正确识别中文。
    ///
    /// 名称以 <c>Get</c> 开头是**约定要求**而非风格：ABP 自动 API 靠方法名前缀判定 HTTP 动词，
    /// 不以 Get 开头的方法会被注册成 POST，而前端是以 GET + 查询串发起下载的。
    /// </summary>
    Task<string> GetExportAsync(StudentQueryDto input);

    /// <summary>模板里的列说明（给前端做提示用）。</summary>
    List<ImportColumnDto> GetColumns();
}

/// <summary>一列的说明。</summary>
public class ImportColumnDto
{
    public string Name { get; set; } = default!;
    public bool Required { get; set; }
    public string? Remark { get; set; }
}

public class ImportStudentsDto
{
    /// <summary>CSV 全文（首行为表头）。</summary>
    public string Content { get; set; } = default!;
}

public class ImportStudentsResultDto
{
    public int Total { get; set; }

    public int Succeeded { get; set; }

    public int Failed { get; set; }

    public List<ImportStudentErrorDto> Errors { get; set; } = new();

    /// <summary>给前端直接展示的一句话结论。</summary>
    public string Message { get; set; } = default!;
}

public class ImportStudentErrorDto
{
    /// <summary>CSV 里的行号（与用户看到的 Excel 行号一致，从 1 开始）。</summary>
    public int RowIndex { get; set; }

    public string? Name { get; set; }

    public string? IdCardNumber { get; set; }

    public string Message { get; set; } = default!;
}
