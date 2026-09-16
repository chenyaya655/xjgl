using Microsoft.Extensions.Logging;
using SqlSugar;
using Volo.Abp.DependencyInjection;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// SqlSugar 事务范围。
///
/// 【为什么用显式事务范围，而不是 ABP 的 [UnitOfWork] 自动事务】
///
/// 实测结论：ABP 的 <c>IUnitOfWork</c> 公开接口只有 OnCompleted / Failed / Disposed /
/// RollbackAsync / CompleteAsync / Items / Outer，**没有** TransactionApi、
/// 也没有 ServiceProvider（见探针输出）。第三方 ORM 无法把自己的事务接到
/// UoW 的事务提交链路上。
///
/// 而“在仓储里 BeginTran、在 uow.OnCompleted 里 Commit”这种写法有一个致命时序问题：
/// ABP 的 DataSeeder 会在 UoW 内部再创建一个 DI 子作用域，SqlSugar 客户端
/// （Scoped）随子作用域先被释放，而 OnCompleted 要等 UoW 完成时才触发 ——
/// 结果是事务在提交前就随连接释放被回滚，表现为「日志说插了 153 条，
/// 换个连接查却是 0 条」。
///
/// 因此：单条写操作直接自动提交；需要多步原子写时，用本接口显式包一层。
///
/// 用法：
/// <code>
/// await _tx.ExecuteAsync(async () =>
/// {
///     await _students.InsertAsync(student);
///     await _changeLogs.InsertAsync(log);
/// });
/// </code>
/// </summary>
public interface ISqlSugarTransactionScope
{
    Task<T> ExecuteAsync<T>(Func<Task<T>> action);

    Task ExecuteAsync(Func<Task> action);
}

/// <inheritdoc />
public class SqlSugarTransactionScope : ISqlSugarTransactionScope, ITransientDependency
{
    private readonly ISqlSugarClientAccessor _clientAccessor;
    private readonly ILogger<SqlSugarTransactionScope> _logger;

    public SqlSugarTransactionScope(
        ISqlSugarClientAccessor clientAccessor,
        ILogger<SqlSugarTransactionScope> logger)
    {
        _clientAccessor = clientAccessor;
        _logger = logger;
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
    {
        var client = _clientAccessor.Client;

        // 已在事务中（嵌套调用）：由最外层负责提交/回滚
        if (client.Ado.IsAnyTran())
        {
            return await action();
        }

        await client.Ado.BeginTranAsync();
        try
        {
            var result = await action();
            await client.Ado.CommitTranAsync();
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SqlSugar 事务执行失败，正在回滚。");

            if (client.Ado.IsAnyTran())
            {
                await client.Ado.RollbackTranAsync();
            }

            throw;
        }
    }

    public Task ExecuteAsync(Func<Task> action)
    {
        return ExecuteAsync(async () =>
        {
            await action();
            return true;
        });
    }
}
