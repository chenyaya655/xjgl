using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Xjgl.Changes;
using Xjgl.Permissions;

namespace Xjgl.Controllers;

/// <summary>
/// 异动附件接口（multipart 上传 + 二进制下载）。
///
/// 为什么不用 ABP 的约定式控制器：附件必须以**流**的形式收与发
/// （与照片同一个理由，见 <c>PhotosController</c>）。
///
/// 权限分两档，类级**不**统一挂一个：
///   - 上传 / 删除 = <c>Chg.Apply.Update</c>（改的是申请单的材料）；
///   - 列表 / 下载 = 登录即可，真正的读权限在应用服务里判
///     （<c>Chg.Apply.Query</c> **或** <c>Chg.Audit.Query</c>，任一即可 ——
///     审核人要看得见材料才能批，而 ABP 的 <c>[Authorize(A, B)]</c> 表达不了"任一"）。
/// 一条**新权限码都没有引入**：附件是申请单的一部分，能被谁看、被谁改，
/// 与申请单本身完全一致；为它单开权限只会在授权界面上多出两个
/// 没人理解的勾选项（而"勾了不影响行为"的权限项比没有更坏）。
/// 数据范围（跨组织读不到）在应用服务层把关，接口层管不到。
/// </summary>
[RemoteService]
[Route("api/change-attachment")]
[Authorize]
public class ChangeAttachmentController : XjglController
{
    /// <summary>
    /// 单次请求体上限，**略高于** 10MB 的业务上限。
    ///
    /// 刻意留出余量：请求体超限时是 Kestrel 直接回 413（没有可读原因），
    /// 而略超 10MB 是很常见的情形（老师拍的照片、扫描件）。留出余量后，
    /// 这类请求能走到应用层，拿到"附件大小 12.3 MB 超过上限 10 MB"这句
    /// 能照着改的提示；真正离谱的大文件（超过本值）才由框架挡掉。
    /// </summary>
    private const int RequestSizeLimitBytes = 16 * 1024 * 1024;

    private readonly IChangeAttachmentAppService _attachments;

    public ChangeAttachmentController(IChangeAttachmentAppService attachments)
    {
        _attachments = attachments;
    }

    /// <summary>
    /// 上传附件。
    ///
    /// 表单约定：<c>file</c> 为文件本体，<c>applyId</c> 为所属申请单。
    ///
    /// 注意 <c>IFormFile</c> 参数上**不能**加 <c>[FromForm]</c>：
    /// Swashbuckle 遇到「[FromForm] + IFormFile」会抛 SwaggerGeneratorException，
    /// 后果是整个 /swagger/v1/swagger.json 生成失败（500），所有接口的文档一起消失
    /// （与 PhotosController 里记录的是同一个坑）。
    /// </summary>
    [HttpPost("upload")]
    [Authorize(MetadataPermissions.Chg.Apply.Update)]
    [RequestSizeLimit(RequestSizeLimitBytes)]
    public async Task<IActionResult> UploadAsync(IFormFile file, [FromForm] Guid applyId)
    {
        if (file == null || file.Length == 0)
        {
            throw new UserFriendlyException("请选择要上传的附件。");
        }

        await using var stream = file.OpenReadStream();

        var result = await _attachments.UploadAsync(
            applyId, file.FileName, file.ContentType, stream, file.Length);

        return Ok(result);
    }

    /// <summary>
    /// 某张申请单的附件列表。
    ///
    /// 这里只要求登录，读权限由应用服务用 `IPermissionChecker` 判
    /// （申请查询 **或** 审核查询，任一即可）—— 因为 ABP 的
    /// `[Authorize(A, B)]` 是**全部满足**语义，表达不了"任一"。
    /// </summary>
    [HttpGet]
    [Authorize]
    public async Task<IActionResult> GetListAsync([FromQuery] Guid applyId)
    {
        return Ok(await _attachments.GetListAsync(applyId));
    }

    /// <summary>
    /// 下载附件。
    ///
    /// 文件名交给 <c>File(...)</c> 处理：ASP.NET Core 会按 RFC 5987 把非 ASCII
    /// 文件名写成 <c>filename*=UTF-8''…</c>，中文材料名（如「户口簿.pdf」）
    /// 因此不会变成乱码，也不会在"另存为"时丢掉名字。
    /// </summary>
    [HttpGet("{id:guid}/download")]
    [Authorize]
    public async Task<IActionResult> DownloadAsync(Guid id)
    {
        var (content, fileName, contentType) = await _attachments.DownloadAsync(id);

        return File(content, contentType, fileName);
    }

    /// <summary>删除附件（记录与存储中的文件一起删）。</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(MetadataPermissions.Chg.Apply.Update)]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        await _attachments.DeleteAsync(id);

        return NoContent();
    }
}
