namespace Xjgl.Organizations;

/// <summary>
/// 当前用户的数据范围 —— 决定「能对谁做」。
///
/// 与权限正交：权限回答"能不能查学生"，数据范围回答"能查哪些学生"。
/// 二者缺一不可 —— 只有权限没有范围，一个县区管理员传个别的县的组织 Id
/// 就能看到全省学生（这正是本项目在实现它之前的真实状态）。
///
/// 这是一个**纯值对象**：所有判断都是字符串前缀比较，不碰数据库，
/// 因此可以用穷尽式的单元测试把边界钉死（见 UserDataScopeTests）。
/// </summary>
public sealed class UserDataScope
{
    private UserDataScope(string scope, Guid? orgId, string? orgPathCode, Guid? userId, bool isSuperAdmin)
    {
        Scope = scope;
        OrgId = orgId;
        OrgPathCode = orgPathCode;
        UserId = userId;
        IsSuperAdmin = isSuperAdmin;
    }

    /// <summary>范围码值，取值见 <see cref="DataScopes"/>。</summary>
    public string Scope { get; }

    /// <summary>所属组织；为空表示未关联组织。</summary>
    public Guid? OrgId { get; }

    /// <summary>所属组织的物化路径。</summary>
    public string? OrgPathCode { get; }

    public Guid? UserId { get; }

    /// <summary>是否超级管理员（内置 admin 角色）：不受数据范围限制。</summary>
    public bool IsSuperAdmin { get; }

    /// <summary>不受限制（全省）。</summary>
    public bool IsUnrestricted => IsSuperAdmin || Scope == DataScopes.All;

    /// <summary>什么都看不到（未关联组织且非超管）。</summary>
    public bool IsEmpty => !IsUnrestricted && string.IsNullOrWhiteSpace(OrgPathCode);

    public static UserDataScope ForSuperAdmin(Guid? userId)
        => new(DataScopes.All, null, null, userId, isSuperAdmin: true);

    /// <summary>全部数据（省厅或显式授予 All 的角色）。</summary>
    public static UserDataScope ForAll(Guid? userId, Guid? orgId = null, string? orgPathCode = null)
        => new(DataScopes.All, orgId, orgPathCode, userId, isSuperAdmin: false);

    /// <summary>本级及下级（默认：市/县/校三级管理员都是这个范围）。</summary>
    public static UserDataScope ForOrgAndChildren(Guid? orgId, string orgPathCode, Guid? userId)
        => new(DataScopes.OrgAndChildren, orgId, orgPathCode, userId, isSuperAdmin: false);

    /// <summary>仅本级。</summary>
    public static UserDataScope ForOrgOnly(Guid? orgId, string orgPathCode, Guid? userId)
        => new(DataScopes.OrgOnly, orgId, orgPathCode, userId, isSuperAdmin: false);

    /// <summary>仅本人录入的数据。</summary>
    public static UserDataScope ForSelfOnly(Guid? orgId, string? orgPathCode, Guid? userId)
        => new(DataScopes.SelfOnly, orgId, orgPathCode, userId, isSuperAdmin: false);

    /// <summary>什么都看不到。</summary>
    public static UserDataScope Empty(Guid? userId)
        => new(DataScopes.OrgOnly, null, null, userId, isSuperAdmin: false);

    /// <summary>
    /// 某个**实体**（学生、照片…）是否落在范围内。
    /// 传入的是实体上冗余的物化路径（如学生的 SchoolPathCode）。
    /// </summary>
    public bool AllowsEntityPath(string? entityPathCode)
    {
        if (IsUnrestricted)
        {
            return true;
        }

        if (IsEmpty || string.IsNullOrWhiteSpace(entityPathCode))
        {
            return false;
        }

        return Scope switch
        {
            // 本级及下级：自己或自己的后代
            DataScopes.OrgAndChildren => IsSelfOrDescendant(entityPathCode),

            // 仅本级：必须是本组织本身（学生挂在"组织"上的情况极少，语义如此）
            DataScopes.OrgOnly => string.Equals(entityPathCode, OrgPathCode, StringComparison.Ordinal),

            // 仅本人：由调用方另外按 CreatorId 判断，这里只放行"组织在范围内"的部分
            DataScopes.SelfOnly => IsSelfOrDescendant(entityPathCode),

            // 自定义组织集合：当前尚未实现配置表，退化为"本级及下级"
            // （宁可收紧也不要放行：配置表落地前不应出现比设计更宽的可见性）
            DataScopes.Custom => IsSelfOrDescendant(entityPathCode),

            _ => false
        };
    }

    /// <summary>
    /// 客户端**请求的组织子树**是否与我的范围有交集。
    ///
    /// 用途：用户在查询里传了 <c>SchoolOrgId=某县</c>，但那不在他的范围内 ——
    /// 此时应当返回空结果，而不是"因为他传了就照查"。
    /// 判断是两个路径的相互前缀包含（任一方是另一方的前缀即有交集）。
    /// </summary>
    public bool AllowsRequestedOrgPath(string? requestedOrgPathCode)
    {
        if (IsUnrestricted)
        {
            return true;
        }

        if (IsEmpty || string.IsNullOrWhiteSpace(requestedOrgPathCode))
        {
            return false;
        }

        if (Scope == DataScopes.OrgOnly)
        {
            return string.Equals(requestedOrgPathCode, OrgPathCode, StringComparison.Ordinal);
        }

        // 两个方向都算"有交集"：
        //   1) 请求的是我或我的后代 → 请求 ⊆ 我；
        //   2) 请求的是我的**上级** → 我 ⊆ 请求。
        //      这一条不能少：县区管理员想看"本市范围"的统计时会传市 Id，
        //      若判为无交集，正常的按上级筛选就直接失效了。
        //      它安全的前提是**实体层仍然叠加范围约束** ——
        //      最终返回的数据依旧被限制在"我的子树"内（见 AllowsEntityPath）。
        //
        // 注意两个方向的写法不同（一个比对 requested，一个比对自身），
        // 写反了就会把"请求上级"误判为越权 —— 单元测试里专门有一条钉住它。
        return IsSelfOrDescendant(requestedOrgPathCode)
               || OrgPathCode!.StartsWith(requestedOrgPathCode + ".", StringComparison.Ordinal);
    }

    /// <summary>实体/组织路径是否等于我的组织或在我的组织之下。</summary>
    private bool IsSelfOrDescendant(string pathCode)
        => string.Equals(pathCode, OrgPathCode, StringComparison.Ordinal)
           || pathCode.StartsWith(OrgPathCode + ".", StringComparison.Ordinal);
}
