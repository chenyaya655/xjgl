using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Users;

/// <summary>预置用户的激活状态。</summary>
public static class UserPredefineStatus
{
    /// <summary>已预置，等待本人激活。</summary>
    public const string Pending = "Pending";

    /// <summary>本人已激活并创建了账号。</summary>
    public const string Activated = "Activated";

    /// <summary>激活码已过期。</summary>
    public const string Expired = "Expired";

    /// <summary>管理员已撤销（离职、调动等）。</summary>
    public const string Revoked = "Revoked";

    public static string GetDisplayName(string status) => status switch
    {
        Pending => "待激活",
        Activated => "已激活",
        Expired => "已过期",
        Revoked => "已撤销",
        _ => status
    };
}

/// <summary>
/// 预置用户（待激活名单）。
///
/// ★ 这是本系统「外人无法注册」的核心机制：
///   管理员按组织批量导入教师/管理人员的预置名单，系统为每条记录生成一次性激活码；
///   本人激活时必须同时满足「手机号 + 姓名 + （可选）身份证号」与预置记录一致，
///   校验通过才创建 ABP 用户。系统**不开放自助注册**。
///
/// 身份证号的处理：
///   - <see cref="IdCardEncrypted"/>：AES 加密后的原文（用于必要时展示/核对，受权限控制）；
///   - <see cref="IdCardHash"/>：HMAC-SHA256 摘要（用于匹配与查重，可建唯一索引）。
///
/// 表名：sys_user_predefine
/// </summary>
public class SysUserPredefine : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>真实姓名（激活时校验）。</summary>
    public string RealName { get; set; } = default!;

    /// <summary>手机号（激活时的凭证之一，激活后作为登录名）。</summary>
    public string PhoneNumber { get; set; } = default!;

    /// <summary>身份证号摘要（HMAC-SHA256），用于匹配与查重。</summary>
    public string? IdCardHash { get; set; }

    /// <summary>身份证号密文（AES）。</summary>
    public string? IdCardEncrypted { get; set; }

    /// <summary>所属组织（学校或教育行政部门）。</summary>
    public Guid OrgId { get; set; }

    /// <summary>所属组织的物化路径（冗余存储，便于按数据范围过滤预置名单）。</summary>
    public string? OrgPathCode { get; set; }

    /// <summary>拟授予的角色名，多个用英文逗号分隔。</summary>
    public string? RoleNames { get; set; }

    /// <summary>职务。</summary>
    public string? Position { get; set; }

    /// <summary>状态，取值见 <see cref="UserPredefineStatus"/>。</summary>
    public string Status { get; set; } = UserPredefineStatus.Pending;

    /// <summary>一次性激活码。</summary>
    public string ActivationCode { get; set; } = default!;

    /// <summary>激活码过期时间。</summary>
    public DateTime ActivationExpiresAt { get; set; }

    /// <summary>激活后创建的 ABP 用户 Id。</summary>
    public Guid? ActivatedUserId { get; set; }

    /// <summary>激活时间。</summary>
    public DateTime? ActivatedAt { get; set; }

    /// <summary>导入批次号，便于按批次撤销。</summary>
    public string? BatchNo { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public SysUserPredefine()
    {
    }

    public SysUserPredefine(
        Guid id,
        string realName,
        string phoneNumber,
        Guid orgId,
        string activationCode,
        DateTime activationExpiresAt) : base(id)
    {
        RealName = realName;
        PhoneNumber = phoneNumber;
        OrgId = orgId;
        ActivationCode = activationCode;
        ActivationExpiresAt = activationExpiresAt;
        Status = UserPredefineStatus.Pending;
    }

    /// <summary>是否还能被激活。</summary>
    public bool CanActivate(DateTime now)
    {
        return Status == UserPredefineStatus.Pending && ActivationExpiresAt > now;
    }

    /// <summary>标记为已激活。</summary>
    public void MarkActivated(Guid userId, DateTime activatedAt)
    {
        Status = UserPredefineStatus.Activated;
        ActivatedUserId = userId;
        ActivatedAt = activatedAt;
    }

    /// <summary>重新生成激活码并延长有效期。</summary>
    public void RenewActivation(string newCode, DateTime newExpiresAt)
    {
        ActivationCode = newCode;
        ActivationExpiresAt = newExpiresAt;
        Status = UserPredefineStatus.Pending;
    }

    /// <summary>解析拟授予的角色名。</summary>
    public string[] GetRoleNames()
    {
        return string.IsNullOrWhiteSpace(RoleNames)
            ? Array.Empty<string>()
            : RoleNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
