using Microsoft.Extensions.Logging;
using SqlSugar;
using Volo.Abp;
using Volo.Abp.Auditing;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Uow;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// SqlSugar 原生仓储接口。
///
/// 说明：SqlSugar 的 <c>ISugarQueryable&lt;T&gt;</c> 并不实现 <c>IQueryable&lt;T&gt;</c>
/// （已用探针实测确认），因此无法派生 ABP 的 RepositoryBase。
/// 本接口直接暴露 SqlSugar 的链式查询能力，同时把审计字段、软删除、
/// 工作单元事务等横切逻辑收敛在 <see cref="XjglRepository{TEntity}"/> 里。
/// </summary>
public interface IXjglRepository<TEntity> : Volo.Abp.DependencyInjection.ITransientDependency
    where TEntity : class, IEntity, new()
{
    /// <summary>当前作用域的 SqlSugar 客户端（可执行任意原生操作，如批量导入）。</summary>
    ISqlSugarClient Client { get; }

    /// <summary>链式查询入口。</summary>
    ISugarQueryable<TEntity> Queryable { get; }

    Task<TEntity> GetAsync(Guid id);

    Task<TEntity?> FindAsync(Guid id);

    Task<List<TEntity>> GetListAsync();

    Task<List<TEntity>> GetPagedListAsync(int skipCount, int maxResultCount, string? sorting = null);

    Task<long> GetCountAsync();

    Task<TEntity> InsertAsync(TEntity entity, bool autoSave = false);

    Task InsertManyAsync(IEnumerable<TEntity> entities, bool autoSave = false);

    Task<TEntity> UpdateAsync(TEntity entity, bool autoSave = false);

    Task UpdateManyAsync(IEnumerable<TEntity> entities, bool autoSave = false);

    Task DeleteAsync(TEntity entity, bool autoSave = false);

    Task DeleteAsync(Guid id, bool autoSave = false);

    Task DeleteManyAsync(IEnumerable<Guid> ids, bool autoSave = false);

    /// <summary>把当前 SqlSugar 事务的变更提交到数据库（配合 autoSave=false 使用）。</summary>
    Task SaveChangesAsync();
}

