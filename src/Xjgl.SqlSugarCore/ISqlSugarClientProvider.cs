using SqlSugar;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// 提供当前工作单元内使用的 <see cref="ISqlSugarClient"/> 实例。
/// 生命周期为 Scoped：同一个 HTTP 请求（同一个 ABP 工作单元）里拿到的是同一个客户端。
/// </summary>
public interface ISqlSugarClientProvider
{
    /// <summary>获取当前作用域的 SqlSugar 客户端。</summary>
    ISqlSugarClient GetClient();

    /// <summary>获取一个独立的（不受当前工作单元事务影响的）客户端，用于后台任务、补偿任务等。</summary>
    ISqlSugarClient CreateIndependentClient();
}
