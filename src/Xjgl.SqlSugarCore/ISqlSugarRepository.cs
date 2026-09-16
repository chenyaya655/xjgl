using SqlSugar;
using Volo.Abp.Domain.Entities;
using Xjgl.Data;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// SqlSugar 原生仓储接口：在通用仓储之上，额外暴露 SqlSugar 的客户端与链式查询。
///
/// 使用场景：批量导入（<c>Fastest&lt;T&gt;().BulkCopy()</c>）、复杂联表统计、
/// 原生 SQL、CodeFirst 建表等 —— 这些是应用层的读模型 / 批处理服务才会用到的能力。
/// 普通增删改查请优先使用 <see cref="IXjglRepository{TEntity}"/>，保持应用层对 ORM 无感知。
/// </summary>
public interface ISqlSugarRepository<TEntity> : IXjglRepository<TEntity>
    where TEntity : class, IEntity, new()
{
    /// <summary>当前作用域的 SqlSugar 客户端。</summary>
    ISqlSugarClient Client { get; }

    /// <summary>链式查询入口。</summary>
    ISugarQueryable<TEntity> Queryable { get; }
}