/// <inheritdoc />
public class XjglRepository<TEntity> : IXjglRepository<TEntity>
    where TEntity : class, IEntity, new()
{
    protected ISqlSugarClientProvider ClientProvider { get; }
    protected IUnitOfWorkManager UnitOfWorkManager { get; }
    protected IAuditPropertySetter AuditPropertySetter { get; }
    protected ILogger Logger { get; }

    public XjglRepository(
        ISqlSugarClientProvider clientProvider,
        IUnitOfWorkManager unitOfWorkManager,
        IAuditPropertySetter auditPropertySetter,
        ILogger<XjglRepository<TEntity>> logger)
    {
        ClientProvider = clientProvider;
        UnitOfWorkManager = unitOfWorkManager;
        AuditPropertySetter = auditPropertySetter;
        Logger = logger;
    }

    public virtual ISqlSugarClient Client => ClientProvider.GetClient();

    public virtual ISugarQueryable<TEntity> Queryable => Client.Queryable<TEntity>();

    public virtual async Task<TEntity> GetAsync(Guid id)
    {
        return await Queryable.InSingleAsync(id)
               ?? throw new EntityNotFoundException(typeof(TEntity), id);
    }

    public virtual Task<TEntity?> FindAsync(Guid id)
    {
        return Queryable.InSingleAsync(id);
    }

    public virtual Task<List<TEntity>> GetListAsync()
    {
        return Queryable.ToListAsync();
    }

    public virtual Task<List<TEntity>> GetPagedListAsync(int skipCount, int maxResultCount, string? sorting = null)
    {
        var query = Queryable;
        if (!sorting.IsNullOrWhiteSpace())
        {
            query = query.OrderBy(sorting!);
        }

        return query.Skip(skipCount).Take(maxResultCount).ToListAsync();
    }

    public virtual async Task<long> GetCountAsync()
    {
        return await Queryable.CountAsync();
    }

    public virtual async Task<TEntity> InsertAsync(TEntity entity, bool autoSave = false)
    {
        SetCreationAuditProperties(entity);

        await EnsureTransactionAsync();
        await Client.Insertable(entity).ExecuteCommandAsync();

        if (autoSave)
        {
            await SaveChangesAsync();
        }

        return entity;
    }

    public virtual async Task InsertManyAsync(IEnumerable<TEntity> entities, bool autoSave = false)
    {
        var list = entities.ToList();
        if (list.Count == 0)
        {
            return;
        }

        foreach (var entity in list)
        {
            SetCreationAuditProperties(entity);
        }

        await EnsureTransactionAsync();
        await Client.Insertable(list).ExecuteCommandAsync();

        if (autoSave)
        {
            await SaveChangesAsync();
        }
    }

    public virtual async Task<TEntity> UpdateAsync(TEntity entity, bool autoSave = false)
    {
        SetModificationAuditProperties(entity);

        await EnsureTransactionAsync();
        await Client.Updateable(entity).ExecuteCommandAsync();

        if (autoSave)
        {
            await SaveChangesAsync();
        }

        return entity;
    }

    public virtual async Task UpdateManyAsync(IEnumerable<TEntity> entities, bool autoSave = false)
    {
        var list = entities.ToList();
        if (list.Count == 0)
        {
            return;
        }

        foreach (var entity in list)
        {
            SetModificationAuditProperties(entity);
        }

        await EnsureTransactionAsync();
        await Client.Updateable(list).ExecuteCommandAsync();

        if (autoSave)
        {
            await SaveChangesAsync();
        }
    }

    public virtual async Task DeleteAsync(TEntity entity, bool autoSave = false)
    {
        await EnsureTransactionAsync();

        // 软删除优先
        if (entity is ISoftDelete)
        {
            SetSoftDeleted(entity);
            SetDeletionAuditProperties(entity);
            await Client.Updateable(entity).ExecuteCommandAsync();
        }
        else
        {
            await Client.Deleteable(entity).ExecuteCommandAsync();
        }

        if (autoSave)
        {
            await SaveChangesAsync();
        }
    }

    public virtual async Task DeleteAsync(Guid id, bool autoSave = false)
    {
        var entity = await FindAsync(id);
        if (entity == null)
        {
            return;
        }

        await DeleteAsync(entity, autoSave);
    }

    public virtual async Task DeleteManyAsync(IEnumerable<Guid> ids, bool autoSave = false)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return;
        }

        await EnsureTransactionAsync();

        if (typeof(ISoftDelete).IsAssignableFrom(typeof(TEntity)))
        {
            // 软删除：逐条走 Update，保证删除审计字段被写入
            var items = await Queryable.In(idList).ToListAsync();
            foreach (var item in items)
            {
                if (item is ISoftDelete)
                {
                    SetSoftDeleted(item);
                    SetDeletionAuditProperties(item);
                }
            }

            await Client.Updateable(items).ExecuteCommandAsync();
        }
        else
        {
            await Client.Deleteable<TEntity>().In(idList).ExecuteCommandAsync();
        }

        if (autoSave)
        {
            await SaveChangesAsync();
        }
    }

    public virtual Task SaveChangesAsync()
    {
        // SqlSugar 逐条执行即落库，这里保留方法是为了与 ABP 的显式提交语义对齐；
        // 若运行在 ABP 工作单元事务中，真正的提交由工作单元完成时触发。
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // 横切逻辑
    // ------------------------------------------------------------------

    /// <summary>
    /// 如果当前 ABP 工作单元要求事务，而 SqlSugar 还没开启事务，就在这里开启，
    /// 并把提交/回滚挂到工作单元的生命周期上。
    ///
    /// 注意：这样 SqlSugar 与 EF Core 各自持有事务；
    /// 需要跨 ORM 严格原子提交时，在 P1 阶段通过共享 DbTransaction 增强。
    /// </summary>
    protected virtual async Task EnsureTransactionAsync()
    {
        var uow = UnitOfWorkManager.Current;
        if (uow == null || !uow.Options.IsTransactional)
        {
            return;
        }

        if (Client.Ado.IsAnyTran())
        {
            return;
        }

        await Client.Ado.BeginTranAsync();

        // 工作单元成功完成 → 提交
        uow.OnCompleted(async () =>
        {
            if (Client.Ado.IsAnyTran())
            {
                await Client.Ado.CommitTranAsync();
            }
        });

        // 工作单元失败 → 回滚。
        // ABP 的 IUnitOfWork 只提供 OnCompleted 与 Failed 事件（无 OnFailed/OnDisposed），
        // 因此这里挂在 Failed 事件上，用同步 API 回滚。
        uow.Failed += (_, _) =>
        {
            if (Client.Ado.IsAnyTran())
            {
                Client.Ado.RollbackTran();
            }
        };
    }

    /// <summary>
    /// 标记软删除。
    /// ABP 10 的 <see cref="ISoftDelete.IsDeleted"/> 是只读接口属性，
    /// 真正可写的是具体实体类上的同名属性（如 FullAuditedAggregateRoot），
    /// 因此这里用反射写入，并按类型缓存 PropertyInfo。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> SoftDeletePropertyCache = new();

    protected virtual void SetSoftDeleted(object entity)
    {
        var property = SoftDeletePropertyCache.GetOrAdd(
            entity.GetType(),
            type => type.GetProperty(nameof(ISoftDelete.IsDeleted)));

        if (property is { CanWrite: true })
        {
            property.SetValue(entity, true);
        }
        else
        {
            Logger.LogWarning(
                "实体 {Entity} 实现了 ISoftDelete，但其 IsDeleted 属性不可写，软删除标记未生效。",
                entity.GetType().Name);
        }
    }

    protected virtual void SetCreationAuditProperties(TEntity entity)
    {
        try
        {
            AuditPropertySetter.SetCreationProperties(entity);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "设置创建审计字段失败：{Entity}", typeof(TEntity).Name);
        }
    }

    protected virtual void SetModificationAuditProperties(TEntity entity)
    {
        try
        {
            AuditPropertySetter.SetModificationProperties(entity);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "设置修改审计字段失败：{Entity}", typeof(TEntity).Name);
        }
    }

    protected virtual void SetDeletionAuditProperties(TEntity entity)
    {
        try
        {
            AuditPropertySetter.SetDeletionProperties(entity);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "设置删除审计字段失败：{Entity}", typeof(TEntity).Name);
        }
    }
}
