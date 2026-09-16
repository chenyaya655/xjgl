using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Xjgl.Data;
using Xjgl.Permissions;

namespace Xjgl.Metadata;

/// <summary>
/// 子系统 / 模块 / 操作的管理实现。
///
/// 权限名由代码按 <c>{子系统}.{模块}.{操作}</c> 自动生成，因此业务人员
/// 在界面上加一个模块和它的操作，就自动多出可授权的权限项，**不需要改代码、不需要重启**。
/// </summary>
[Authorize]
public class ModuleAppService : XjglAppService, IModuleAppService
{
    private readonly IXjglRepository<SysSubsystem> _subsystems;
    private readonly IXjglRepository<SysModule> _modules;
    private readonly IXjglRepository<SysModuleAction> _actions;
    private readonly IModuleMetadataCache _metadataCache;

    public ModuleAppService(
        IXjglRepository<SysSubsystem> subsystems,
        IXjglRepository<SysModule> modules,
        IXjglRepository<SysModuleAction> actions,
        IModuleMetadataCache metadataCache)
    {
        _subsystems = subsystems;
        _modules = modules;
        _actions = actions;
        _metadataCache = metadataCache;
    }

    // ------------------------------------------------------------------
    // 查询
    // ------------------------------------------------------------------

    public async Task<List<SubsystemDto>> GetTreeAsync(bool onlyEnabled = false)
    {
        var subsystems = await _subsystems.GetSortedListAsync(
            onlyEnabled ? s => s.IsEnabled : null, "Sort ASC, Code ASC");

        var modules = await _modules.GetSortedListAsync(
            onlyEnabled ? m => m.IsEnabled : null, "Sort ASC, Code ASC");

        var actions = await _actions.GetSortedListAsync(
            onlyEnabled ? a => a.IsEnabled : null, "Sort ASC, Code ASC");

        var actionsByModule = actions.GroupBy(a => a.ModuleId).ToDictionary(g => g.Key, g => g.ToList());
        var modulesBySubsystem = modules.GroupBy(m => m.SubsystemId).ToDictionary(g => g.Key, g => g.ToList());

        return subsystems.Select(subsystem =>
        {
            var dto = new SubsystemDto
            {
                Id = subsystem.Id,
                Code = subsystem.Code,
                Name = subsystem.Name,
                Icon = subsystem.Icon,
                Sort = subsystem.Sort,
                IsEnabled = subsystem.IsEnabled,
                Remark = subsystem.Remark
            };

            if (!modulesBySubsystem.TryGetValue(subsystem.Id, out var subsystemModules))
            {
                return dto;
            }

            dto.Modules = subsystemModules.Select(module =>
            {
                var moduleDto = ToModuleDto(module);

                if (actionsByModule.TryGetValue(module.Id, out var moduleActions))
                {
                    moduleDto.Actions = moduleActions.Select(ToActionDto).ToList();
                }

                return moduleDto;
            }).ToList();

            return dto;
        }).ToList();
    }

    // ------------------------------------------------------------------
    // 子系统
    // ------------------------------------------------------------------

    [Authorize(MetadataPermissions.Sys.Module.Create)]
    public async Task<SubsystemDto> CreateSubsystemAsync(CreateUpdateSubsystemDto input)
    {
        var code = input.Code.Trim();

        if (await _subsystems.AnyAsync(s => s.Code == code))
        {
            throw new BusinessException(XjglDomainErrorCodes.DuplicateData).WithData("code", code);
        }

        var entity = new SysSubsystem(GuidGenerator.Create(), code, input.Name.Trim(), input.Icon, input.Sort)
        {
            IsEnabled = input.IsEnabled,
            Remark = input.Remark
        };

        await _subsystems.InsertAsync(entity, autoSave: true);
        _metadataCache.Invalidate();

        return new SubsystemDto
        {
            Id = entity.Id,
            Code = entity.Code,
            Name = entity.Name,
            Icon = entity.Icon,
            Sort = entity.Sort,
            IsEnabled = entity.IsEnabled,
            Remark = entity.Remark
        };
    }

    [Authorize(MetadataPermissions.Sys.Module.Update)]
    public async Task<SubsystemDto> UpdateSubsystemAsync(Guid id, CreateUpdateSubsystemDto input)
    {
        var entity = await _subsystems.GetAsync(id);

        entity.Name = input.Name.Trim();
        entity.Icon = input.Icon;
        entity.Sort = input.Sort;
        entity.IsEnabled = input.IsEnabled;
        entity.Remark = input.Remark;
        // 注意：子系统编码参与权限名生成，一旦有模块就不允许改（改权限名会让已有授权全部失效）
        if (!string.Equals(entity.Code, input.Code.Trim(), StringComparison.Ordinal))
        {
            if (await _modules.AnyAsync(m => m.SubsystemId == id))
            {
                throw new BusinessException(XjglDomainErrorCodes.SystemCodeNotAllowedToModify)
                    .WithData("reason", "该子系统下已有模块，修改编码会导致既有权限名失效");
            }

            entity.Code = input.Code.Trim();
        }

        await _subsystems.UpdateAsync(entity, autoSave: true);
        _metadataCache.Invalidate();

        return new SubsystemDto
        {
            Id = entity.Id,
            Code = entity.Code,
            Name = entity.Name,
            Icon = entity.Icon,
            Sort = entity.Sort,
            IsEnabled = entity.IsEnabled,
            Remark = entity.Remark
        };
    }

