using SqlSugar;
using Volo.Abp.DependencyInjection;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// 当前工作单元（作用域）内的 SqlSugar 客户端持有者。
/// 同一个 HTTP 请求 / 同一个 ABP 工作单元里拿到的是同一个客户端实例，
/// 事务状态才能连贯。
/// </summary>
public interface ISqlSugarClientAccessor
{
    ISqlSugarClient Client { get; }
}

/// <inheritdoc />
public class SqlSugarClientAccessor : ISqlSugarClientAccessor, IScopedDependency, IDisposable
{
    private readonly Lazy<ISqlSugarClient> _lazyClient;

    public SqlSugarClientAccessor(ISqlSugarClientFactory factory)
    {
        // 延迟创建：不访问数据库的请求不会白白开连接
        _lazyClient = new Lazy<ISqlSugarClient>(factory.Create, isThreadSafe: true);
    }

    public ISqlSugarClient Client => _lazyClient.Value;

    public void Dispose()
    {
        if (_lazyClient.IsValueCreated)
        {
            _lazyClient.Value.Dispose();
        }
    }
}
