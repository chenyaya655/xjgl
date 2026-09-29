using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Exporting;

/// <summary>
/// 导出/打印任务。
///
/// 【为什么这批操作要从"同步返回文件"改成"任务 + 下载链接"】
///   导出上千人的花名册要渲染 PDF、照片打包要读上千个文件并压缩，
///   这些都在请求线程里做。请求一多就占满线程池，而且浏览器先超时、
///   服务端还在傻跑 —— 用户看到"失败"，实际文件已经被生成出来了，
///   他再点一次，于是白干两遍。
///
/// 【为什么要把发起人写进表里】
///   任务是在**没有 HttpContext 的后台线程**里执行的。而数据范围
///   （`UserDataScopeProvider`）是从 `ICurrentUser` 推出来的 ——
///   后台线程里它是空的。所以必须把"以谁的身份跑"持久化下来，
///   执行时再冒充回去（见 ExportTaskWorker）。少了这个，
///   导出会安静地变成空文件，或者更糟：变成不受范围限制的全量导出。
///
/// 表名：sys_export_task
/// </summary>
public class SysExportTask : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>任务种类，取值见 <see cref="ExportTaskKinds"/>。</summary>
    public string Kind { get; set; } = default!;

    /// <summary>状态，取值见 <see cref="ExportTaskStatuses"/>。</summary>
    public string Status { get; set; } = ExportTaskStatuses.Pending;

    /// <summary>
    /// 查询参数的 JSON 快照。
    ///
    /// 刻意存快照而不是"执行时再取一次"：任务从入队到执行之间用户可能在别的页面
    /// 改了筛选条件，若执行时重新取，导出的就不是他点下按钮时看到的那批数据了。
    /// </summary>
    public string? ParametersJson { get; set; }

    /// <summary>发起人。既是数据范围的依据，也是下载授权的唯一凭据。</summary>
    public Guid RequestedByUserId { get; set; }

    public string? RequestedByName { get; set; }

    /// <summary>
    /// 发起人的角色（逗号分隔）。
    ///
    /// **不能省**：`UserDataScopeProvider` 靠 <c>Roles.Contains("admin")</c> 做超管旁路，
    /// 冒充身份时只带 userId 不带角色，admin 发起的导出会反而看不到任何数据。
    /// </summary>
    public string? RequestedByRoles { get; set; }

    /// <summary>下载时呈现给用户的文件名。</summary>
    public string? FileName { get; set; }

    public string? ContentType { get; set; }

    /// <summary>
    /// 产物在存储根目录下的**相对路径**。
    ///
    /// 由服务端生成（基于任务 Id），**绝不接受客户端输入**：
    /// 让客户端决定物理路径等于把任意文件读写交给调用方。
    /// </summary>
    public string? RelativeArtifactPath { get; set; }

    public long FileSize { get; set; }

    /// <summary>符合筛选条件的**总记录数**。</summary>
    public long Total { get; set; }

    /// <summary>实际写进产物的记录数。与 <see cref="Total"/> 不等即表示被上限截断。</summary>
    public long RowCount { get; set; }

    /// <summary>已处理数（进度）。当前实现是一次性生成，因此只有 0 与终值两个状态。</summary>
    public long Processed { get; set; }

    /// <summary>是否因上限被截断。true 时 <see cref="Message"/> 必须写明。</summary>
    public bool IsTruncated { get; set; }

    /// <summary>给人看的说明：截断提示、缺失张数等。</summary>
    public string? Message { get; set; }

    /// <summary>失败原因（成功时为 null）。</summary>
    public string? FailureReason { get; set; }

    public DateTime? StartedTime { get; set; }

    public DateTime? FinishedTime { get; set; }

    /// <summary>产物过期时间。到点由后台任务删文件并置 <c>Expired</c>。</summary>
    public DateTime? ExpiresTime { get; set; }

    public SysExportTask()
    {
    }

    public SysExportTask(
        Guid id,
        string kind,
        Guid requestedByUserId,
        string? requestedByName,
        string? requestedByRoles,
        string? parametersJson) : base(id)
    {
        Kind = kind;
        Status = ExportTaskStatuses.Pending;
        RequestedByUserId = requestedByUserId;
        RequestedByName = requestedByName;
        RequestedByRoles = requestedByRoles;
        ParametersJson = parametersJson;
    }

    /// <summary>发起人角色列表（由逗号分隔的字段还原）。</summary>
    public string[] GetRequestedByRoles()
        => string.IsNullOrWhiteSpace(RequestedByRoles)
            ? Array.Empty<string>()
            : RequestedByRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>是否已处于终态（终态的任务不再被 worker 领取）。</summary>
    public bool IsTerminal() => ExportTaskStatuses.IsTerminal(Status);
}
