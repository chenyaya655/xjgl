namespace Xjgl;

/// <summary>
/// 领域错误码。
/// 约定：BusinessException 的 Code 使用 "Xjgl:NNNNN"，前端据此做国际化与提示。
/// 本文件按业务域分段编号，新增错误码请续号，不要复用已废弃的号。
/// </summary>
public static class XjglDomainErrorCodes
{
    // ---- 通用 00001 - 00099 ----
    public const string DataNotFound = "Xjgl:00001";
    public const string DuplicateData = "Xjgl:00002";
    public const string DataInUse = "Xjgl:00003";
    public const string ValidationFailed = "Xjgl:00004";

    // ---- 组织与学校 00100 - 00199 ----
    public const string OrgNotFound = "Xjgl:00100";
    public const string OrgHasChildren = "Xjgl:00101";
    public const string SchoolCodeDuplicated = "Xjgl:00102";
    public const string OrgCodeDuplicated = "Xjgl:00103";

    // ---- 用户与注册激活 00200 - 00299 ----
    public const string PredefinedUserNotFound = "Xjgl:00200";
    public const string UserAlreadyActivated = "Xjgl:00201";
    public const string ActivationCodeExpired = "Xjgl:00202";
    public const string IdentityMismatch = "Xjgl:00203";
    public const string UserNotInDataScope = "Xjgl:00204";

    // ---- 权限与授权 00300 - 00399 ----
    public const string PermissionNotOwned = "Xjgl:00300";
    public const string OutOfGrantScope = "Xjgl:00301";
    public const string CannotGrantToSelf = "Xjgl:00302";

    // ---- 微代码 00400 - 00499 ----
    public const string CodeCategoryNotFound = "Xjgl:00400";
    public const string CodeItemNotFound = "Xjgl:00401";
    public const string SystemCodeNotAllowedToModify = "Xjgl:00402";

    // ---- 学生与学籍号 00500 - 00599 ----
    public const string StudentNotFound = "Xjgl:00500";
    public const string StudentNumberGenerateFailed = "Xjgl:00501";
    public const string DuplicateIdCard = "Xjgl:00502";
    public const string StudentInChangeFlow = "Xjgl:00503";

    // ---- 异动与工作流 00600 - 00699 ----
    public const string ChangeTypeNotFound = "Xjgl:00600";
    public const string ChangeApplyNotFound = "Xjgl:00601";
    public const string ChangeFlowNotConfigured = "Xjgl:00602";
    public const string OutOfApplyTimeWindow = "Xjgl:00603";
    public const string GradeNotAllowedForChange = "Xjgl:00604";
    public const string TaskNotAssignedToYou = "Xjgl:00605";
    public const string RevokeNotAllowed = "Xjgl:00606";
    public const string FlowVersionConflict = "Xjgl:00607";

    // ---- 照片与文件 00700 - 00799 ----
    public const string PhotoNotFound = "Xjgl:00700";
    public const string PhotoFormatNotSupported = "Xjgl:00701";
    public const string PhotoSizeExceeded = "Xjgl:00702";
    public const string PhotoStorageUnavailable = "Xjgl:00703";
    public const string PhotoBatchTooLarge = "Xjgl:00704";

    // ---- 导入导出 00800 - 00899 ----
    public const string ImportFileInvalid = "Xjgl:00800";
    public const string ImportRowError = "Xjgl:00801";
    public const string ExportTaskNotFound = "Xjgl:00802";
}
