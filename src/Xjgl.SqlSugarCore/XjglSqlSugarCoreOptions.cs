using SqlSugar;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// SqlSugar 集成层配置项。
/// </summary>
public class XjglSqlSugarCoreOptions
{
    /// <summary>要使用的连接串名称（对应 ConnectionStrings 节点），默认 Default。</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>数据库类型，本系统固定为 PostgreSQL。</summary>
    public DbType DbType { get; set; } = DbType.PostgreSQL;

    /// <summary>是否输出 SQL 日志（开发环境开启）。</summary>
    public bool EnableSqlLog { get; set; }

    /// <summary>命令超时（秒）。</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>是否自动把实体名 PascalCase 映射为 snake_case 表名。</summary>
    public bool UseSnakeCaseTableName { get; set; } = true;
}
