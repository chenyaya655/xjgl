using Volo.Abp.Settings;

namespace Xjgl.Settings;

/// <summary>
/// 系统参数定义。这些值会写入 ABP 的 AbpSettings 表，
/// 并可通过 SettingManagement 模块（或后续的“系统参数”页面）在运行期修改。
/// </summary>
public class XjglSettingDefinitionProvider : SettingDefinitionProvider
{
    public override void Define(ISettingDefinitionContext context)
    {
        context.Add(
            new SettingDefinition(XjglSettings.DefaultPageSize, "20", isVisibleToClients: true),

            // 学籍辅号规则：默认 = 学校标识码(10 位) + 流水号(6 位)，共 16 位
            new SettingDefinition(
                XjglSettings.StudentNumberRule,
                "{SchoolCode:10}{Seq:6}",
                isVisibleToClients: false),

            // ---- 照片存储与韧性（NAS 熔断降级相关，见方案第 11.5 节）----
            new SettingDefinition(XjglSettings.PhotoRootPath, "/mnt/nas/xjgl", isVisibleToClients: false),
            new SettingDefinition(XjglSettings.PhotoMaxBatchSize, "500", isVisibleToClients: true),
            new SettingDefinition(XjglSettings.PhotoBulkheadSize, "8", isVisibleToClients: false),
            new SettingDefinition(XjglSettings.PhotoCircuitBreakerFailureRate, "0.5", isVisibleToClients: false),
            new SettingDefinition(XjglSettings.PhotoCircuitBreakerSeconds, "30", isVisibleToClients: false),
            new SettingDefinition(XjglSettings.PhotoFallbackEnabled, "true", isVisibleToClients: false),
            new SettingDefinition(XjglSettings.PhotoMinWidth, "240", isVisibleToClients: true),
            new SettingDefinition(XjglSettings.PhotoMinHeight, "320", isVisibleToClients: true)
        );
    }
}
