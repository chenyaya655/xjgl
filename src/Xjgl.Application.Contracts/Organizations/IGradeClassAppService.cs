using Volo.Abp.Application.Services;

namespace Xjgl.Organizations;

/// <summary>
/// 年级与班级管理（学年维度）。
///
/// 年级班级不进组织树（见 <see cref="SysGrade"/> 的类注释），
/// 而是挂在「学校 + 学年」下，因此每年升级只是一次数据操作，不动组织与权限。
/// </summary>
public interface IGradeClassAppService : IApplicationService
{
    // ---- 年级 ----

    Task<List<GradeDto>> GetGradesAsync(Guid schoolOrgId, string academicYear);

    Task<GradeDto> CreateGradeAsync(CreateUpdateGradeDto input);

    Task<GradeDto> UpdateGradeAsync(Guid id, CreateUpdateGradeDto input);

    /// <summary>删除年级（年级下还有班级时拒绝）。</summary>
    Task DeleteGradeAsync(Guid id);

    // ---- 班级 ----

    Task<List<ClassDto>> GetClassesAsync(Guid schoolOrgId, string? academicYear = null, Guid? gradeId = null);

    Task<ClassDto> CreateClassAsync(CreateUpdateClassDto input);

    Task<ClassDto> UpdateClassAsync(Guid id, CreateUpdateClassDto input);

    Task DeleteClassAsync(Guid id);

    // ---- 批量生成 ----

    /// <summary>
    /// 按学年批量生成年级。
    /// 未指定年级码时按学校类型推断默认范围（小学 1-6、初中 7-9、高中 10-12 等）。
    /// 已存在的年级会被跳过（幂等）。
    /// </summary>
    Task<BatchGenerateResultDto<GradeDto>> BatchGenerateGradesAsync(BatchGenerateGradesDto input);

    /// <summary>
    /// 为某个年级批量生成 N 个班级。已存在的班号会被跳过（幂等）。
    /// </summary>
    Task<BatchGenerateResultDto<ClassDto>> BatchGenerateClassesAsync(BatchGenerateClassesDto input);
}
