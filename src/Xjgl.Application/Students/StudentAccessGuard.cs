using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Xjgl.Data;
using Xjgl.Organizations;

namespace Xjgl.Students;

/// <summary>
/// 学生（及其子表）的数据范围守卫。
///
/// 学生详情下挂着监护人、学习简历、奖励、处分、资助、体检六张子表，
/// 每一次读写都要先回答同一个问题：「这个学生你看得到吗」。
/// 把这段判断集中在这里，是为了避免出现"主档挡住了、子表接口忘了挡"的越权漏洞
/// —— 子表接口是独立路由，**不会**自动继承主档的过滤条件。
/// </summary>
public interface IStudentAccessGuard
{
    /// <summary>按 Id 取学生，并校验数据范围；不存在抛 EntityNotFound，越权抛业务异常。</summary>
    Task<StuStudent> GetInScopeAsync(Guid studentId, CancellationToken cancellationToken = default);

    /// <summary>校验一条已取出的学生是否在当前用户的数据范围内。</summary>
    Task EnsureInScopeAsync(StuStudent student, CancellationToken cancellationToken = default);

    /// <summary>校验某个组织是否在数据范围内（新增/改归属时用）。</summary>
    Task EnsureOrgInScopeAsync(SysOrg org, string reason, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class StudentAccessGuard : IStudentAccessGuard, ITransientDependency
{
    private readonly IXjglRepository<StuStudent> _students;
    private readonly IUserDataScopeProvider _dataScopeProvider;

    public StudentAccessGuard(
        IXjglRepository<StuStudent> students,
        IUserDataScopeProvider dataScopeProvider)
    {
        _students = students;
        _dataScopeProvider = dataScopeProvider;
    }

    /// <inheritdoc />
    public async Task<StuStudent> GetInScopeAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        var student = await _students.GetAsync(studentId, cancellationToken);

        await EnsureInScopeAsync(student, cancellationToken);

        return student;
    }

    /// <inheritdoc />
    public async Task EnsureInScopeAsync(StuStudent student, CancellationToken cancellationToken = default)
    {
        var scope = await _dataScopeProvider.GetAsync();

        // 越权访问明确抛异常而不是返回空：调用方需要区分
        // 「这条数据存在但你看不到」与「这条数据不存在」
        if (!scope.AllowsEntityPath(student.SchoolPathCode))
        {
            throw new BusinessException(XjglDomainErrorCodes.UserNotInDataScope)
                .WithData("reason", "该学生不在您的数据范围内");
        }
    }

    /// <inheritdoc />
    public async Task EnsureOrgInScopeAsync(
        SysOrg org,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var scope = await _dataScopeProvider.GetAsync();

        if (!scope.AllowsRequestedOrgPath(org.PathCode))
        {
            throw new BusinessException(XjglDomainErrorCodes.UserNotInDataScope)
                .WithData("reason", reason);
        }
    }
}
