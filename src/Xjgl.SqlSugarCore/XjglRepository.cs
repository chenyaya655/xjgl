using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Volo.Abp;
using Volo.Abp.Auditing;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Uow;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// SqlSugar 仓储实现。
///
/// 职责：
///   1) 用 SqlSugar 实现与 ORM 无关的 <see cref="Xjgl.Data.IXjglRepository{TEntity}"/>；
///   2) 收敛横切逻辑 —— 审计字段自动填充、软删除、工作单元事务、排序字符串翻译。
///
/// 由 ABP 的约定式注册自动注册（接口继承 ITransientDependency），
/// 因此不需要在模块里手写 AddTransient。
/// </summary>
public class XjglRepository<TEntity> : ISqlSugarRepository<TEntity>
    where TEntity : class, IEntity, new()
{
    protected ISqlSugarClientAccessor ClientAccessor { get; }
    protected IUnitOfWorkManager UnitOfWorkManager { get; }
    protected IAuditPropertySetter AuditPropertySetter { get; }
    protected ILogger Logger { get; }

    public XjglRepository(
        ISqlSugarClientAccessor clientAccessor,
        IUnitOfWorkManager unitOfWorkManager,
        IAuditPropertySetter auditPropertySetter,
        ILogger<XjglRepository<TEntity>> logger)
    {
        ClientAccessor = clientAccessor;
        UnitOfWorkManager = unitOfWorkManager;
        AuditPropertySetter = auditPropertySetter;
        Logger = logger;
    }

    public virtual ISqlSugarClient Client => ClientAccessor.Client;

    public virtual ISugarQueryable<TEntity> Queryable => Client.Queryable<TEntity>();

    // ------------------------------------------------------------------
    // 单个查询
    // ------------------------------------------------------------------

    public virtual async Task<TEntity> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await Queryable.InSingleAsync(id)
               ?? throw new EntityNotFoundException(typeof(TEntity), id);
    }

    public virtual Task<TEntity?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Queryable.InSingleAsync(id);
    }

    public virtual async Task<TEntity> GetAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await Queryable.Where(predicate).FirstAsync()
               ?? throw new EntityNotFoundException(typeof(TEntity), "查询条件未命中任何记录");
    }

    public virtual Task<TEntity?> FindAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return Queryable.Where(predicate).FirstAsync();
    }

    // ------------------------------------------------------------------
    // 列表查询
    // ------------------------------------------------------------------

    public virtual Task<List<TEntity>> GetListAsync(CancellationToken cancellationToken = default)
    {
        return Queryable.ToListAsync();
    }

    public virtual Task<List<TEntity>> GetListAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return Queryable.Where(predicate).ToListAsync();
    }

    public virtual Task<List<TEntity>> GetSortedListAsync(
        Expression<Func<TEntity, bool>>? predicate,
        string? sorting,
        CancellationToken cancellationToken = default)
    {
        var query = Queryable;

        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        return ApplySorting(query, sorting).ToListAsync();
    }

    public virtual Task<List<TEntity>> GetPagedListAsync(
        Expression<Func<TEntity, bool>>? predicate,
        string? sorting,
        int skipCount,
        int maxResultCount,
        CancellationToken cancellationToken = default)
    {
        var query = Queryable;

        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        query = ApplySorting(query, sorting);

        if (skipCount > 0)
        {
            query = query.Skip(skipCount);
        }

        if (maxResultCount > 0)
        {
            query = query.Take(maxResultCount);
        }

        return query.ToListAsync();
    }

    // ------------------------------------------------------------------
    // 聚合
    // ------------------------------------------------------------------

    public virtual async Task<long> GetCountAsync(CancellationToken cancellationToken = default)
    {
        return await Queryable.CountAsync();
    }

    public virtual async Task<long> GetCountAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await Queryable.Where(predicate).CountAsync();
    }

    public virtual Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return Queryable.AnyAsync(predicate);
    }

    // ------------------------------------------------------------------
    // 写入
    // ------------------------------------------------------------------

    public virtual async Task<TEntity> InsertAsync(
        TEntity entity,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
    {
        SetCreationAuditProperties(entity);

        await EnsureTransactionAsync();
        await Client.Insertable(entity).ExecuteCommandAsync();

        if (autoSave)
        {
            await SaveChangesAsync(cancellationToken);
        }

        return entity;
    }

    public virtual async Task InsertManyAsync(
        IEnumerable<TEntity> entities,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
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
            await SaveChangesAsync(cancellationToken);
        }
    }

    public virtual async Task<TEntity> UpdateAsync(
        TEntity entity,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
    {
        SetModificationAuditProperties(entity);

        await EnsureTransactionAsync();
        await Client.Updateable(entity).ExecuteCommandAsync();

        if (autoSave)
        {
            await SaveChangesAsync(cancellationToken);
        }

        return entity;
    }

    public virtual async Task UpdateManyAsync(
        IEnumerable<TEntity> entities,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
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
            await SaveChangesAsync(cancellationToken);
        }
    }

    public virtual async Task DeleteAsync(
        TEntity entity,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureTransactionAsync();

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
            await SaveChangesAsync(cancellationToken);
        }
    }

    public virtual async Task DeleteAsync(
        Guid id,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(id, cancellationToken);
        if (entity == null)
        {
            return;
        }

        await DeleteAsync(entity, autoSave, cancellationToken);
    }

    public virtual async Task DeleteManyAsync(
        IEnumerable<Guid> ids,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return;
        }

        await EnsureTransactionAsync();

        if (typeof(ISoftDelete).IsAssignableFrom(typeof(TEntity)))
        {
            var items = await Queryable.In(idList).ToListAsync();
            foreach (var item in items)
            {
                SetSoftDeleted(item);
                SetDeletionAuditProperties(item);
            }

            if (items.Count > 0)
            {
                await Client.Updateable(items).ExecuteCommandAsync();
            }
        }
        else
        {
            await Client.Deleteable<TEntity>().In(idList).ExecuteCommandAsync();
        }

        if (autoSave)
        {
            await SaveChangesAsync(cancellationToken);
        }
    }

    public virtual async Task<int> DeleteDirectAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        await EnsureTransactionAsync();
        return await Client.Deleteable<TEntity>().Where(predicate).ExecuteCommandAsync();
    }

    public virtual Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // SqlSugar 逐条执行即落库。保留该方法是为了与 ABP 的显式提交语义对齐：
        // 若运行在 ABP 工作单元事务中，真正的提交由工作单元完成时触发。
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // 横切逻辑
    // ------------------------------------------------------------------

    /// <summary>
    /// 排序字符串翻译：<c>"Sort ASC, Code DESC"</c>（实体属性名）→
    /// <c>"sort ASC, code DESC"</c>（数据库列名，snake_case）。
    ///
    /// 属性名 → 列名的转换规则必须与 <c>SqlSugarClientProvider</c> 里的建表约定保持一致。
    /// </summary>
    protected virtual ISugarQueryable<TEntity> ApplySorting(ISugarQueryable<TEntity> query, string? sorting)
    {
        if (string.IsNullOrWhiteSpace(sorting))
        {
            return query;
        }

        var builder = new StringBuilder();

        foreach (var part in sorting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            var propertyName = tokens[0];
            var descending = tokens.Length > 1 &&
                             tokens[1].Equals("DESC", StringComparison.OrdinalIgnoreCase);

            // 只允许实体上真实存在的属性，避免排序字段被拼进 SQL
            var property = typeof(TEntity).GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (property == null)
            {
                throw new BusinessException(XjglDomainErrorCodes.DataNotFound)
                    .WithData("reason", $"排序字段“{propertyName}”不是 {typeof(TEntity).Name} 的属性");
            }

            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(ToSnakeCase(property.Name));
            builder.Append(descending ? " DESC" : " ASC");
        }

        return builder.Length == 0 ? query : query.OrderBy(builder.ToString());
    }

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 若当前 ABP 工作单元要求事务而 SqlSugar 尚未开启事务，则在此开启，
    /// 并把提交/回滚挂到工作单元的生命周期上。
    ///
    /// 说明：SqlSugar 与 EF Core 各自持有事务。业务模块内部（纯 SqlSugar）
    /// 的原子性由此保证；需要跨两套 ORM 严格原子提交的场景，
    /// 通过共享 DbTransaction 增强（P1 阶段）。
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

        uow.OnCompleted(async () =>
        {
            if (Client.Ado.IsAnyTran())
            {
                await Client.Ado.CommitTranAsync();
            }
        });

        // ABP 的 IUnitOfWork 只提供 OnCompleted 与 Failed 事件（没有 OnFailed/OnDisposed）
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
    /// 可写的是具体实体类上的同名属性，因此用反射写入并按类型缓存 PropertyInfo。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo?> SoftDeletePropertyCache = new();

    protected virtual void SetSoftDeleted(object entity)
    {
        var property = SoftDeletePropertyCache.GetOrAdd(
            entity.GetType(),
            type => type.GetProperty(nameof(ISoftDelete.IsDeleted)));

        if (property is { CanWrite: true })
        {
            property.SetValue(entity, true);
            return;
        }

        Logger.LogWarning(
            "实体 {Entity} 实现了 ISoftDelete，但 IsDeleted 属性不可写，软删除标记未生效。",
            entity.GetType().Name);
    }

    protected virtual void SetCreationAuditProperties(TEntity entity)
    {
        TrySetAudit(() => AuditPropertySetter.SetCreationProperties(entity), "创建");
    }

    protected virtual void SetModificationAuditProperties(TEntity entity)
    {
        TrySetAudit(() => AuditPropertySetter.SetModificationProperties(entity), "修改");
    }

    protected virtual void SetDeletionAuditProperties(TEntity entity)
    {
        TrySetAudit(() => AuditPropertySetter.SetDeletionProperties(entity), "删除");
    }

    private void TrySetAudit(Action action, string stage)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // 实体没有实现对应的审计接口时 ABP 会抛异常，这属于正常情况
            Logger.LogDebug(ex, "设置{Stage}审计字段跳过：{Entity}", stage, typeof(TEntity).Name);
        }
    }
}
