using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Services;
using Xjgl.Students;

namespace Xjgl.Organizations;

/// <summary>
/// 组织（省 / 市 / 县 / 校）的批量导入导出（ROADMAP 第 17 条）。
///
/// 【为什么组织导入最值得先做】
/// 部署一套学籍系统的第一步就是**把学校名录灌进去** —— 河南省有上万所学校，
/// 靠界面一个个点是不现实的。方案文档里 Q6 问的就是"省里的学校名录你能提供吗"，
/// 当时的答复是"能提供就导入真实数据，不能就用模拟数据 + 留导入模板"。
/// 模板与导入因此是**部署前置条件**，不是锦上添花。
///
/// 【为什么导入复用 OrgAppService.CreateAsync】
/// 与学生导入同一条理由：导入必须与手工新增**走完全相同的校验** ——
/// 编码唯一、层级必须对（省→市→县→校，见 `EnsureHierarchy`）、物化路径与 TreeLevel 推导。
/// 自己拼 SQL 的话，"导入进来的组织"会变成绕过校验的后门，
/// 而层级错的树会让数据范围与审核级别**静默算错**。
/// </summary>
public interface IOrganizationImportExportAppService : IApplicationService
{
    /// <summary>导入模板（CSV 文本，含表头与四行示例，覆盖四个层级）。</summary>
    string GetImportTemplate();

    /// <summary>模板里的列说明。</summary>
    List<ImportColumnDto> GetColumns();

    /// <summary>
    /// 批量导入。**逐行容错**：单行失败不影响其它行，最后统一返回失败明细。
    ///
    /// 行序**不需要**由用户保证（文件里"学校"写在"县区"前面也能导入）：
    /// 服务端按"省 → 市 → 县 → 校"排序后再处理，因为上级必须先生成才能挂下级。
    /// </summary>
    Task<ImportOrganizationsResultDto> ImportAsync(ImportOrganizationsDto input);
}

public class ImportOrganizationsDto
{
    /// <summary>CSV 全文（首行为表头）。</summary>
    [Required]
    public string Content { get; set; } = default!;
}

public class ImportOrganizationsResultDto
{
    public int Total { get; set; }

    public int Succeeded { get; set; }

    public int Failed { get; set; }

    public List<ImportOrganizationErrorDto> Errors { get; set; } = new();

    /// <summary>给前端直接展示的一句话结论。</summary>
    public string Message { get; set; } = default!;
}

public class ImportOrganizationErrorDto
{
    /// <summary>CSV 里的行号（与用户看到的 Excel 行号一致，从 1 开始，含表头）。</summary>
    public int RowIndex { get; set; }

    public string? OrgType { get; set; }

    public string? Code { get; set; }

    public string? Name { get; set; }

    public string Message { get; set; } = default!;
}
