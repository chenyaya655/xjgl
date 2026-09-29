using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Organizations;

/// <summary>
/// 角色级数据范围配置（ROADMAP 第 9 条）。
///
/// 【为什么需要它 —— 层级推导覆盖不了的那一小撮例外】
/// 在此之前，数据范围完全由"用户所属组织的层级"推导：
/// 省厅=全省、市/县/校=本级及下级。四级行政体系里这才是主流，
/// 所以当初没做配置表是对的（先把"默认安全"落地更急）。
///
/// 但真实业务里存在**例外**，而且都是很常见的例外：
///   - 某市局只看本市、**不看他下属的县**（汇总口径与管辖口径不一致）；
///   - 某县局只让某个人维护**自己录入**的数据（新入职、实习、代管）。
/// 这些用层级推导表达不出来，只能靠配置。
///
/// 【一个角色一行】
/// `RoleId` 唯一。没有配置行的角色 = 沿用按层级推导的默认值 ——
/// 这是刻意的：全新安装不需要为现有角色补任何数据，行为与加这个功能之前**完全一致**。
///
/// 表名：sys_role_data_scope
/// </summary>
public class SysRoleDataScope : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>角色 Id（对应 ABP 的 <c>AbpRoles.Id</c>，框架表，因此不加外键）。</summary>
    public Guid RoleId { get; set; }

    /// <summary>范围码值，取值见 <see cref="DataScopes.Configurable"/>。</summary>
    public string Scope { get; set; } = DataScopes.OrgAndChildren;

    /// <summary>备注（为什么给这个角色配这个范围 —— 半年后没人记得）。</summary>
    public string? Remark { get; set; }

    public SysRoleDataScope()
    {
    }

    public SysRoleDataScope(Guid id, Guid roleId, string scope) : base(id)
    {
        RoleId = roleId;
        Scope = scope;
    }
}
