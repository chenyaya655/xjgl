using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Xjgl.Permissions;
using Xjgl.Students;

namespace Xjgl.Controllers;

/// <summary>
/// 打印接口（学籍卡 / 花名册）。
///
/// 为什么手写控制器而不是约定式应用服务：
///   这两个接口的产物是**二进制 PDF**。若走自动 API，ABP 会把 <c>byte[]</c>
///   序列化成 base64 塞进 JSON —— 一张 200KB 的学籍卡会变成 270KB 的字符串，
///   浏览器也拿不到"另存为"的文件名，只能自己在前端拼 Blob。
///   手写控制器可以直接设置 Content-Type 与 Content-Disposition，
///   让浏览器/下载器按文件处理。
///
/// 权限：统一要求 <c>Stu.Student.Print</c>（权限码来自元数据驱动的动作表，
/// 没有权限的用户连 URL 都打不开，而不是"能下载但不能看"）。
/// </summary>
[RemoteService]
[Route("api/print")]
[Authorize(MetadataPermissions.Stu.Student.Print)]
public class StudentPrintController : XjglController
{
    private readonly IStudentPrintService _printService;

    public StudentPrintController(IStudentPrintService printService)
    {
        _printService = printService;
    }

    /// <summary>打印一名学生的学籍卡（A4 纵向，一人一页）。</summary>
    [HttpGet("student-card/{studentId}")]
    public async Task<IActionResult> StudentCardAsync(Guid studentId, CancellationToken cancellationToken)
    {
        var file = await _printService.RenderStudentCardAsync(studentId, cancellationToken);

        // inline 而不是 attachment：打印场景下多数人希望先在浏览器里预览再打印
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>按筛选条件打印学生花名册（A4 横向，多页带页码）。</summary>
    [HttpGet("student-roster")]
    public async Task<IActionResult> StudentRosterAsync(
        [FromQuery] StudentRosterQueryDto input,
        CancellationToken cancellationToken)
    {
        var file = await _printService.RenderRosterAsync(input, cancellationToken);

        return File(file.Content, file.ContentType, file.FileName);
    }
}
