using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Dtos;
using Xjgl.Organizations;
using Xjgl.Permissions;
using Xjgl.Photos;
using Xjgl.Students;
namespace Xjgl.Exporting;

/// <summary>
/// 任务种类与所需权限的映射。
///
/// 【为什么不新造权限码】
///   本系统的权限码来自元数据表（`sys_module` / `sys_module_action` → 动态权限定义 + 菜单），
///   新增一个码就要动种子、动菜单、动前端按钮级权限，代价远大于收益。
///   而"导出学生名单"与"打印花名册"在业务上本来就是同一批人要干的事，
///   复用既有权限码语义上也说得通：**能看到才能导出**。
///
/// 放在 Contracts 层是因为两处都要用：应用服务（入队时校验）与
/// HttpApi 的下载控制器（防御性再校验一次）。
/// </summary>
public static class ExportTaskPermissions
{
    /// <summary>该种类至少需要其中**任意一个**权限。</summary>
    public static string[] GetRequiredPermissions(string kind) => kind switch
    {
        ExportTaskKinds.StudentCsv => new[] { MetadataPermissions.Stu.Student.Export },

        ExportTaskKinds.StudentCardPdf or ExportTaskKinds.StudentRosterPdf
            => new[] { MetadataPermissions.Stu.Student.Print },

        // 照片导出同时要求 Query：导出内部复用台账查询，
        // 只给 Export 不给 Query 的话会在内部调用处炸出 403 —— 那种报错让人摸不着头脑。
        ExportTaskKinds.PhotoZip
            => new[] { MetadataPermissions.Pho.Photo.Export, MetadataPermissions.Pho.Photo.Query },

        ExportTaskKinds.OrganizationCsv
            => new[] { MetadataPermissions.Sys.Org.Export },

        ExportTaskKinds.CodeCsv
            => new[] { MetadataPermissions.Sys.Code.Export },

        ExportTaskKinds.ChangeApplyCsv
            => new[] { MetadataPermissions.Chg.Apply.Export },

        ExportTaskKinds.ChangeAuditCsv
            => new[] { MetadataPermissions.Chg.Audit.Export },

        _ => Array.Empty<string>()
    };

    /// <summary>是否为已知种类。</summary>
    public static bool IsKnown(string? kind)
        => !string.IsNullOrWhiteSpace(kind) && ExportTaskKinds.All.Contains(kind);
}

/// <summary>
/// 入队请求。
///
/// 每种种类带自己的查询条件，而**不是**让前端塞一段 JSON 进来：
/// 参数由服务端序列化成快照，前端塞 JSON 的话，字段名写错、少传一个
/// 都会变成执行期才暴露的问题，而那时用户早已离开页面。
/// </summary>
public class CreateExportTaskDto
{
    [Required]
    [StringLength(32)]
    public string Kind { get; set; } = default!;

    /// <summary><see cref="ExportTaskKinds.StudentCsv"/> 用。</summary>
    public StudentQueryDto? StudentQuery { get; set; }

    /// <summary><see cref="ExportTaskKinds.StudentRosterPdf"/> 用。</summary>
    public StudentRosterQueryDto? RosterQuery { get; set; }

    /// <summary><see cref="ExportTaskKinds.StudentCardPdf"/> 用。</summary>
    public Guid? StudentId { get; set; }

    /// <summary><see cref="ExportTaskKinds.PhotoZip"/> 用。</summary>
    public PhotoExportQueryDto? PhotoQuery { get; set; }

    /// <summary><see cref="ExportTaskKinds.OrganizationCsv"/> 用；留空表示导出全部组织。</summary>
    public OrgQueryDto? OrgQuery { get; set; }

    /// <summary><see cref="ExportTaskKinds.CodeCsv"/> 用；留空表示导出全部码表。</summary>
    public Codes.CodeExportQueryDto? CodeQuery { get; set; }

    /// <summary>
    /// <see cref="ExportTaskKinds.ChangeApplyCsv"/> 与 <see cref="ExportTaskKinds.ChangeAuditCsv"/> 用；
    /// 留空表示导出全部（受数据范围约束）。
    /// </summary>
    public Changes.ChangeApplyQueryDto? ChangeQuery { get; set; }
}

/// <summary>任务列表的查询条件。</summary>
public class ExportTaskQueryDto : PagedAndSortedResultRequestDto
{
    [StringLength(32)]
    public string? Kind { get; set; }

    [StringLength(32)]
    public string? Status { get; set; }
}

/// <summary>任务的对外视图。</summary>
public class ExportTaskDto : EntityDto<Guid>
{
    public string Kind { get; set; } = default!;

    public string KindName { get; set; } = default!;

    public string Status { get; set; } = default!;

    public string? FileName { get; set; }

    public string? ContentType { get; set; }

    public long FileSize { get; set; }

    /// <summary>符合条件的总记录数。</summary>
    public long Total { get; set; }

    /// <summary>实际写进产物的记录数。<c>Total != RowCount</c> 即表示被截断。</summary>
    public long RowCount { get; set; }

    public long Processed { get; set; }

    public bool IsTruncated { get; set; }

    /// <summary>截断提示、照片缺失张数等。</summary>
    public string? Message { get; set; }

    public string? FailureReason { get; set; }

    public DateTime? StartedTime { get; set; }

    public DateTime? FinishedTime { get; set; }

    public DateTime? ExpiresTime { get; set; }

    public DateTime CreationTime { get; set; }

    /// <summary>
    /// 产物是否可下载。前端据此决定"下载"按钮是否可点 ——
    /// 让它自己按 Status 判断的话，将来加了新状态两边就会不一致。
    /// </summary>
    public bool CanDownload { get; set; }
}

/// <summary>
/// 导出/打印任务的应用服务。
///
/// 入队是**幂等性无关**的：同一个条件点两次会得到两个任务、两份产物。
/// 这是刻意的 —— 合并"相同请求"需要定义一个"相同"的判据，
/// 而用户点了两次通常就是想要两份（或者第一次没等到）。
/// 产物有保留期，重复的会被自动清理。
/// </summary>
public interface IExportTaskAppService : Volo.Abp.Application.Services.IApplicationService
{
    /// <summary>入队一个导出/打印任务。</summary>
    Task<ExportTaskDto> CreateAsync(CreateExportTaskDto input);

    /// <summary>查单条（**只允许查自己发起的**，见实现里的说明）。</summary>
    Task<ExportTaskDto> GetAsync(Guid id);

    /// <summary>查自己发起的任务列表。</summary>
    Task<PagedResultDto<ExportTaskDto>> GetListAsync(ExportTaskQueryDto input);

    /// <summary>取消（仅 Pending 可取消；Running 已经不能中断）。</summary>
    Task<ExportTaskDto> CancelAsync(Guid id);

    /// <summary>删除任务记录（连带删产物）。</summary>
    Task DeleteAsync(Guid id);
}
