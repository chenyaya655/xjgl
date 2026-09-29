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

    /// <summary>
    /// 链式查询入口。
    ///
    /// ★ 这里自动附加了软删除过滤：实体实现 <see cref="ISoftDelete"/> 时，
    ///   所有读操作（Get / Find / List / Count / Any）都只看到未删除的数据。
    ///   漏掉这个过滤就会出现「删除成功但还能查到」的假象 ——
    ///   集成测试正是靠这一点发现的（见 OrgAppServiceTests / CodeAppServiceTests）。
    ///
    /// 需要查已删除数据（如回收站）时，用 <see cref="QueryableIncludingDeleted"/>。
    /// </summary>
    public virtual ISugarQueryable<TEntity> Queryable
    {
        get
        {
            var query = Client.Queryable<TEntity>();
            var filter = GetSoftDeleteFilter();

            return filter == null ? query : query.Where(filter);
        }
    }

    /// <summary>不过滤软删除的查询入口（回收站、数据恢复等场景）。</summary>
    public virtual ISugarQueryable<TEntity> QueryableIncludingDeleted => Client.Queryable<TEntity>();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, LambdaExpression?> SoftDeleteFilterCache = new();

    /// <summary>
    /// 构造 <c>e =&gt; !e.IsDeleted</c> 表达式并按类型缓存。
    /// 实体未实现 ISoftDelete 时返回 null，不做任何过滤。
    /// </summary>
    private static Expression<Func<TEntity, bool>>? GetSoftDeleteFilter()
    {
        var cached = SoftDeleteFilterCache.GetOrAdd(typeof(TEntity), static type =>
        {
            if (!typeof(ISoftDelete).IsAssignableFrom(type))
            {
                return null;
            }

            var property = type.GetProperty(nameof(ISoftDelete.IsDeleted));
            if (property == null || property.PropertyType != typeof(bool))
            {
                return null;
            }

            var parameter = Expression.Parameter(type, "e");
            var body = Expression.Not(Expression.Property(parameter, property));

            return Expression.Lambda(body, parameter);
        });

        return (Expression<Func<TEntity, bool>>?)cached;
    }

    // ------------------------------------------------------------------
    // 单个查询
    // ------------------------------------------------------------------

    public virtual async Task<TEntity> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await Queryable.InSingleAsync(id)
               ?? throw new EntityNotFoundException(typeof(TEntity), id);
    }

    public virtual async Task<TEntity?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // 【为什么要 await 而不是直接 return】
        //   SqlSugar 的 `InSingleAsync` 声明返回 `Task<TEntity>`（非空标注），
        //   而它的真实语义是"查不到返回 null"。直接 `return Queryable.InSingleAsync(id);`
        //   是拿 `Task<TEntity>` 去填 `Task<TEntity?>` —— 泛型不变，编译器给 CS8619。
        //   await 之后拿到的是 `TEntity`，可以隐式转成 `TEntity?`，
        //   既消掉了警告，也让"这里可能为 null"这件事在代码上显式可见。
        return await Queryable.InSingleAsync(id);
    }

    public virtual async Task<TEntity> GetAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await Queryable.Where(predicate).FirstAsync()
               ?? throw new EntityNotFoundException(typeof(TEntity), "查询条件未命中任何记录");
    }

    public virtual async Task<TEntity?> FindAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        // 同上面的按 Id 重载：await 一次，让 `TEntity` → `TEntity?` 的转换发生
        return await Queryable.Where(predicate).FirstAsync();
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
        Expression<Func<TEntity, bool>>? predicate,
        CancellationToken cancellationToken = default)
    {
        // 谓词为 null 时必须**整个跳过 Where**：SqlSugar 收到 null 谓词会拼出
        // 「... WHERE」这样的残缺 SQL，数据库报 syntax error at end of input。
        return predicate == null
            ? await Queryable.CountAsync()
            : await Queryable.Where(predicate).CountAsync();
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

        // 事务改由 ISqlSugarTransactionScope 显式管理，仓储不再隐式开事务
        // （原因见 ISqlSugarTransactionScope 的类注释：ABP 的 UoW 公开接口无法挂接第三方事务，
        //   且 OnCompleted 的触发时机晚于 DI 子作用域释放，会造成事务被静默回滚）
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

        // 事务改由 ISqlSugarTransactionScope 显式管理，仓储不再隐式开事务
        // （原因见 ISqlSugarTransactionScope 的类注释：ABP 的 UoW 公开接口无法挂接第三方事务，
        //   且 OnCompleted 的触发时机晚于 DI 子作用域释放，会造成事务被静默回滚）
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

        // 事务改由 ISqlSugarTransactionScope 显式管理，仓储不再隐式开事务
        // （原因见 ISqlSugarTransactionScope 的类注释：ABP 的 UoW 公开接口无法挂接第三方事务，
        //   且 OnCompleted 的触发时机晚于 DI 子作用域释放，会造成事务被静默回滚）
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

        // 事务改由 ISqlSugarTransactionScope 显式管理，仓储不再隐式开事务
        // （原因见 ISqlSugarTransactionScope 的类注释：ABP 的 UoW 公开接口无法挂接第三方事务，
        //   且 OnCompleted 的触发时机晚于 DI 子作用域释放，会造成事务被静默回滚）
        await Client.Updateable(list).ExecuteCommandAsync();

        if (autoSave)
        {
            await SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 只更新指定列。
    ///
    /// 【为什么是原生参数化 UPDATE，而不是 SqlSugar 的链式局部更新】
    ///   SqlSugar 的 `SetColumns` 有两个重载：一个接**编译期常量**列名列表，
    ///   另一个接 `(实体, 新值) => new 实体 { ... }` 的表达式 —— 后者要求"值和列名"
    ///   一起写在表达式里，而这个方法的列名是**运行时**才知道的（调用方给的是字典）。
    ///   硬凑的话得在运行时反射构造表达式树，比直接拼一句参数化 SQL 更难读、更容易错。
    ///   列名与表名仍走本仓储自己的约定（<see cref="ToSnakeCase"/> + 实体上的
    ///   `[SugarTable]`/类型名），值一律走参数 —— 没有任何字符串拼接进 SQL 的值。
    ///
    /// 两个必须拒绝的入参，都是为了把"静默不生效"变成"当场失败"：
    ///   1. 属性名不在实体上 → 抛异常（否则会拼出一列不存在的 SQL，报的是数据库语法错，
    ///      看起来像基础设施故障，其实是调用方拼错了列名）；
    ///   2. <c>Id</c> 与创建侧审计列不允许由调用方改 → 抛异常（改 Id 等于把这一行搬到
    ///      另一个身份上，而"创建时间"被后写等于审计失真）。
    /// </summary>
    public virtual async Task UpdateColumnsAsync(
        Guid id,
        IReadOnlyDictionary<string, object?> columns,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
    {
        // 用全限定的 Volo.Abp.Check：SqlSugar 里也有一个同名的 Check 类，
        // 直接写 `Check` 会是 CS0104 二义性（这个坑在引入 SqlSugar 命名空间时很容易踩）。
        Volo.Abp.Check.NotNull(columns, nameof(columns));

        if (columns.Count == 0)
        {
            return;
        }

        var assignments = new List<string>(columns.Count);
        var parameters = new List<SugarParameter>(columns.Count + 1);
        var index = 0;

        SugarParameter NewParameter(string name, object? value)
        {
            // 用 `SugarParameter` 而不是 ADO 的 `SqlParameter`：SqlSugar 5.1 的 Ado
            // 只认它自己的参数类型（`SqlSugar.SqlParameter` 这个类型根本不存在，
            // 编译期就会红），名字里带 Sql 的那个是 System.Data 家族，不是这里要的。
            return new SugarParameter(name, value ?? DBNull.Value);
        }

        void AddColumn(string propertyName, object? value)
        {
            // 列名用**双引号**包起来：PostgreSQL 会把未加引号的标识符折叠成小写，
            // 而本项目的列名由属性名转蛇形（`LastModificationTime` → `last_modification_time`），
            // 不加引号时大小写对不上就会报"列不存在"。
            var columnName = ToSnakeCase(propertyName);

            // 每次出现都用**独立的**参数名。
            // 看起来可以把同一个列名复用成一个参数（SqlSugar 的 `SqlParameter` 是
            // 按属性名索引的），但那要求我们自己猜它的键名规则 ——
            // 猜错的表现是"参数没绑上 → 写进去的是 NULL"，极难发现。
            // 一个变量一个名字，绑定规则就完全交给框架。
            var parameterName = $"p{index++}";

            assignments.Add($"\"{columnName}\" = @{parameterName}");
            parameters.Add(NewParameter(parameterName, value));
        }

        foreach (var (propertyName, value) in columns)
        {
            var property = typeof(TEntity).GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (property == null)
            {
                throw new BusinessException(XjglDomainErrorCodes.DataNotFound)
                    .WithData("reason", $"要更新的列「{propertyName}」不是 {typeof(TEntity).Name} 的属性");
            }

            // "Id" 用字面量而不是 nameof(IEntity.Id)：ABP 10 的 IEntity 是个**标记接口**
            // （仅 `IEntity<TKey>` 上才有 Id），`nameof(IEntity.Id)` 根本编译不过。
            if (property.Name is "Id" or "CreationTime" or "CreatorId" or "IsDeleted")
            {
                throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                    .WithData("reason", $"列「{property.Name}」不允许用局部更新改写");
            }

            AddColumn(property.Name, value);
        }

        // 修改审计由仓储统一补：局部更新绕过了 SetModificationAuditProperties
        //（那里需要一个实体对象），若不显式带上，这些行的"最后修改时间"会一直停在创建时刻。
        var modificationTimeProperty = typeof(TEntity).GetProperty(
            nameof(IHasModificationTime.LastModificationTime),
            BindingFlags.Public | BindingFlags.Instance);

        if (modificationTimeProperty is { CanWrite: true }
            && !columns.ContainsKey(modificationTimeProperty.Name))
        {
            // 借一个空实体让 ABP 的审计设置器算时间：它要读 IClock / ICurrentUser，
            // 自己 new 一个 DateTime.Now 会与全系统的时间口径不一致。
            var probe = new TEntity();

            TrySetAudit(() => AuditPropertySetter.SetModificationProperties(probe), "修改");

            if (modificationTimeProperty.GetValue(probe) is { } modificationTime)
            {
                AddColumn(modificationTimeProperty.Name, modificationTime);
            }
        }

        var tableName = GetTableName();
        var idParameterName = $"p{index}";

        parameters.Add(NewParameter(idParameterName, id));

        await Client.Ado.ExecuteCommandAsync(
            $"UPDATE {tableName} SET {string.Join(", ", assignments)} WHERE \"id\" = @{idParameterName}",
            parameters.ToArray());

        if (autoSave)
        {
            await SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 表名。**必须问 SqlSugar**，不能自己用类型名或 <c>[SugarTable]</c> 拼。
    ///
    /// 这里踩过一次：`SysExportTask` 上没有 `[SugarTable]` 特性，表名是靠
    /// `Xjgl:SqlSugar:UseSnakeCaseTableName=true` 这个**全局配置**把
    /// `SysExportTask` 转成 `sys_export_task` 的。自己回落到类型名会拼出
    /// `sysexporttask`，数据库回的是 `42P01: relation "sysexporttask" does not exist` ——
    /// 而进度上报把异常吞掉只记警告，于是表现成"进度永远不动"，
    /// 排查时完全看不出是表名的问题。
    /// </summary>
    private string GetTableName()
    {
        var entityInfo = Client.EntityMaintenance.GetEntityInfo<TEntity>();

        if (!string.IsNullOrWhiteSpace(entityInfo.DbTableName))
        {
            return entityInfo.DbTableName;
        }

        // 退路极少走到（SqlSugar 一定会算出表名），但真走到时也要是**能用的**名字，
        // 而不是一个必然 42P01 的类名 —— 至少让它与仓储的列名约定保持一致。
        return ToSnakeCase(typeof(TEntity).Name);
    }

    public virtual async Task DeleteAsync(
        TEntity entity,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
    {
        // 事务改由 ISqlSugarTransactionScope 显式管理，仓储不再隐式开事务
        // （原因见 ISqlSugarTransactionScope 的类注释：ABP 的 UoW 公开接口无法挂接第三方事务，
        //   且 OnCompleted 的触发时机晚于 DI 子作用域释放，会造成事务被静默回滚）

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

        // 事务改由 ISqlSugarTransactionScope 显式管理，仓储不再隐式开事务
        // （原因见 ISqlSugarTransactionScope 的类注释：ABP 的 UoW 公开接口无法挂接第三方事务，
        //   且 OnCompleted 的触发时机晚于 DI 子作用域释放，会造成事务被静默回滚）

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
        // 事务改由 ISqlSugarTransactionScope 显式管理，仓储不再隐式开事务
        // （原因见 ISqlSugarTransactionScope 的类注释：ABP 的 UoW 公开接口无法挂接第三方事务，
        //   且 OnCompleted 的触发时机晚于 DI 子作用域释放，会造成事务被静默回滚）
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

    // 历史说明（不要再把事务挂回 UoW 的 OnCompleted 上）：
    //
    // 曾经的做法是：当前 ABP 工作单元要求事务时，在仓储里开启 SqlSugar 事务，
    // 并把提交/回滚挂到 uow.OnCompleted / uow.Failed 上。
    //
    // 为什么废弃：ABP 的 DataSeeder 会在 UoW 内部再创建一个 DI 子作用域，
    // SqlSugar 客户端（Scoped）随子作用域先被释放，而 OnCompleted 要等 UoW 完成才触发
    // —— 事务在提交前就随连接释放被静默回滚
    //（日志显示插入 153 条，独立连接查询却是 0 条）。
    //
    // 现在改用 ISqlSugarTransactionScope 显式开启事务范围。

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
