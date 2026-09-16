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

    Task<long> GetCountAsync(
        Expression<Func<TEntity, bool>> predicate,
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
