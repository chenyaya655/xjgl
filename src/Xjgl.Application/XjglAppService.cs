using Volo.Abp.Application.Services;

namespace Xjgl;

/// <summary>
/// 本系统应用服务的公共基类。
/// 所有业务应用服务都从这里继承，便于统一挂审计、异常处理等横切逻辑。
/// </summary>
public abstract class XjglAppService : ApplicationService
{
    protected XjglAppService()
    {
        LocalizationResource = typeof(Localization.XjglResource);
    }
}
