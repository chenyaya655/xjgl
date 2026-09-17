using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Xjgl.Permissions;
using Xjgl.Photos;
using Xjgl.Photos.Dtos;
using Xjgl.RateLimiting;

namespace Xjgl.Controllers;

/// <summary>
/// 照片上传接口（multipart/form-data）。
///
/// 为什么不用 ABP 的约定式控制器：
///   约定式控制器基于 DTO，而照片必须以**流**的形式接收。
///   如果改成 DTO + base64，一张 200KB 的照片会变成 270KB 的 JSON 字符串，
///   一次 50 张的批量上传会白白多传几 MB，而且服务端还得先在内存里
///   拼出整个 JSON 才能开始处理。手写控制器可以直接边收边处理。
///
/// 权限：上传要求 <c>Pho.Photo.Upload</c>；**读取照片要求 <c>Pho.Photo.Query</c>** ——
/// 类级不能统一挂 Upload，否则"能看照片"就隐含了"能传照片"。
/// </summary>
[RemoteService]
[Route("api/photos")]
[Authorize]
public class PhotosController : XjglController
{
    /// <summary>单次请求最多接收的文件数，与上传实现共用同一个上限。</summary>
    public const int MaxFilesPerRequest = PhotoLimits.MaxBatchSize;

    private readonly IPhotoUploadService _uploadService;
    private readonly IPhotoContentService _contentService;
    private readonly IPhotoExportService _exportService;
    private readonly ITokenBucketRateLimiter _rateLimiter;

    public PhotosController(
        IPhotoUploadService uploadService,
        IPhotoContentService contentService,
        IPhotoExportService exportService,
        ITokenBucketRateLimiter rateLimiter)
    {
        _uploadService = uploadService;
        _contentService = contentService;
        _exportService = exportService;
        _rateLimiter = rateLimiter;
    }

