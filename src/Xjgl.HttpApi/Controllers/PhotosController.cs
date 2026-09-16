using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Xjgl.Permissions;
using Xjgl.Photos;
using Xjgl.Photos.Dtos;

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
/// 权限：统一要求 <c>Pho.Photo.Upload</c>；删除/查询仍走应用服务。
/// </summary>
[RemoteService]
[Route("api/photos")]
[Authorize(MetadataPermissions.Pho.Photo.Upload)]
public class PhotosController : XjglController
{
    /// <summary>单次请求最多接收的文件数，与上传实现共用同一个上限。</summary>
    public const int MaxFilesPerRequest = PhotoLimits.MaxBatchSize;

    private readonly IPhotoUploadService _uploadService;

    public PhotosController(IPhotoUploadService uploadService)
    {
        _uploadService = uploadService;
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
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<PhotoUploadResultDto> UploadAsync(
        [FromForm] List<IFormFile> files,
        [FromForm] List<Guid> studentIds,
        [FromForm] Guid? batchId = null)
    {
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
            return await _uploadService.UploadBatchAsync(batchId ?? Guid.NewGuid(), requests);
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
    [HttpPost("upload-one")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<PhotoUploadItemResultDto> UploadOneAsync(
        [FromForm] IFormFile file,
        [FromForm] Guid studentId,
        [FromForm] Guid? batchId = null)
    {
        if (file == null)
        {
            throw new UserFriendlyException("请选择要上传的照片。");
        }

        await using var stream = file.OpenReadStream();

        return await _uploadService.UploadOneAsync(
            new PhotoUploadRequest
            {
                StudentId = studentId,
                FileName = file.FileName,
                Length = file.Length,
                Content = stream
            },
            batchId);
    }
}
