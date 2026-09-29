using Microsoft.Extensions.Logging;
using SqlSugar;
using Volo.Abp.DependencyInjection;
using Xjgl.Data;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// SqlSugar 事务范围的实现（接口在 <c>Xjgl.Domain.Data.IXjglTransactionScope</c> —— 债 1）。
///
/// 【为什么用显式事务范围，而不是 ABP 的 [UnitOfWork] 自动事务】
///
/// 已用**枚举 <c>IUnitOfWork</c> 全部公开成员**的方式核对过（不是"按名字搜不到就断定没有"
/// —— 本仓库为这种查法栽过）：接口上只有
/// Id / IsCompleted / IsDisposed / IsReserved / ReservationName / Items / Options / Outer
/// 以及 CompleteAsync / RollbackAsync / SaveChangesAsync / Initialize / Reserve / 事件，
/// **没有** TransactionApi，也**没有** ServiceProvider。
/// 因此第三方 ORM 无法把自己的事务挂到 UoW 的提交链路上。
///
/// 另一个被实测否掉的写法："在仓储里 BeginTran、在 <c>uow.OnCompleted</c> 里 Commit"
/// 有一个致命时序问题：ABP 的 DataSeeder 会在 UoW 内部再创建一个 DI 子作用域，
/// SqlSugar 客户端（Scoped）随子作用域先被释放，而 OnCompleted 要等 UoW 完成才触发 ——
/// 事务在提交前就随连接释放被回滚，表现为「日志说插了 153 条，换个连接查却是 0 条」。
///
/// 所以规则是：**单条写操作直接自动提交；需要多步原子写时用本接口显式包一层。**
///
/// 【它保证什么、不保证什么（说清楚，免得被当成万能）】
///   ✅ 保证：**同一个 SqlSugar 客户端上的多次写**全成或全败（业务表之间原子）。
///   ❌ 不保证：跨到 EF Core 框架表（ABP 的 Identity / 权限 / 审计）的原子性。
///      本项目的 <c>AbpUnitOfWorkDefaultOptions</c> 从未配置过 `IsTransactional`，
///      即 EF Core 的 UoW **默认不开事务**、各自 <c>SaveChanges</c> 提交 ——
///      两套 ORM 用的是两条连接、两个事务，没有共享机制。
///      真正的跨 ORM 原子性需要在 Domain 引入 Outbox（先落"待执行意图"，
///      再由后台任务重试到成功），那是独立的架构改动，不在本次范围内。
/// </summary>
/// <summary>
/// 【为什么显式写 <see cref="ExposeServicesAttribute"/>】
/// ABP 的约定式注册对"实现了 <c>ITransientDependency</c> 的类"**只把它注册成它自己**，
/// 不会顺带把它注册成它实现的自定义接口。实测（容器探针，不是猜）：
///   · 解析 <c>SqlSugarTransactionScope</c> 类 → OK
///   · 解析 <c>IXjglTransactionScope</c> 接口 → "has not been registered"
/// 而应用层注入的是**接口**，于是 `ChangeFlowAppService` 直接构造失败、
/// 整条审批链路全挂（表现为"事务改造把审批搞坏了"）。
/// 在实现类上显式声明暴露哪个服务，是唯一可靠的写法。
///
/// 这个坑值得记住：**"类能被解析"不等于"接口能被解析"** ——
/// 只探实现类会得出"注册没问题"的错误结论。
/// </summary>
[ExposeServices(typeof(IXjglTransactionScope))]
public class SqlSugarTransactionScope : IXjglTransactionScope, ITransientDependency
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

        // 已在事务中（嵌套调用）：由最外层负责提交/回滚。
        // 这条分支是"服务里再调另一个也开了事务范围的服务"时的正确行为 ——
        // 内层若自己提交，外层的回滚就回不到内层已提交的部分了。
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