    [Authorize(MetadataPermissions.Sys.Module.Delete)]
    public async Task DeleteSubsystemAsync(Guid id)
    {
        var entity = await _subsystems.GetAsync(id);

        if (await _modules.AnyAsync(m => m.SubsystemId == id))
        {
            throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                .WithData("reason", "该子系统下还有模块，请先删除模块");
        }

        await _subsystems.DeleteAsync(entity, autoSave: true);
        _metadataCache.Invalidate();
    }

    // ------------------------------------------------------------------
    // 模块
    // ------------------------------------------------------------------

    [Authorize(MetadataPermissions.Sys.Module.Create)]
    public async Task<ModuleDto> CreateModuleAsync(CreateUpdateModuleDto input)
    {
        var subsystem = await _subsystems.GetAsync(input.SubsystemId);
        var code = input.Code.Trim();

        if (await _modules.AnyAsync(m => m.SubsystemId == input.SubsystemId && m.Code == code))
        {
            throw new BusinessException(XjglDomainErrorCodes.DuplicateData).WithData("code", code);
        }

        var entity = new SysModule(
            GuidGenerator.Create(),
            subsystem.Id,
            code,
            input.Name.Trim(),
            $"{subsystem.Code}.{code}",
            input.Path,
            input.Component,
            input.Sort)
        {
            Icon = input.Icon,
            IsEnabled = input.IsEnabled,
            IsHidden = input.IsHidden,
            IsExternal = input.IsExternal,
            OrgTypes = input.OrgTypes,
            Remark = input.Remark
        };

        await _modules.InsertAsync(entity, autoSave: true);

        // ★ 这一步让新模块的权限立刻可用
        _metadataCache.Invalidate();

        return ToModuleDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.Module.Update)]
    public async Task<ModuleDto> UpdateModuleAsync(Guid id, CreateUpdateModuleDto input)
    {
        var entity = await _modules.GetAsync(id);

        entity.Name = input.Name.Trim();
        entity.Path = input.Path;
        entity.Component = input.Component;
        entity.Icon = input.Icon;
        entity.Sort = input.Sort;
        entity.IsEnabled = input.IsEnabled;
        entity.IsHidden = input.IsHidden;
        entity.IsExternal = input.IsExternal;
        entity.OrgTypes = input.OrgTypes;
        entity.Remark = input.Remark;

        await _modules.UpdateAsync(entity, autoSave: true);
        _metadataCache.Invalidate();

        return ToModuleDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.Module.Delete)]
    public async Task DeleteModuleAsync(Guid id)
    {
        var entity = await _modules.GetAsync(id);

        if (await _actions.AnyAsync(a => a.ModuleId == id))
        {
            throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                .WithData("reason", "该模块下还有操作，请先删除操作");
        }

        await _modules.DeleteAsync(entity, autoSave: true);
        _metadataCache.Invalidate();
    }

    // ------------------------------------------------------------------
    // 操作
    // ------------------------------------------------------------------

    [Authorize(MetadataPermissions.Sys.Module.Create)]
    public async Task<ModuleActionDto> CreateActionAsync(CreateUpdateModuleActionDto input)
    {
        var module = await _modules.GetAsync(input.ModuleId);
        var subsystem = await _subsystems.GetAsync(module.SubsystemId);
        var code = input.Code.Trim();

        if (await _actions.AnyAsync(a => a.ModuleId == input.ModuleId && a.Code == code))
        {
            throw new BusinessException(XjglDomainErrorCodes.DuplicateData).WithData("code", code);
        }

        var entity = new SysModuleAction(
            GuidGenerator.Create(),
            module.Id,
            code,
            input.Name.Trim(),
            input.ActionType,
            $"{subsystem.Code}.{module.Code}.{code}",
            input.Sort)
        {
            IsEnabled = input.IsEnabled,
            IsDefaultGranted = input.IsDefaultGranted
        };

        await _actions.InsertAsync(entity, autoSave: true);
        _metadataCache.Invalidate();

        return ToActionDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.Module.Update)]
    public async Task<ModuleActionDto> UpdateActionAsync(Guid id, CreateUpdateModuleActionDto input)
    {
        var entity = await _actions.GetAsync(id);

        entity.Name = input.Name.Trim();
        entity.ActionType = input.ActionType;
        entity.Sort = input.Sort;
        entity.IsEnabled = input.IsEnabled;
        entity.IsDefaultGranted = input.IsDefaultGranted;

        await _actions.UpdateAsync(entity, autoSave: true);
        _metadataCache.Invalidate();

        return ToActionDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.Module.Delete)]
    public async Task DeleteActionAsync(Guid id)
    {
        var entity = await _actions.GetAsync(id);

        await _actions.DeleteAsync(entity, autoSave: true);
        _metadataCache.Invalidate();
    }

    public Task RefreshCacheAsync()
    {
        _metadataCache.Invalidate();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------

    private static ModuleDto ToModuleDto(SysModule m) => new()
    {
        Id = m.Id,
        SubsystemId = m.SubsystemId,
        Code = m.Code,
        Name = m.Name,
        Path = m.Path,
        Component = m.Component,
        Icon = m.Icon,
        Sort = m.Sort,
        IsEnabled = m.IsEnabled,
        IsHidden = m.IsHidden,
        IsExternal = m.IsExternal,
        PermissionName = m.PermissionName,
        OrgTypes = m.OrgTypes,
        Remark = m.Remark
    };

    private static ModuleActionDto ToActionDto(SysModuleAction a) => new()
    {
        Id = a.Id,
        ModuleId = a.ModuleId,
        Code = a.Code,
        Name = a.Name,
        ActionType = a.ActionType,
        PermissionName = a.PermissionName,
        Sort = a.Sort,
        IsEnabled = a.IsEnabled,
        IsDefaultGranted = a.IsDefaultGranted
    };
}
