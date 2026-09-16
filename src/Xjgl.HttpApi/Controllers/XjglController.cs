using Volo.Abp.AspNetCore.Mvc;

namespace Xjgl.Controllers;

/// <summary>
/// 本系统 Controller 的公共基类。
/// </summary>
public abstract class XjglController : AbpControllerBase
{
    protected XjglController()
    {
        LocalizationResource = typeof(Localization.XjglResource);
    }
}
