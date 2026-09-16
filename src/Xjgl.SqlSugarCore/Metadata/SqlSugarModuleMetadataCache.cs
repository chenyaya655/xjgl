using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Xjgl.Metadata;

namespace Xjgl.SqlSugarCore.Metadata;

/// <summary>
/// 基于 SqlSugar 的模块元数据缓存实现。
///
/// 关键点：
///  1) 用 <see cref="ISqlSugarClientFactory"/> 创建**独立客户端**，
///     而不是走 Scoped 的仓储 —— 因为它会在 ABP 权限定义提供器里被同步调用，
///     那时可能没有活跃的工作单元/作用域；
///  2) 用 SqlSugar 的同步 API 查询，满足权限定义提供器的同步签名；
///  3) 读库失败（例如全新数据库尚未建表）时返回空快照并记录日志，
///     绝不让启动过程崩溃。
/// </summary>
public class SqlSugarModuleMetadataCache : IModuleMetadataCache, ISingletonDependency
{
    private readonly ISqlSugarClientFactory _clientFactory;
    private readonly ILogger<SqlSugarModuleMetadataCache> _logger;
    private readonly object _sync = new();

    private ModuleMetadataSnapshot? _snapshot;

    public SqlSugarModuleMetadataCache(
        ISqlSugarClientFactory clientFactory,
        ILogger<SqlSugarModuleMetadataCache> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public ModuleMetadataSnapshot GetSnapshot()
    {
        var cached = _snapshot;
        if (cached != null)
        {
            return cached;
        }

        lock (_sync)
        {
            _snapshot ??= Load();
            return _snapshot;
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _snapshot = null;
        }

        _logger.LogInformation("模块元数据缓存已失效，下次访问将重新加载。");
    }

    private ModuleMetadataSnapshot Load()
    {
        try
        {
            using var client = _clientFactory.Create();

            var subsystems = client.Queryable<SysSubsystem>()
                .Where(s => s.IsEnabled && s.IsDeleted == false)
                .OrderBy("sort ASC, code ASC")
                .ToList();

            if (subsystems.Count == 0)
            {
                return ModuleMetadataSnapshot.Empty;
            }

            var modules = client.Queryable<SysModule>()
                .Where(m => m.IsEnabled && m.IsDeleted == false)
                .OrderBy("sort ASC, code ASC")
                .ToList();

            var actions = client.Queryable<SysModuleAction>()
                .Where(a => a.IsEnabled && a.IsDeleted == false)
                .OrderBy("sort ASC, code ASC")
                .ToList();

            var actionsByModule = actions
                .GroupBy(a => a.ModuleId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var modulesBySubsystem = modules
                .GroupBy(m => m.SubsystemId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var allPermissionNames = new HashSet<string>(StringComparer.Ordinal);

            var subsystemMetadata = subsystems
                .Select(subsystem =>
                {
                    var moduleList = modulesBySubsystem.TryGetValue(subsystem.Id, out var ms)
                        ? ms
                        : new List<SysModule>();

                    var moduleMetadata = moduleList.Select(module =>
                    {
                        allPermissionNames.Add(module.PermissionName);

                        var actionList = actionsByModule.TryGetValue(module.Id, out var acts)
                            ? acts
                            : new List<SysModuleAction>();

                        var actionMetadata = actionList.Select(action =>
                        {
                            allPermissionNames.Add(action.PermissionName);

                            return new ActionMetadata
                            {
                                Code = action.Code,
                                Name = action.Name,
                                ActionType = action.ActionType,
                                PermissionName = action.PermissionName,
                                Sort = action.Sort,
                                IsDefaultGranted = action.IsDefaultGranted
                            };
                        }).ToList();

                        return new ModuleMetadata
                        {
                            Id = module.Id,
                            Code = module.Code,
                            Name = module.Name,
                            Path = module.Path,
                            Component = module.Component,
                            Icon = module.Icon,
                            Sort = module.Sort,
                            IsHidden = module.IsHidden,
                            IsExternal = module.IsExternal,
                            PermissionName = module.PermissionName,
                            AllowedOrgTypes = module.GetAllowedOrgTypes(),
                            Actions = actionMetadata
                        };
                    }).ToList();

                    return new SubsystemMetadata
                    {
                        Code = subsystem.Code,
                        Name = subsystem.Name,
                        Icon = subsystem.Icon,
                        Sort = subsystem.Sort,
                        Modules = moduleMetadata
                    };
                })
                .ToList();

            var snapshot = new ModuleMetadataSnapshot
            {
                Subsystems = subsystemMetadata,
                AllPermissionNames = allPermissionNames
            };

            _logger.LogInformation(
                "已加载模块元数据：{Subsystems} 个子系统、{Modules} 个模块、{Permissions} 个权限名。",
                subsystemMetadata.Count,
                modules.Count,
                allPermissionNames.Count);

            return snapshot;
        }
        catch (Exception ex)
        {
            // 全新数据库、数据库暂不可用等场景：返回空快照，不影响启动
            _logger.LogWarning(ex,
                "加载模块元数据失败（若数据库尚未初始化，这是预期行为）。本次将使用空元数据。");

            return ModuleMetadataSnapshot.Empty;
        }
    }
}
