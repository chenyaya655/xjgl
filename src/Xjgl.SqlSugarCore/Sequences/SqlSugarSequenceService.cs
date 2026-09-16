using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Xjgl.Sequences;

namespace Xjgl.SqlSugarCore.Sequences;

/// <summary>
/// 基于 PostgreSQL 的取号实现。
///
/// 用 <c>pg_advisory_xact_lock</c> 对号段名加事务级建议锁，
/// 保证「按需建行 + 自增取值」这一整套动作在并发下不会发出重复号，
/// 同时避免为 sys_sequence 建唯一索引（领域层不描述索引，建表约定集中在集成层）。
///
/// 全流程在一个事务里完成：
///   1) 取号段名的建议锁（同名调用在此串行化）
///   2) 号段行不存在则插入
///   3) UPDATE ... RETURNING 原子自增并返回新值
///
/// 允许跳号：事务回滚时已取出的号会作废，这符合学籍号的业务预期
/// （唯一即可，不要求连续）。
/// </summary>
public class SqlSugarSequenceService : ISequenceService, ITransientDependency
{
    private readonly ISqlSugarClientAccessor _clientAccessor;
    private readonly ILogger<SqlSugarSequenceService> _logger;

    public SqlSugarSequenceService(
        ISqlSugarClientAccessor clientAccessor,
        ILogger<SqlSugarSequenceService> logger)
    {
        _clientAccessor = clientAccessor;
        _logger = logger;
    }

    public long NextValue(string name, int length = 0)
    {
        return NextRange(name, 1, length);
    }

    public long NextRange(string name, int count, int length = 0)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("号段名称不能为空。", nameof(name));
        }

        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "取号数量必须大于 0。");
        }

        var client = _clientAccessor.Client;
        var ownsTransaction = !client.Ado.IsAnyTran();

        if (ownsTransaction)
        {
            client.Ado.BeginTran();
        }

        try
        {
            // 1) 号段名建议锁（事务结束自动释放）
            client.Ado.SqlQuery<object>(
                "SELECT pg_advisory_xact_lock(hashtext(@Name))",
                new { Name = name });

            // 2) 确保号段行存在。
            //    注意带上 concurrency_stamp：ABP 的实体实现了 IHasConcurrencyStamp，
            //    该列是 NOT NULL，走原生 SQL 时不会由审计拦截器补上。
            client.Ado.ExecuteCommand(
                "INSERT INTO sys_sequence (id, name, current_value, length, is_deleted, creation_time, concurrency_stamp) " +
                "SELECT @Id, @Name, 0, @Length, false, now(), @Stamp " +
                "WHERE NOT EXISTS (SELECT 1 FROM sys_sequence WHERE name = @Name AND is_deleted = false)",
                new
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Length = length,
                    Stamp = Guid.NewGuid().ToString("N")
                });

            // 3) 原子自增并返回新值
            var rows = client.Ado.SqlQuery<long>(
                "UPDATE sys_sequence SET current_value = current_value + @Count " +
                "WHERE name = @Name AND is_deleted = false " +
                "RETURNING current_value",
                new { Name = name, Count = count });

            var endValue = rows.FirstOrDefault();

            if (endValue <= 0)
            {
                throw new InvalidOperationException($"号段「{name}」取号失败，未返回有效值。");
            }

            if (ownsTransaction)
            {
                client.Ado.CommitTran();
            }

            // 返回本次范围的**起始**号
            return endValue - count + 1;
        }
        catch (Exception ex)
        {
            if (ownsTransaction && client.Ado.IsAnyTran())
            {
                client.Ado.RollbackTran();
            }

            _logger.LogError(ex, "号段「{Name}」取号失败。", name);
            throw;
        }
    }
}
