using System.Linq.Expressions;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;

namespace Xjgl.Data;

/// <summary>
/// 业务仓储接口（**与 ORM 无关**，放在领域层）。
///
/// 为什么不用 ABP 自带的 <c>IRepository&lt;T&gt;</c>：
/// ABP 的仓储抽象建立在 <c>IQueryable&lt;T&gt;</c> 之上，而 SqlSugar 的
/// <c>ISugarQueryable&lt;T&gt;</c> **不实现** <c>IQueryable&lt;T&gt;</c>（已实测确认），
/// 因此业务仓储无法派生 ABP 的 RepositoryBase。
///
/// 本接口只暴露与 ORM 无关的能力：条件表达式 + 排序字符串 + 分页。
/// 需要 SqlSugar 原生能力（批量导入、原生 SQL、CodeFirst）时，
/// 使用 <c>Xjgl.SqlSugarCore.ISqlSugarRepository&lt;T&gt;</c>。
///
/// 实现：<c>Xjgl.SqlSugarCore.XjglRepository&lt;TEntity&gt;</c>
/// </summary>
public interface IXjglRepository<TEntity> : ITransientDependency
    where TEntity : class, IEntity, new()
{
    // ---- 单个查询 ----

    Task<TEntity> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TEntity?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TEntity> GetAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default);

    Task<TEntity?> FindAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default);

    // ---- 列表查询 ----

    Task<List<TEntity>> GetListAsync(CancellationToken cancellationToken = default);

    Task<List<TEntity>> GetListAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 带排序的列表查询（不分页）。
    /// <paramref name="sorting"/> 形如 <c>"Sort ASC, Code DESC"</c>，使用实体属性名。
    /// </summary>
    Task<List<TEntity>> GetSortedListAsync(
        Expression<Func<TEntity, bool>>? predicate,
        string? sorting,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 分页查询。
    /// <paramref name="sorting"/> 形如 <c>"Sort ASC, Code DESC"</c>，
    /// 使用**实体属性名**（由实现层翻译成数据库列名）。
    /// </summary>
    Task<List<TEntity>> GetPagedListAsync(
        Expression<Func<TEntity, bool>>? predicate,
        string? sorting,
        int skipCount,
        int maxResultCount,
        CancellationToken cancellationToken = default);

    // ---- 聚合 ----

    Task<long> GetCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 条件计数。
    ///
    /// <paramref name="predicate"/> 允许为 <c>null</c>，表示「不加过滤条件」——
    /// 这与无参重载等价。之所以必须显式允许，是因为列表接口的过滤条件是**可选**的：
    /// 调用方拼不出条件时自然会传 null（编译期有 CS8604 警告，很容易被忽略），
    /// 而底层 SqlSugar 收到 null 谓词会生成一条以 <c>WHERE</c> 结尾的残缺 SQL，
    /// 数据库直接报 <c>42601: syntax error at end of input</c>，
    /// 用户看到的是「查询全部」时报 500、「带条件查询」时正常 —— 极难排查。
    /// </summary>
    Task<long> GetCountAsync(
        Expression<Func<TEntity, bool>>? predicate,
        CancellationToken cancellationToken = default);

    Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default);

    // ---- 写入 ----

    Task<TEntity> InsertAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default);

    Task InsertManyAsync(
        IEnumerable<TEntity> entities,
        bool autoSave = false,
        CancellationToken cancellationToken = default);

    Task<TEntity> UpdateAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default);

    Task UpdateManyAsync(
        IEnumerable<TEntity> entities,
        bool autoSave = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// **只更新指定的若干列**（按实体属性名给出），其余列原样不动。
    ///
    /// 【为什么必须有这个方法，而不是"查出实体、改两个字段、整行写回"】
    ///   整行写回的语义是"以我手里这份快照为准"，而长耗时任务手里的快照必然是旧的：
    ///   导出跑几十秒期间用户可能点了取消、或另一处改了 Message，
    ///   整行覆盖会把那些修改**静默抹掉**（表现为"取消按钮点了没反应"）。
    ///   最典型、也是本方法第一个用户的场景是**进度上报**（`Processed`/`Total`）：
    ///   它每秒可能写一次，若每次都整行覆盖，任务表就变成"谁最后写谁赢"。
    ///
    /// 【代价】调用方要对"列名拼错"负责：拼错的列会被实现层**拒绝并抛异常**
    ///   （见实现里的校验），而不是永远写不进去 —— 后者表现为"进度永远不动"，
    ///   排查成本远高于一次响亮的失败。
    /// </summary>
    Task UpdateColumnsAsync(
        Guid id,
        IReadOnlyDictionary<string, object?> columns,
        bool autoSave = false,
        CancellationToken cancellationToken = default);

    /// <summary>删除（实体实现 ISoftDelete 时为软删除，并写入删除审计字段）。</summary>
    Task DeleteAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, bool autoSave = false, CancellationToken cancellationToken = default);

    Task DeleteManyAsync(
        IEnumerable<Guid> ids,
        bool autoSave = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按条件直接删除（绕过软删除，用于清理临时数据）。
    /// 返回受影响行数。
    /// </summary>
    Task<int> DeleteDirectAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>把当前事务中的变更提交到数据库。</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
