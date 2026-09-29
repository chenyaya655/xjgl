namespace Xjgl.Exporting;

/// <summary>导出/打印任务的种类。</summary>
public static class ExportTaskKinds
{
    /// <summary>学生名单 CSV。</summary>
    public const string StudentCsv = "StudentCsv";

    /// <summary>学籍卡 PDF（一人一份）。</summary>
    public const string StudentCardPdf = "StudentCardPdf";

    /// <summary>花名册 PDF。</summary>
    public const string StudentRosterPdf = "StudentRosterPdf";

    /// <summary>学生照片打包 zip。</summary>
    public const string PhotoZip = "PhotoZip";

    /// <summary>组织（省 / 市 / 县 / 校）名单 CSV（ROADMAP 第 17 条）。</summary>
    public const string OrganizationCsv = "OrganizationCsv";

    /// <summary>微代码（码表 / 码项）CSV（ROADMAP 第 17 条）。</summary>
    public const string CodeCsv = "CodeCsv";

    /// <summary>异动申请单 CSV（ROADMAP 第 17 条）。</summary>
    public const string ChangeApplyCsv = "ChangeApplyCsv";

    /// <summary>异动审核流水 CSV（一个审批节点一行）。</summary>
    public const string ChangeAuditCsv = "ChangeAuditCsv";

    /// <summary>账号清单 CSV（ROADMAP 第 17 条）。</summary>
    public const string UserCsv = "UserCsv";

    /// <summary>预置名单 CSV（一个"待激活的人"一行，身份证只出后 4 位）。</summary>
    public const string UserProvisionCsv = "UserProvisionCsv";

    public static readonly string[] All =
    {
        StudentCsv, StudentCardPdf, StudentRosterPdf, PhotoZip,
        OrganizationCsv, CodeCsv, ChangeApplyCsv, ChangeAuditCsv,
        UserCsv, UserProvisionCsv
    };

    public static string GetDisplayName(string kind) => kind switch
    {
        StudentCsv => "学生名单导出",
        StudentCardPdf => "学籍卡打印",
        StudentRosterPdf => "花名册打印",
        PhotoZip => "照片打包导出",
        OrganizationCsv => "组织名单导出",
        CodeCsv => "微代码导出",
        ChangeApplyCsv => "异动申请导出",
        ChangeAuditCsv => "异动审核流水导出",
        UserCsv => "账号清单导出",
        UserProvisionCsv => "预置名单导出",
        _ => kind
    };
}

/// <summary>
/// 任务状态。
///
/// <c>Expired</c> 与 <c>Canceled</c> 刻意分开：前者是"产物被保留期清理掉了"，
/// 后者是"人主动取消"。合并成一个状态的话，界面上就没法区分
/// "我来晚了"和"我取消过"，而这两种情况该给用户的提示完全不同。
/// </summary>
public static class ExportTaskStatuses
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Canceled = "Canceled";
    public const string Expired = "Expired";

    /// <summary>终态：不会再变化，前端可以停止轮询。</summary>
    public static bool IsTerminal(string status)
        => status is Succeeded or Failed or Canceled or Expired;

    public static string GetDisplayName(string status) => status switch
    {
        Pending => "排队中",
        Running => "执行中",
        Succeeded => "已完成",
        Failed => "失败",
        Canceled => "已取消",
        Expired => "已过期",
        _ => status
    };
}

// 【为什么这两个常量类在 Domain.Shared 而不是跟实体一起放 Domain】
//   Xjgl.Application.Contracts **只引用 Domain.Shared**（不引用 Domain），
//   而 DTO 与下载契约都要用这些常量。放 Domain 里的结果是 Contracts 编译不过 ——
//   这正是本次第一次构建报的那 5 个 CS0103。
//   与 SsoPartnerConsts、IdCardValidator 的下沉是同一个理由。
