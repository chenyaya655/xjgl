using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Identity;
using Xjgl.Data;
using Xjgl.Permissions;

namespace Xjgl.Organizations;

/// <summary>
/// 角色级数据范围配置（ROADMAP 第 9 条）。
///
/// 【为什么放在应用层而不是域服务】
/// 它做的是"把配置表里的一个值读出来 / 写回去"，没有业务规则；
/// 真正的判定逻辑在 <see cref="DataScopes.ResolveEffective"/>（纯函数，有单元测试）
/// 与 <see cref="UserDataScope"/> 上。这里保持薄，是为了让"谁生效"这件事只有一处实现。
/// </summary>
[Authorize]
public class RoleDataScopeAppService : XjglAppService, IRoleDataScopeAppService
{
    private readonly IXjglRepository<SysRoleDataScope> _scopes;
    private readonly IIdentityRoleRepository _roles;

    public RoleDataScopeAppService(
        IXjglRepository<SysRoleDataScope> scopes,
        IIdentityRoleRepository roles)
    {
        _scopes = scopes;
        _roles = roles;
    }

    /// <inheritdoc />
    [Authorize(MetadataPermissions.Sys.Role.Query)]
    public async Task<List<RoleDataScopeDto>> GetListAsync()
    {
        // 角色取自 ABP 框架表；数据范围来自业务表。两边都不大（角色通常十几个），
        // 这里直接全量取回再在内存里对齐，比为每个角色查一次数据库简单得多。
        var roles = await _roles.GetListAsync();
        var configured = await _scopes.GetListAsync();

        var byRole = configured
            .GroupBy(s => s.RoleId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.LastModificationTime ?? s.CreationTime).First());

        return roles
            .OrderBy(r => r.Name)
            .Select(role =>
            {
                byRole.TryGetValue(role.Id, out var row);

                return new RoleDataScopeDto
                {
                    RoleId = role.Id,
                    RoleName = role.Name ?? string.Empty,
                    Scope = row?.Scope,
                    ScopeName = row == null ? null : DataScopes.GetDisplayName(row.Scope),
                    Remark = row?.Remark
                };
            })
            .ToList();
    }

    /// <inheritdoc />
    [Authorize(MetadataPermissions.Sys.Role.Query)]
    public Task<List<CodeOption>> GetOptionsAsync()
    {
        return Task.FromResult(RoleDataScopeDto.Options());
    }

    /// <inheritdoc />
    [Authorize(MetadataPermissions.Sys.Role.Update)]
    public async Task<RoleDataScopeDto> SetAsync(SetRoleDataScopeDto input)
    {
        var role = await _roles.FindAsync(input.RoleId)
                   ?? throw new BusinessException(XjglDomainErrorCodes.DataNotFound)
                       .WithData("reason", "角色不存在");

        var scope = input.Scope?.Trim();
        var existing = await _scopes.FindAsync(s => s.RoleId == input.RoleId);

        // 传空 = 清除配置，回到"按组织层级推导"。
        // 这是必要的：否则一旦配过就再也回不到默认值，而默认值才是绝大多数角色该用的。
        if (scope.IsNullOrWhiteSpace())
        {
            if (existing != null)
            {
                await _scopes.DeleteAsync(existing, autoSave: true);
            }

            return new RoleDataScopeDto
            {
                RoleId = role.Id,
                RoleName = role.Name ?? string.Empty,
                Scope = null,
                ScopeName = null,
                Remark = null
            };
        }

        // 只接受可配置的四种。特别是 Custom（自定义组织集合）目前**不支持**：
        // 各查询的 SQL 谓词按"单一根路径"生成，落不了多个根，于是它会悄悄退化成
        // "本级及下级"—— 那比配置更宽，属于"看起来配了、实际放开了"。
        // 宁可在这里明确拒绝，也不要让它静默生效（见 ROADMAP 第 21 条）。
        if (!DataScopes.IsConfigurable(scope))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason",
                    scope == DataScopes.Custom
                        ? "「自定义组织集合」暂不支持配置（多根路径的查询谓词尚未实现），请先用「仅本级」或「本级及下级」。"
                        : $"未知的数据范围「{scope}」");
        }

        if (existing == null)
        {
            existing = new SysRoleDataScope(GuidGenerator.Create(), input.RoleId, scope)
            {
                Remark = input.Remark
            };

            await _scopes.InsertAsync(existing, autoSave: true);
        }
        else
        {
            existing.Scope = scope;
            existing.Remark = input.Remark;

            await _scopes.UpdateAsync(existing, autoSave: true);
        }

        return new RoleDataScopeDto
        {
            RoleId = role.Id,
            RoleName = role.Name ?? string.Empty,
            Scope = existing.Scope,
            ScopeName = DataScopes.GetDisplayName(existing.Scope),
            Remark = existing.Remark
        };
    }
}
