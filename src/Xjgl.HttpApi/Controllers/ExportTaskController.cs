using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Xjgl.Exporting;

namespace Xjgl.Controllers;

/// <summary>
/// 导出/打印产物的下载接口。
///
/// 为什么手写控制器而不是走自动 API：产物是**二进制流**。
/// 自动 API 会把流序列化成 base64 塞进 JSON —— 一个 5MB 的 zip 变成 6.7MB 的字符串，
/// 浏览器也拿不到"另存为"的文件名。（与 StudentPrintController 同一个理由。）
///
/// 权限：这里**不挂固定权限码**，因为所需权限取决于任务种类
/// （名单导出要 Export、打印要 Print、照片要 Export+Query），
/// 而种类只有拿到任务记录才知道。判断放在
/// <see cref="IExportTaskDownloadService"/> 里做，那里能同时完成
/// "是不是我的任务"与"我现在还有没有这个权限"两道检查。
/// </summary>
[RemoteService]
[Route("api/export-task")]
[Authorize]
public class ExportTaskController : XjglController
{
    private readonly IExportTaskDownloadService _downloads;

    public ExportTaskController(IExportTaskDownloadService downloads)
    {
        _downloads = downloads;
    }

    /// <summary>
    /// 下载产物。
    ///
    /// 三种结果：
    ///   - 200：流式返回文件；
    ///   - 404：任务不存在或**不属于当前用户**（刻意不区分，避免泄露任务是否存在）；
    ///   - 409：任务还没成功（排队/执行中/失败/取消/过期），带可读原因。
    /// </summary>
    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> DownloadAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await _downloads.GetAsync(id, cancellationToken);

        switch (result.Status)
        {
            case ExportTaskDownloadStatus.Ready:
                // enableRangeProcessing: false 是默认值。断点续传对"一次性下载的导出件"
                // 没有实际收益，而打开它就要处理 If-Range/多段响应等一整套语义。
                return File(result.Content!, result.ContentType!, result.FileName!);

            case ExportTaskDownloadStatus.NotFound:
                return NotFound();

            default:
                return Conflict(new { message = result.Message });
        }
    }
}
