using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Timing;
using Xjgl.Changes;
using Xjgl.Codes;
using Xjgl.Organizations;
using Xjgl.Photos;
using Xjgl.Students;

namespace Xjgl.Exporting;

/// <summary>一次任务执行的产出（文件已落盘）。</summary>
public sealed record ExportTaskRunResult(
    string FileName,
    string ContentType,
    string RelativeArtifactPath,
    long FileSize,
    long Total,
    long RowCount,
    bool IsTruncated,
    string? Message);

/// <summary>
/// 按任务种类生成产物。
///
/// 放在应用层：它要调用 <see cref="IStudentPrintService"/>、<see cref="IPhotoExportService"/>
/// 与 <see cref="IStudentCsvExportBuilder"/> —— 这三个都在应用层。
/// Host 的 worker 只负责"什么时候跑、以谁的身份跑、跑完写什么状态"，
/// 生成什么由这里决定，两者分开是为了让 worker 不必引用一堆业务服务。
///
/// **本类不写数据库**：产物落盘后就返回，状态流转全部交给 worker 一处完成。
/// 两处都写状态的话，"谁最后写的"取决于代码顺序，排查时极难复原。
/// </summary>
public interface IExportTaskRunner
{
    Task<ExportTaskRunResult> RunAsync(SysExportTask task, CancellationToken cancellationToken = default);
}

