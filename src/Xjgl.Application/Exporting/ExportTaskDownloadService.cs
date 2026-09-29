using Volo.Abp.Authorization.Permissions;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Users;
using Xjgl.Data;

namespace Xjgl.Exporting;

/// <summary>
/// 产物下载的实现。
///
/// 【为什么要再查一次权限，而不是只认"发起人"】
///   发起人当初有权限、现在可能已经没有了（调岗、权限收回）。
///   只认发起人等于把权限判断**冻结在入队那一刻**，
///   而"今天已经不能导出学生名单了，却还能下载昨天导出的名单"是不合理的。
///   两道判断都过才放行：是自己的任务（归属）**且**仍然具备该种类的权限。
/// </summary>
public class ExportTaskDownloadService : IExportTaskDownloadService, ITransientDependency
{
    private readonly IXjglRepository<SysExportTask> _tasks;
    private readonly IExportArtifactStore _artifacts;
    private readonly IPermissionChecker _permissionChecker;
    private readonly ICurrentUser _currentUser;

    public ExportTaskDownloadService(
        IXjglRepository<SysExportTask> tasks,
        IExportArtifactStore artifacts,
        IPermissionChecker permissionChecker,
        ICurrentUser currentUser)
    {
        _tasks = tasks;
        _artifacts = artifacts;
        _permissionChecker = permissionChecker;
        _currentUser = currentUser;
    }

    public async Task<ExportTaskDownloadResult> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var task = await _tasks.FindAsync(id, cancellationToken);

        // 不存在与不是自己的，返回同一个结果：区分开就等于提供了
        // 一个"枚举系统里有哪些任务 Id"的探针。
        if (task == null || task.RequestedByUserId != _currentUser.Id)
        {
            return new ExportTaskDownloadResult(
                ExportTaskDownloadStatus.NotFound, null, null, null, null);
        }

        foreach (var permission in ExportTaskPermissions.GetRequiredPermissions(task.Kind))
        {
            if (!await _permissionChecker.IsGrantedAsync(permission))
            {
                return new ExportTaskDownloadResult(
                    ExportTaskDownloadStatus.NotFound, null, null, null, null);
            }
        }

        if (task.Status != ExportTaskStatuses.Succeeded || string.IsNullOrWhiteSpace(task.RelativeArtifactPath))
        {
            return new ExportTaskDownloadResult(
                ExportTaskDownloadStatus.NotReady, null, null, null,
                task.Status switch
                {
                    ExportTaskStatuses.Pending => "任务正在排队，请稍后重试",
                    ExportTaskStatuses.Running => "任务正在执行，请稍后重试",
                    ExportTaskStatuses.Failed => $"任务执行失败：{task.FailureReason ?? "原因未知"}",
                    ExportTaskStatuses.Canceled => "任务已取消",
                    ExportTaskStatuses.Expired => "产物已过保留期并被清理，请重新导出",
                    _ => "产物不可用"
                });
        }

        var stream = await _artifacts.OpenReadAsync(task.RelativeArtifactPath!, cancellationToken);

        if (stream == null)
        {
            // 记录说产物在、磁盘上没有（被人工删了、磁盘换了）。
            // 这种情况**必须让用户看见**：静默回一个空文件会让他以为导出本来就是空的。
            return new ExportTaskDownloadResult(
                ExportTaskDownloadStatus.NotReady, null, null, null,
                "产物文件在存储中不存在，请重新导出");
        }

        return new ExportTaskDownloadResult(
            ExportTaskDownloadStatus.Ready,
            stream,
            task.FileName ?? "导出结果",
            task.ContentType ?? "application/octet-stream",
            null);
    }
}