    /// <summary>
    /// 批量上传照片。
    ///
    /// 表单约定：
    ///   - <c>files</c>：一个或多个文件；
    ///   - <c>studentIds</c>：与 files **顺序一一对应**的学生 Id（同名多值）；
    ///   - <c>batchId</c>（可选）：前端生成的批次 Id，用于进度聚合与按批查错。
    ///
    /// 返回结果里逐张说明成功 / 秒传 / 降级 / 失败，
    /// 因此前端不需要自己猜哪几张没上去。
    /// </summary>
    [HttpPost("upload")]
    [Authorize(MetadataPermissions.Pho.Photo.Upload)]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<IActionResult> UploadAsync(
        [FromForm] List<IFormFile> files,
        [FromForm] List<Guid> studentIds,
        [FromForm] Guid? batchId = null)
    {
        // 限流放在最前面：被挡住时连表单都不必解析
        var limited = await EnforceRateLimitAsync(_rateLimiter, RateLimitPolicies.PhotoUpload, "photo-upload");

        if (limited != null)
        {
            return limited;
        }

        if (files == null || files.Count == 0)
        {
            throw new UserFriendlyException("请选择要上传的照片。");
        }

        if (studentIds == null || studentIds.Count != files.Count)
        {
            throw new UserFriendlyException("学生 Id 的数量必须与照片数量一致。");
        }

        if (files.Count > MaxFilesPerRequest)
        {
            throw new UserFriendlyException(
                $"单次最多上传 {MaxFilesPerRequest} 张照片，本次 {files.Count} 张，请分批上传。");
        }

        var requests = new List<PhotoUploadRequest>(files.Count);

        for (var i = 0; i < files.Count; i++)
        {
            requests.Add(new PhotoUploadRequest
            {
                StudentId = studentIds[i],
                FileName = files[i].FileName,
                Length = files[i].Length,
                Content = files[i].OpenReadStream()
            });
        }

        try
        {
            return Ok(await _uploadService.UploadBatchAsync(batchId ?? Guid.NewGuid(), requests));
        }
        finally
        {
            // 上传服务内部已经把内容读完，这里负责释放表单流
            foreach (var request in requests)
            {
                await request.Content.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// 单张上传（小程序拍照上传、PC 端替换某学生照片）。
    /// </summary>
    /// <remarks>
    /// 注意 <c>IFormFile</c> 参数上**不能**加 <c>[FromForm]</c>：
    /// Swashbuckle 遇到「[FromForm] + IFormFile」会直接抛 SwaggerGeneratorException，
    /// 后果不是这个接口没有文档，而是**整个 /swagger/v1/swagger.json 生成失败（500）**，
    /// 所有接口的文档一起消失。IFormFile 本身就会从表单绑定，不需要这个特性。
    /// （同类问题排查起来很绕：Swagger UI 打不开，但接口调用完全正常。）
    /// </remarks>
    [HttpPost("upload-one")]
    [Authorize(MetadataPermissions.Pho.Photo.Upload)]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> UploadOneAsync(
        IFormFile file,
        [FromForm] Guid studentId,
        [FromForm] Guid? batchId = null)
    {
        var limited = await EnforceRateLimitAsync(_rateLimiter, RateLimitPolicies.PhotoUpload, "photo-upload");

        if (limited != null)
        {
            return limited;
        }

        if (file == null)
        {
            throw new UserFriendlyException("请选择要上传的照片。");
        }

        await using var stream = file.OpenReadStream();

        return Ok(await _uploadService.UploadOneAsync(
            new PhotoUploadRequest
            {
                StudentId = studentId,
                FileName = file.FileName,
                Length = file.Length,
                Content = stream
            },
            batchId));
    }

    /// <summary>
    /// 取学生照片的**缩略图**（列表页用）。
    ///
    /// 为什么要专门一个接口：列表页一屏 20 个学生，如果都去拉原图，
    /// 一次翻页就是几 MB —— 这正是"照片管理打开很慢"的典型原因。
    /// 缩略图长边 480px，通常十几 KB。
    ///
    /// 缓存：返回 <c>ETag</c>（内容哈希）。同一个学生的照片重新上传后哈希会变，
    /// 浏览器因此不会拿到旧图；没变则回 304，不重复传输。
    /// </summary>
    [HttpGet("{studentId:guid}/thumbnail")]
    [Authorize(MetadataPermissions.Pho.Photo.Query)]
    public async Task<IActionResult> ThumbnailAsync(Guid studentId, CancellationToken cancellationToken)
    {
        return await SendPhotoAsync(studentId, thumbnail: true, cancellationToken);
    }

    /// <summary>取学生照片的**原图**（详情/打印/人工核对用）。</summary>
    [HttpGet("{studentId:guid}/content")]
    [Authorize(MetadataPermissions.Pho.Photo.Query)]
    public async Task<IActionResult> ContentAsync(Guid studentId, CancellationToken cancellationToken)
    {
        return await SendPhotoAsync(studentId, thumbnail: false, cancellationToken);
    }

    /// <summary>
    /// 按条件打包导出照片（zip）。
    ///
    /// 压缩包里除了照片还有两份 CSV：**照片清单**（哪张有、哪张在台账里有但读不到）
    /// 与**缺照片名单**（可选）。照片的文件名是「学籍辅号_姓名」，方便和花名册对照。
    ///
    /// 权限同时要求 <c>Export</c> 与 <c>Query</c>：导出内部复用台账查询，
    /// 只给 Export 不给 Query 的话会在内部调用处 403 —— 那是个让人摸不着头脑的报错，
    /// 不如在这里就把要求写清楚。
    /// </summary>
    [HttpGet("export")]
    [Authorize(MetadataPermissions.Pho.Photo.Export)]
    [Authorize(MetadataPermissions.Pho.Photo.Query)]
    public async Task<IActionResult> ExportAsync(
        [FromQuery] PhotoExportQueryDto input,
        CancellationToken cancellationToken)
    {
        // 导出要读大量文件并压缩，比查询重得多，单独给它一个更严的策略
        var limited = await EnforceRateLimitAsync(
            _rateLimiter,
            RateLimitPolicies.PhotoExport,
            "photo-export",
            cancellationToken: cancellationToken);

        if (limited != null)
        {
            return limited;
        }

        var result = await _exportService.ExportAsync(input, cancellationToken);

        return File(result.Content, "application/zip", result.FileName);
    }

    private async Task<IActionResult> SendPhotoAsync(
        Guid studentId,
        bool thumbnail,
        CancellationToken cancellationToken)
    {
        var photo = await _contentService.GetAsync(studentId, thumbnail, cancellationToken);

        if (photo == null)
        {
            // 没有照片就是没有：不回 403（那会让人以为"有照片但没权限"）
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(photo.ETag))
        {
            Response.Headers.ETag = $"\"{photo.ETag}\"";

            if (Request.Headers.IfNoneMatch.ToString().Contains(photo.ETag, StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }
        }

        // private：照片是个人信息，不允许中间代理缓存
        Response.Headers.CacheControl = "private,max-age=86400";

        return File(photo.Content, photo.ContentType);
    }
}
