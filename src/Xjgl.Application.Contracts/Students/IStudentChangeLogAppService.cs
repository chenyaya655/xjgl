using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Xjgl.Students;

/// <summary>
/// 学籍变更流水的查询。
///
/// **只读**：留痕由业务操作在同一个工作单元里写入（见 <see cref="IStudentChangeLogger"/>），
/// 不提供"补写/修改/删除"接口 —— 能被改的审计日志等于没有审计。
/// </summary>
public interface IStudentChangeLogAppService : IApplicationService
{
    /// <summary>按学生查变更历史（分页，按时间倒序）。</summary>
    Task<ListResultDto<StudentChangeLogDto>> GetStudentLogsAsync(Guid studentId, int maxResultCount = 100);

    /// <summary>全局查询（按动作/时间过滤），用于县级以上的审计抽查。</summary>
    Task<PagedResultDto<StudentChangeLogDto>> GetListAsync(StudentChangeLogQueryDto input);
}
