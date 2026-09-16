using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Xjgl.Users;

/// <summary>
/// 用户预置与本人注册激活。
///
/// 设计要点（见方案文档第 10 章）：
///   1) 系统**不开放自助注册**，用户必须先在预置名单里；
///   2) 管理员按组织批量导入名单，系统生成一次性激活码；
///   3) 本人凭 手机号 + 姓名 +（可选）身份证号 + 激活码 激活，校验通过才建号；
///   4) 激活后由管理员在「授权」里分配模块与操作权限。
/// </summary>
public interface IUserProvisionAppService : IApplicationService
{
    // ---- 管理端 ----

    /// <summary>分页查询预置名单（可按组织子树、状态、关键字过滤）。</summary>
    Task<PagedResultDto<PredefineUserDto>> GetListAsync(PredefineUserQueryDto input);

    Task<PredefineUserDto> GetAsync(Guid id);

    Task<PredefineUserDto> CreateAsync(CreatePredefineUserDto input);

    /// <summary>批量导入（逐行校验，返回失败明细，不整体回滚）。</summary>
    Task<ImportPredefineResultDto> ImportAsync(ImportPredefineUsersDto input);

    /// <summary>重新生成激活码并延长有效期（用于过期或未收到通知的情况）。</summary>
    Task<PredefineUserDto> RenewActivationAsync(Guid id, int validDays = 30);

    /// <summary>撤销某条预置记录（不能是已激活的）。</summary>
    Task<PredefineUserDto> RevokeAsync(Guid id);

    /// <summary>删除未激活的预置记录。</summary>
    Task DeleteAsync(Guid id);

    // ---- 本人激活（无需登录）----

    /// <summary>本人激活账号。</summary>
    Task<ActivateUserResultDto> ActivateAsync(ActivateUserDto input);
}
