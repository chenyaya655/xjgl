using Volo.Abp.Application.Services;

namespace Xjgl.Metadata;

/// <summary>
/// 子系统 / 模块 / 操作的管理（元数据维护）。
///
/// 任何写操作都会失效元数据缓存，使权限定义与菜单在下一次加载时反映最新配置。
/// </summary>
public interface IModuleAppService : IApplicationService
{
    /// <summary>完整的元数据树：子系统 → 模块 → 操作。</summary>
    Task<List<SubsystemDto>> GetTreeAsync(bool onlyEnabled = false);

    Task<SubsystemDto> CreateSubsystemAsync(CreateUpdateSubsystemDto input);

    Task<SubsystemDto> UpdateSubsystemAsync(Guid id, CreateUpdateSubsystemDto input);

    Task DeleteSubsystemAsync(Guid id);

    Task<ModuleDto> CreateModuleAsync(CreateUpdateModuleDto input);

    Task<ModuleDto> UpdateModuleAsync(Guid id, CreateUpdateModuleDto input);

    Task DeleteModuleAsync(Guid id);

    Task<ModuleActionDto> CreateActionAsync(CreateUpdateModuleActionDto input);

    Task<ModuleActionDto> UpdateActionAsync(Guid id, CreateUpdateModuleActionDto input);

    Task DeleteActionAsync(Guid id);

    /// <summary>手工刷新元数据缓存（一般不需要，写操作已自动失效）。</summary>
    Task RefreshCacheAsync();
}

/// <summary>
/// 当前登录用户的上下文：身份、组织、角色、权限码、可见菜单。
/// 前端登录后调用一次即可完成动态路由与按钮权限的初始化。
/// </summary>
public interface IMyProfileAppService : IApplicationService
{
    Task<MyProfileDto> GetAsync();
}