public class ExportTaskRunner : IExportTaskRunner, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly IStudentCsvExportBuilder _csvBuilder;
    private readonly IStudentPrintService _printService;
    private readonly IPhotoExportService _photoExport;
    private readonly IOrganizationCsvExportBuilder _orgCsvBuilder;
    private readonly ICodeCsvExportBuilder _codeCsvBuilder;
    private readonly IChangeCsvExportBuilder _changeCsvBuilder;
    private readonly IStudentAppService _students;
    private readonly IExportArtifactStore _artifacts;
    private readonly IClock _clock;
    private readonly ILogger<ExportTaskRunner> _logger;

    public ExportTaskRunner(
        IStudentCsvExportBuilder csvBuilder,
        IStudentPrintService printService,
        IPhotoExportService photoExport,
        IOrganizationCsvExportBuilder orgCsvBuilder,
        ICodeCsvExportBuilder codeCsvBuilder,
        IChangeCsvExportBuilder changeCsvBuilder,
        IStudentAppService students,
        IExportArtifactStore artifacts,
        IClock clock,
        ILogger<ExportTaskRunner> logger)
    {
        _csvBuilder = csvBuilder;
        _printService = printService;
        _photoExport = photoExport;
        _orgCsvBuilder = orgCsvBuilder;
        _codeCsvBuilder = codeCsvBuilder;
        _changeCsvBuilder = changeCsvBuilder;
        _students = students;
        _artifacts = artifacts;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ExportTaskRunResult> RunAsync(
        SysExportTask task,
        CancellationToken cancellationToken = default)
    {
        Check.NotNull(task, nameof(task));

        return task.Kind switch
        {
            ExportTaskKinds.StudentCsv => await RunStudentCsvAsync(task, cancellationToken),
            ExportTaskKinds.StudentCardPdf => await RunStudentCardAsync(task, cancellationToken),
            ExportTaskKinds.StudentRosterPdf => await RunStudentRosterAsync(task, cancellationToken),
            ExportTaskKinds.PhotoZip => await RunPhotoZipAsync(task, cancellationToken),
            ExportTaskKinds.OrganizationCsv => await RunOrganizationCsvAsync(task, cancellationToken),
            ExportTaskKinds.CodeCsv => await RunCodeCsvAsync(task, cancellationToken),
            ExportTaskKinds.ChangeApplyCsv => await RunChangeCsvAsync(task, ShowKind.Applies, cancellationToken),
            ExportTaskKinds.ChangeAuditCsv => await RunChangeCsvAsync(task, ShowKind.Audit, cancellationToken),
            _ => throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", $"未知的任务种类「{task.Kind}」")
        };
    }

    private async Task<ExportTaskRunResult> RunStudentCsvAsync(
        SysExportTask task,
        CancellationToken cancellationToken)
    {
        var query = Deserialize<StudentQueryDto>(task);

        var result = await _csvBuilder.BuildAsync(query, cancellationToken);

        var content = new MemoryStream(Encoding.UTF8.GetBytes(result.Csv));
        var artifact = await _artifacts.WriteAsync(task.Id, "text/csv", content, cancellationToken);

        return new ExportTaskRunResult(
            FileName: $"学生名单_{_clock.Now:yyyyMMddHHmmss}.csv",
            ContentType: "text/csv",
            RelativeArtifactPath: artifact.RelativePath,
            FileSize: artifact.Size,
            Total: result.Total,
            RowCount: result.RowCount,
            IsTruncated: result.IsTruncated,
            Message: result.IsTruncated
                // 截断必须说出来，而且要说清"少了多少"。
                // 只写"已达上限"的话，用户不知道差的是 3 个人还是 8000 个人。
                ? $"实际导出 {result.RowCount} 行 / 共 {result.Total} 行，已达导出上限（{result.Total - result.RowCount} 行未导出）"
                : $"共 {result.RowCount} 行");
    }

    private async Task<ExportTaskRunResult> RunStudentCardAsync(
        SysExportTask task,
        CancellationToken cancellationToken)
    {
        var studentId = Deserialize<Guid>(task);

        var file = await _printService.RenderStudentCardAsync(studentId, cancellationToken);

        var artifact = await _artifacts.WriteAsync(
            task.Id, file.ContentType, new MemoryStream(file.Content), cancellationToken);

        return new ExportTaskRunResult(
            FileName: file.FileName,
            ContentType: file.ContentType,
            RelativeArtifactPath: artifact.RelativePath,
            FileSize: artifact.Size,
            Total: 1,
            RowCount: 1,
            IsTruncated: false,
            Message: null);
    }

    private async Task<ExportTaskRunResult> RunStudentRosterAsync(
        SysExportTask task,
        CancellationToken cancellationToken)
    {
        var query = Deserialize<StudentRosterQueryDto>(task);

        var file = await _printService.RenderRosterAsync(query, cancellationToken);

        // 花名册的打印服务只回"打了几行"，不回"一共有几行"，
        // 而 `MaxRows`（默认 2000）是会截断的。所以这里**另外数一次总数**，
        // 否则"少打了一半人"这件事在产出物里没有任何痕迹 ——
        // 名单类的产出少人是最不能接受的失败方式。
        var total = await CountMatchingStudentsAsync(query, cancellationToken);
        var rowCount = file.RowCount ?? 0;
        var truncated = total > 0 && rowCount < total;

        var artifact = await _artifacts.WriteAsync(
            task.Id, file.ContentType, new MemoryStream(file.Content), cancellationToken);

        return new ExportTaskRunResult(
            FileName: file.FileName,
            ContentType: file.ContentType,
            RelativeArtifactPath: artifact.RelativePath,
            FileSize: artifact.Size,
            Total: total,
            RowCount: rowCount,
            IsTruncated: truncated,
            Message: truncated
                ? $"实际打印 {rowCount} 人 / 共 {total} 人，已达本次打印上限（{total - rowCount} 人未打印）"
                : $"共 {rowCount} 人");
    }

    private async Task<ExportTaskRunResult> RunChangeCsvAsync(
        SysExportTask task,
        ShowKind kind,
        CancellationToken cancellationToken)
    {
        var query = Deserialize<ChangeApplyQueryDto>(task);

        // 两种导出共用一份查询条件，产物不同：申请单清单 / 审核流水（一个节点一行）
        var result = kind == ShowKind.Applies
            ? await _changeCsvBuilder.BuildAppliesAsync(query, cancellationToken)
            : await _changeCsvBuilder.BuildAuditAsync(query, cancellationToken);

        var content = new MemoryStream(Encoding.UTF8.GetBytes(result.Csv));
        var artifact = await _artifacts.WriteAsync(task.Id, "text/csv", content, cancellationToken);

        var label = kind == ShowKind.Applies ? "异动申请" : "异动审核流水";

        // 与其它名单类导出一致：**触顶必须说出来**，并说清差多少
        return new ExportTaskRunResult(
            FileName: $"{label}_{_clock.Now:yyyyMMddHHmmss}.csv",
            ContentType: "text/csv",
            RelativeArtifactPath: artifact.RelativePath,
            FileSize: artifact.Size,
            Total: result.Total,
            RowCount: result.RowCount,
            IsTruncated: result.IsTruncated,
            Message: result.IsTruncated
                ? $"实际导出 {result.RowCount} 行 / 共 {result.Total} 行，已达导出上限（{result.Total - result.RowCount} 行未导出）"
                : $"共 {result.RowCount} 行");

    }

    /// <summary>异动导出的两种产物（决定列与文件名）。</summary>
    private enum ShowKind
    {
        Applies,
        Audit
    }

    private async Task<ExportTaskRunResult> RunCodeCsvAsync(
        SysExportTask task,
        CancellationToken cancellationToken)
    {
        var query = Deserialize<CodeExportQueryDto>(task);

        var result = await _codeCsvBuilder.BuildAsync(query, cancellationToken);

        var content = new MemoryStream(Encoding.UTF8.GetBytes(result.Csv));
        var artifact = await _artifacts.WriteAsync(task.Id, "text/csv", content, cancellationToken);

        // 与组织导出同理：码表数据量有天然上限，不做行数截断
        return new ExportTaskRunResult(
            FileName: $"微代码_{_clock.Now:yyyyMMddHHmmss}.csv",
            ContentType: "text/csv",
            RelativeArtifactPath: artifact.RelativePath,
            FileSize: artifact.Size,
            Total: result.ItemCount,
            RowCount: result.ItemCount,
            IsTruncated: false,
            Message: $"共 {result.ItemCount} 条码项 / {result.CategoryCount} 个类别（列与导入模板一致，可直接改后导入）");
    }

    private async Task<ExportTaskRunResult> RunOrganizationCsvAsync(
        SysExportTask task,
        CancellationToken cancellationToken)
    {
        var query = Deserialize<OrgQueryDto>(task);

        var result = await _orgCsvBuilder.BuildAsync(query, cancellationToken);

        var content = new MemoryStream(Encoding.UTF8.GetBytes(result.Csv));
        var artifact = await _artifacts.WriteAsync(task.Id, "text/csv", content, cancellationToken);

        // 组织数据量有天然上限（一省的学校数以万计，CSV 也就几 MB），
        // 因此这里**不做行数截断**，也就没有 IsTruncated：
        // 加一个永远为 false 的字段只会让人以为"可能会被截断"。
        return new ExportTaskRunResult(
            FileName: $"组织名单_{_clock.Now:yyyyMMddHHmmss}.csv",
            ContentType: "text/csv",
            RelativeArtifactPath: artifact.RelativePath,
            FileSize: artifact.Size,
            Total: result.RowCount,
            RowCount: result.RowCount,
            IsTruncated: false,
            Message: $"共 {result.RowCount} 个组织（列与导入模板一致，可直接改后导入）");
    }

    private async Task<ExportTaskRunResult> RunPhotoZipAsync(
        SysExportTask task,
        CancellationToken cancellationToken)
    {
        var query = Deserialize<PhotoExportQueryDto>(task);

        // ★ 债 22：压缩包**直接写临时文件**，不在内存里拼。
        //
        // 之前是 `ExportAsync` 出一个 byte[]（内部 MemoryStream + ToArray，
        // 峰值内存约等于压缩包的 2 倍），再包成 MemoryStream 交给产物存储 ——
        // 上千张照片的批次会实打实地吃内存，而 worker 与接口同进程。
        //
        // 现在：写临时文件 → 以只读流交给产物存储 → 无论成败都删临时文件。
        // 用临时文件而不是直接把产物流交给 zip，是因为产物存储要"先写 .tmp 再原子改名"，
        // 它需要一个**可读**的输入流（不能边生成边写同一个文件）。
        var tempPath = Path.Combine(Path.GetTempPath(), $"xjgl-photo-export-{task.Id:N}.zip");

        PhotoExportResult result;
        ExportArtifactWriteResult artifact;

        try
        {
            await using (var file = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true))
            {
                result = await _photoExport.ExportToAsync(query, file, cancellationToken);
            }

            await using (var read = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true))
            {
                artifact = await _artifacts.WriteAsync(task.Id, "application/zip", read, cancellationToken);
            }
        }
        finally
        {
            // 临时文件是纯中间产物（内容已经进了产物存储），删失败也不能影响任务结果
            TryDeleteTempFile(tempPath);
        }

        // Total 取"处理过的记录数"（含读不到文件的那部分）：
        // 这里**不能**用 IsTruncated 表示缺文件 —— 缺文件是存储问题，不是被上限砍掉的，
        // 混为一谈会让用户以为"再导一次就能拿到更多"。缺几张写在 Message 里。
        var total = result.PhotoCount + result.SkippedCount;

        // 债 24b：上限触顶必须说出来。"**实际**打包 N 张"这个措辞是刻意的 ——
        // 只说"已打包 500 张"时，用户分不清"一共就 500 张"与"被砍到 500 张"，
        // 而这两种情况下他该做的事完全不同（后者要缩小筛选条件再导一次）。
        var parts = new List<string> { $"实际打包 {result.PhotoCount} 张" };

        if (result.ReachedLimit)
        {
            parts.Add($"已达本次导出上限 {query.MaxCount} 张，可能还有照片未导出");
        }

        if (result.SkippedCount > 0)
        {
            parts.Add($"另有 {result.SkippedCount} 张台账有记录但存储里读不到（见包内「照片清单.csv」）");
        }

        return new ExportTaskRunResult(
            FileName: result.FileName,
            ContentType: "application/zip",
            RelativeArtifactPath: artifact.RelativePath,
            FileSize: artifact.Size,
            Total: total,
            RowCount: result.PhotoCount,
            IsTruncated: result.ReachedLimit,
            Message: string.Join("；", parts));
    }

    /// <summary>删除临时文件；删不掉只记日志，不能让"清理失败"把成功的导出变成失败。</summary>
    private void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[导出] 临时文件清理失败（可忽略，系统临时目录会被回收）：{Path}", path);
        }
    }

    /// <summary>只取总数（MaxResultCount=1），用于判断花名册有没有被截断。</summary>
    private async Task<long> CountMatchingStudentsAsync(
        StudentRosterQueryDto query,
        CancellationToken cancellationToken)
    {
        var page = await _students.GetListAsync(new StudentQueryDto
        {
            SchoolOrgId = query.SchoolOrgId,
            IncludeDescendantOrgs = query.IncludeDescendantOrgs,
            AcademicYear = query.AcademicYear,
            GradeId = query.GradeId,
            ClassId = query.ClassId,
            StudentStatusCode = query.StudentStatusCode,
            Keyword = query.Keyword,
            SkipCount = 0,
            MaxResultCount = 1
        });

        return page.TotalCount;
    }

    private static T Deserialize<T>(SysExportTask task)
    {
        if (string.IsNullOrWhiteSpace(task.ParametersJson))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", $"任务 {task.Id} 缺少参数快照，无法执行");
        }

        var value = JsonSerializer.Deserialize<T>(task.ParametersJson!, JsonOptions);

        if (value == null)
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", $"任务 {task.Id} 的参数快照无法还原为 {typeof(T).Name}");
        }

        return value;
    }
}
