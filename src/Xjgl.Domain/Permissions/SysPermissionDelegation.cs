using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Permissions;

/// <summary>
/// 权限委派（ROADMAP 第 19 条 / 方案文档 §5.2）。
///
/// 【它做什么】把**自己拥有的某条权限**临时交给另一个人，
/// 到期或撤销后自动收回。典型场景：审核人休假，把「异动审核」交给同科室同事；
/// 或者新同事上手期间，由科长把自己的「学生录入」临时借给他。
///
/// 【安全边界（这一条决定了整个设计）】
/// 委派**只授予"能不能调这个接口"，不改动"能看哪些数据"**。
/// 数据范围层（`IUserDataScopeProvider`）是**与权限无关**的：它按被委派人自己的组织算。
/// 因此被委派人拿到的只是能力，**看不到一件他自己本来看不到的数据** —— 不存在提权通道。
///
/// 代价（如实记下）：委托给数据范围更小的人，他仍然只能在自己范围内办事；
/// "代我审我这一片"这种**按权限限定范围**的委派做不到 ——
/// 那需要让数据范围感知"正在行使哪个权限"，是 ROADMAP 第 21 条同类的遗留。
///
/// 【为什么落到 ABP 的用户级授权上】
/// 委派的权限必须让 `[Authorize(PermissionName)]` 立刻生效。要做到这一点，
/// 唯一稳的做法就是走 ABP 自己的授权存储（用户级授权），
/// 而不是另建一套"权限判定旁路" —— 后者意味着每个鉴权点都要改，
/// 且迟早会有一处忘记查委派表（那种"看起来有防护其实没有"的状态最危险）。
/// 本表因此是**台账与期限的依据**，授权存储是**生效的依据**，两者必须同时维护。
///
/// 表名：sys_permission_delegation
/// </summary>
public class SysPermissionDelegation : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>委派人（把自己的权限借出去的人）。</summary>
    public Guid DelegatorUserId { get; set; }

    /// <summary>委派人姓名（冗余，便于列表展示时不必再查一次用户）。</summary>
    public string? DelegatorName { get; set; }

    /// <summary>被委派人（拿到权限的人）。</summary>
    public Guid DelegateeUserId { get; set; }

    /// <summary>被委派人姓名（冗余，同上）。</summary>
    public string? DelegateeName { get; set; }

    /// <summary>被委派的权限码（例如 <c>Chg.Audit.Approve</c>）。</summary>
    public string PermissionName { get; set; } = default!;

    /// <summary>到期时间；为空表示长期有效（需要显式撤销）。</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// 是否已失效（被撤销或已到期）。
    ///
    /// 与"软删除"分开：**撤销/到期是业务事实**（"这条委派曾经存在、何时失效"要留痕），
    /// 而软删除是"这条记录不该再被看到"。合并成一个字段的话，
    /// 台账就查不出"上个月谁把权限借给谁了"。
    /// </summary>
    public bool IsRevoked { get; set; }

    /// <summary>失效时间（撤销或到期回收的时刻）。</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>失效原因（"已撤销" / "已到期自动回收"）。</summary>
    public string? RevokedReason { get; set; }

    /// <summary>备注：为什么借、借多久。半年后没人记得。</summary>
    public string? Remark { get; set; }

    public SysPermissionDelegation()
    {
    }

    public SysPermissionDelegation(
        Guid id,
        Guid delegatorUserId,
        Guid delegateeUserId,
        string permissionName) : base(id)
    {
        DelegatorUserId = delegatorUserId;
        DelegateeUserId = delegateeUserId;
        PermissionName = permissionName;
    }

    /// <summary>是否仍然生效（未撤销且未到期）。</summary>
    public bool IsActive(DateTime now) => !IsRevoked && (ExpiresAt == null || ExpiresAt > now);

    /// <summary>标记失效。重复调用保持首次的时间与原因（台账不该被后来的操作覆盖）。</summary>
    public void MarkRevoked(DateTime now, string reason)
    {
        if (IsRevoked)
        {
            return;
        }

        IsRevoked = true;
        RevokedAt = now;
        RevokedReason = reason;
    }
}
