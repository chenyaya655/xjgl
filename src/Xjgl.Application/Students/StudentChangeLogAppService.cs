using System.Linq.Expressions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Domain.Entities;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Permissions;

namespace Xjgl.Students;

/// <summary>
/// 学籍变更流水的查询实现。
///
/// 两件事必须做对：
///   1. **数据范围**：留痕里带着学生姓名与身份证摘要，越权读到别的县的记录同样是泄露。
///      按学生的做法用**物化路径前缀**过滤（留痕表上冗余了 `SchoolPathCode`），
///      而不是"先查出学生 Id 再拼 IN" —— 后者在省厅视角下会拼出上万个 Id。
///   2. **只读**：没有写入接口，留痕只能由业务操作产生。
/// </summary>
[Authorize]
public class StudentChangeLogAppService : XjglAppService, IStudentChangeLogAppService
{
    /// <summary>
    /// 用来表达"这个范围不可能匹配任何路径"的哨兵前缀。
    ///
    /// 为什么不直接返回空：查询谓词在 SqlSugar 里无法用"恒假表达式"短路
    /// （`s => false` 会抛异常，本项目实测踩过），因此只能给一个绝不可能命中的前缀。
    /// </summary>
    private const string ImpossiblePathPrefix = "\u0001";

    private const string QueryPermission = MetadataPermissions.Stu.Student.Query;

    private readonly IXjglRepository<StuChangeLog> _logs;
    private readonly IXjglRepository<StuStudent> _students;
    private readonly IStudentAccessGuard _guard;
    private readonly IUserDataScopeProvider _dataScopeProvider;

    public StudentChangeLogAppService(
        IXjglRepository<StuChangeLog> logs,
        IXjglRepository<StuStudent> students,
        IStudentAccessGuard guard,
        IUserDataScopeProvider dataScopeProvider)
    {
        _logs = logs;
        _students = students;
        _guard = guard;
        _dataScopeProvider = dataScopeProvider;
    }

    /// <inheritdoc />
    [Authorize(QueryPermission)]
    public async Task<ListResultDto<StudentChangeLogDto>> GetStudentLogsAsync(
        Guid studentId,
        int maxResultCount = 100)
    {
        var rows = await _logs.GetPagedListAsync(
            l => l.StudentId == studentId,
            "OccurredAt DESC",
            0,
            Math.Clamp(maxResultCount, 1, 500));

        // 一条流水都没有：这个学生不存在（或者是刚建还没留下任何痕迹的异常情况）。
        // 返回 404 而不是空列表，调用方才能区分"没有变更"与"没有这个人"。
        if (rows.Count == 0)
        {
            throw new EntityNotFoundException(typeof(StuStudent), studentId);
        }

        var scope = await _dataScopeProvider.GetAsync();

        var student = await _students.FindAsync(studentId);

        if (student != null)
        {
            // 学生还在：按学生当前的数据范围判断（他的历史记录可能属于已经转出的学校，
            // 只要他现在在你看得见的范围里，就应当能看到完整履历）
            await _guard.EnsureInScopeAsync(student);
        }
        else if (!rows.Any(r => scope.AllowsEntityPath(r.SchoolPathCode)))
        {
            // 学生已被删除：**这种场景正是留痕存在的理由**，不能因为"查不到学生"就报 404。
            // 此时退而校验流水上的学校路径（写入时冗余下来的），
            // 且只要有一条在范围内就放行（转学过的学生，旧记录属于原学校）。
            throw new BusinessException(XjglDomainErrorCodes.UserNotInDataScope)
                .WithData("reason", "该学生已删除，其学籍变更流水不在您的数据范围内");
        }

        return new ListResultDto<StudentChangeLogDto>(rows.Select(ToDto).ToList());
    }

    /// <inheritdoc />
    [Authorize(QueryPermission)]
    public async Task<PagedResultDto<StudentChangeLogDto>> GetListAsync(StudentChangeLogQueryDto input)
    {
        var scope = await _dataScopeProvider.GetAsync();

        // 未关联组织且非超管：返回空列表而不是报错（与列表页一致的产品行为）
        if (scope.IsEmpty)
        {
            return new PagedResultDto<StudentChangeLogDto>(0, new List<StudentChangeLogDto>());
        }

        var scopePathCode = scope.IsUnrestricted ? null : scope.OrgPathCode;
        var scopeChildPrefix = scopePathCode == null
            ? null
            : (scope.Scope == DataScopes.OrgOnly ? ImpossiblePathPrefix : scopePathCode + ".");
        var hasScopePath = !string.IsNullOrWhiteSpace(scopePathCode);
        var hasScopeChildPrefix = !string.IsNullOrWhiteSpace(scopeChildPrefix);

        var hasAction = !input.Action.IsNullOrWhiteSpace();
        var hasFrom = input.From.HasValue;
        var hasTo = input.To.HasValue;

        // 值必须先在表达式外算好：直接在谓词里写三元或调用方法，SqlSugar 翻译不了
        // （本项目在学员与照片模块都踩过：`s => false` 抛异常、三元变成 SqlFunc.IIF 报
        //  "argument do not support"）
        var action = input.Action?.Trim();
        var from = input.From ?? default;
        var to = input.To ?? default;

        Expression<Func<StuChangeLog, bool>>? predicate =
            scope.IsUnrestricted && !hasAction && !hasFrom && !hasTo
                ? null
                : l =>
                    (scope.IsUnrestricted
                     || (hasScopePath && l.SchoolPathCode == scopePathCode)
                     || (hasScopeChildPrefix && l.SchoolPathCode != null
                         && l.SchoolPathCode.StartsWith(scopeChildPrefix!)))
                    && (!hasAction || l.Action == action)
                    && (!hasFrom || l.OccurredAt >= from)
                    && (!hasTo || l.OccurredAt <= to);

        var total = await _logs.GetCountAsync(predicate);
        var sorting = input.Sorting ?? "OccurredAt DESC";

        var rows = await _logs.GetPagedListAsync(
            predicate,
            sorting,
            input.SkipCount,
            input.MaxResultCount);

        return new PagedResultDto<StudentChangeLogDto>(total, rows.Select(ToDto).ToList());
    }

    private static StudentChangeLogDto ToDto(StuChangeLog log)
    {
        return new StudentChangeLogDto
        {
            Id = log.Id,
            StudentId = log.StudentId,
            StudentNo = log.StudentNo,
            StudentName = log.StudentName,
            BatchId = log.BatchId,
            Action = log.Action,
            ActionName = StudentChangeActions.GetName(log.Action),
            FieldName = log.FieldName,
            FieldLabel = log.FieldLabel,
            OldValue = log.OldValue,
            NewValue = log.NewValue,
            Reason = log.Reason,
            OperatorId = log.OperatorId,
            OperatorName = log.OperatorName,
            OccurredAt = log.OccurredAt,
            Source = log.Source
        };
    }
}
