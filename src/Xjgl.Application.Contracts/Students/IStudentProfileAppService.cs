using Volo.Abp.Application.Services;

namespace Xjgl.Students;

/// <summary>
/// 学生档案子表（监护人 / 学习简历 / 奖励 / 处分 / 资助 / 体检）。
///
/// **权限复用 <c>Stu.Student.*</c>，不新增权限码。** 理由：这些子表是"学生信息"的组成部分，
/// 单独设权限只会让授权配置变得难维护（每加一张子表就要配一套码）。
/// 需要更细的粒度时，再在 <c>sys_module_action</c> 里加操作即可（权限是元数据驱动的，加码不用改代码）。
///
/// **每个方法都会先校验学生主档的数据范围** —— 子表接口是独立路由，
/// 不会自动继承主档接口的过滤条件，漏掉一步就是越权读取家长身份信息。
/// </summary>
public interface IStudentProfileAppService : IApplicationService
{
    // ---- 监护人 ----

    Task<List<GuardianDto>> GetGuardiansAsync(Guid studentId);

    Task<GuardianDto> CreateGuardianAsync(Guid studentId, CreateUpdateGuardianDto input);

    Task<GuardianDto> UpdateGuardianAsync(Guid id, CreateUpdateGuardianDto input);

    Task DeleteGuardianAsync(Guid id);

    // ---- 学习简历 ----

    Task<List<ResumeDto>> GetResumesAsync(Guid studentId);

    Task<ResumeDto> CreateResumeAsync(Guid studentId, CreateUpdateResumeDto input);

    Task<ResumeDto> UpdateResumeAsync(Guid id, CreateUpdateResumeDto input);

    Task DeleteResumeAsync(Guid id);

    // ---- 奖励 ----

    Task<List<RewardDto>> GetRewardsAsync(Guid studentId);

    Task<RewardDto> CreateRewardAsync(Guid studentId, CreateUpdateRewardDto input);

    Task<RewardDto> UpdateRewardAsync(Guid id, CreateUpdateRewardDto input);

    Task DeleteRewardAsync(Guid id);

    // ---- 处分 ----

    Task<List<PunishmentDto>> GetPunishmentsAsync(Guid studentId);

    Task<PunishmentDto> CreatePunishmentAsync(Guid studentId, CreateUpdatePunishmentDto input);

    Task<PunishmentDto> UpdatePunishmentAsync(Guid id, CreateUpdatePunishmentDto input);

    Task DeletePunishmentAsync(Guid id);

    // ---- 资助 ----

    Task<List<SubsidyDto>> GetSubsidiesAsync(Guid studentId);

    Task<SubsidyDto> CreateSubsidyAsync(Guid studentId, CreateUpdateSubsidyDto input);

    Task<SubsidyDto> UpdateSubsidyAsync(Guid id, CreateUpdateSubsidyDto input);

    Task DeleteSubsidyAsync(Guid id);

    // ---- 体质健康（体检）----

    Task<List<PhysicalDto>> GetPhysicalsAsync(Guid studentId);

    Task<PhysicalDto> CreatePhysicalAsync(Guid studentId, CreateUpdatePhysicalDto input);

    Task<PhysicalDto> UpdatePhysicalAsync(Guid id, CreateUpdatePhysicalDto input);

    Task DeletePhysicalAsync(Guid id);
}
