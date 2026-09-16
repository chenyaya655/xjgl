using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Xjgl.Students;

/// <summary>
/// 学生学籍管理。
///
/// 本模块是学籍系统的核心：学生主档 + 学籍辅号生成 + 重复学籍检测。
/// 异动审核通过后的业务联动（改学籍状态、年级班级、学校）也会落到这里。
/// </summary>
public interface IStudentAppService : IApplicationService
{
    Task<PagedResultDto<StudentListItemDto>> GetListAsync(StudentQueryDto input);

    Task<StudentDto> GetAsync(Guid id);

    /// <summary>
    /// 新增学生。会自动生成学籍辅号（按设置的规则 + 按学校自增的流水号），
    /// 并在写入前做重复学籍检测。
    /// </summary>
    Task<StudentDto> CreateAsync(CreateUpdateStudentDto input);

    Task<StudentDto> UpdateAsync(Guid id, CreateUpdateStudentDto input);

    /// <summary>删除（软删除，可在回收站恢复）。</summary>
    Task DeleteAsync(Guid id);

    /// <summary>
    /// 重复学籍检测：按 身份证号摘要 + 姓名 + 出生日期 跨校跨区查重。
    /// 新增/修改前建议前端先调一次，给出明确提示。
    /// </summary>
    Task<StudentDuplicateCheckResultDto> CheckDuplicateAsync(CheckStudentDuplicateDto input);
}
