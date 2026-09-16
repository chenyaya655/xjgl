using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;

namespace Xjgl.Controllers;

/// <summary>
/// 系统信息接口，用于冒烟测试与运维探活。
/// GET /api/system/info
/// </summary>
[RemoteService]
[Route("api/system")]
public class SystemInfoController : XjglController
{
    [HttpGet("info")]
    public Task<object> GetInfoAsync()
    {
        return Task.FromResult<object>(new
        {
            application = "河南省学籍管理系统",
            version = "1.0.0",
            framework = ".NET 10 / ABP 10.6",
            serverTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            environment = Environment.MachineName
        });
    }

    /// <summary>不需登录的探活接口。</summary>
    [HttpGet("ping")]
    [AllowAnonymous]
    public Task<string> PingAsync()
    {
        return Task.FromResult("pong");
    }
}
