using SqlSugar;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// 作用域内的 SqlSugar 客户端持有者（Scoped）。
/// </summary>
public interface ISqlSugarClientAccessor
{
    ISqlSugarClient Client { get; }
}

/// <inheritdoc />
public class SqlSugarClientAccessor : ISqlSugarClientAccessor, IDisposable
{
    private readonly Lazy<ISqlSugarClient> _lazyClient;

    public SqlSugarClientAccessor(SqlSugarClientProvider provider)
    {
        _lazyClient = new Lazy<ISqlSugarClient>(provider.BuildClient, isThreadSafe: true);
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
