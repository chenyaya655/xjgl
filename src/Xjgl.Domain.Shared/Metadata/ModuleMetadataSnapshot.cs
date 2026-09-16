namespace Xjgl.Metadata;

/// <summary>
/// 模块元数据快照。供权限定义提供器（同步）与菜单服务（异步）共用。
/// </summary>
public sealed class ModuleMetadataSnapshot
{
    public static readonly ModuleMetadataSnapshot Empty = new();

    public IReadOnlyList<SubsystemMetadata> Subsystems { get; init; } = Array.Empty<SubsystemMetadata>();

    public bool IsEmpty => Subsystems.Count == 0;

    /// <summary>快照生成时间，便于排查缓存问题。</summary>
    public DateTime LoadedAt { get; init; } = DateTime.Now;

    /// <summary>扁平化后的全部权限名（模块 + 操作），用于快速判断权限是否由元数据定义。</summary>
    public IReadOnlySet<string> AllPermissionNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public sealed class SubsystemMetadata
{
    public string Code { get; init; } = default!;
    public string Name { get; init; } = default!;
    public string? Icon { get; init; }
    public int Sort { get; init; }
    public IReadOnlyList<ModuleMetadata> Modules { get; init; } = Array.Empty<ModuleMetadata>();
}

public sealed class ModuleMetadata
{
    public Guid Id { get; init; }
    public string Code { get; init; } = default!;
    public string Name { get; init; } = default!;
    public string? Path { get; init; }
    public string? Component { get; init; }
    public string? Icon { get; init; }
    public int Sort { get; init; }
    public bool IsHidden { get; init; }
    public bool IsExternal { get; init; }
    public string PermissionName { get; init; } = default!;

    /// <summary>允许访问的组织层级；为空表示不限。</summary>
    public IReadOnlyList<string> AllowedOrgTypes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ActionMetadata> Actions { get; init; } = Array.Empty<ActionMetadata>();
}

public sealed class ActionMetadata
{
    public string Code { get; init; } = default!;
    public string Name { get; init; } = default!;
    public string ActionType { get; init; } = default!;
    public string PermissionName { get; init; } = default!;
    public int Sort { get; init; }
    public bool IsDefaultGranted { get; init; }
}

/// <summary>
/// 模块元数据缓存。
///
/// 为什么要做成「同步 + 缓存」：
///   ABP 的 <c>PermissionDefinitionProvider.Define</c> 是**同步**执行的（启动期一次），
///   而元数据存在数据库里。因此这里必须提供同步读取 + 进程内缓存，
///   并在模块/操作变更时主动失效。
/// </summary>
public interface IModuleMetadataCache
{
    /// <summary>获取快照（首次访问时从数据库加载并缓存）。读取失败时返回空快照而不抛异常。</summary>
    ModuleMetadataSnapshot GetSnapshot();

    /// <summary>失效缓存，下次访问重新加载。</summary>
    void Invalidate();
}
