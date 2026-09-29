using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;
using Xjgl.Printing;

namespace Xjgl.Reporting;

/// <summary>
/// PDF 渲染模块。
///
/// 只依赖 `Xjgl.Domain.Shared`（拿视图模型与接口）与 QuestPDF，
/// **不依赖业务层**：渲染是"把数据排成版面"，不该反向依赖领域模型与数据库。
/// 应用层通过 <see cref="IStudentCardPdfRenderer"/> 调用它，实现由 Host 在这里装配。
/// </summary>
[DependsOn(typeof(XjglDomainSharedModule))]
public class XjglReportingModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // 注意：QuestPDF 的许可证声明不在这里，而在 PrintFontRegistry.EnsureInitialized()。
        // 原因是"必须在使用前设置"—— 放在模块里只覆盖"通过 ABP 装配"的路径，
        // 单元测试直接 new 渲染器时就会漏掉（实测被 9 个用例逮住）。
        // 放在渲染必经的初始化点，两条路径都覆盖得到。
        context.Services.AddSingleton<PrintFontRegistry>();
        context.Services.AddTransient<IStudentCardPdfRenderer, StudentCardPdfRenderer>();
        context.Services.AddTransient<IStudentRosterPdfRenderer, StudentRosterPdfRenderer>();
    }
}

/// <summary>
/// 打印字体登记。
///
/// 【为什么需要它】
/// QuestPDF 自带的默认字体（Lato）**不含中文**，不指定字体族时中文会整片变成方块；
/// 而学籍档案里还可能出现在国标字库之外的生僻字（如「𠮷」）。
/// 这里做三件事：
///   1. 指定一个**确定含中文的字体族**（`Xjgl:Print:DefaultFontFamily`）；
///   2. 把生僻字字体目录加入 QuestPDF 的字体发现路径，并登记备用字体族
///      （`Xjgl:Print:RareCharacterFontFamily`，如 Plangothic 分片）；
///   3. 决定字体来源策略。
///
/// 【字体来源的取舍：默认用系统字体】
/// QuestPDF 2026 默认 **不使用**系统字体（只认随应用部署的字体文件），理由是容器/云环境
/// 往往没有字体，输出会随环境变化。但本系统的部署形态是**教育系统的 Windows 服务器**，
/// 中文字体（微软雅黑）必然存在，因此这里默认 `UseSystemFonts = true`，
/// 让开箱即用；容器化部署时把 `Xjgl:Print:UseSystemFonts` 设为 false，
/// 并把 `Xjgl:Print:RareCharacterFontDirectory` 指向随镜像部署的字体目录即可。
///
/// 字体文件**不进仓库**（体积大且有各自的授权），缺失时不会因此报错；
/// 但真的画不出字形时会报错（见 <c>ThrowOnMissingTextGlyphs</c>）。
/// </summary>
public sealed class PrintFontRegistry
{
    private static readonly object SyncRoot = new();

    /// <summary>
    /// 已经向 QuestPDF 注册过字体的目录（进程级，大小写不敏感）。
    ///
    /// 存在的原因：本类在 DI 里是单例，但单元测试会**直接 new**（每个用例一个新实例），
    /// 于是同一个目录会被反复注册。用这个集合把注册动作收敛成"每个目录一次"。
    /// </summary>
    private static readonly HashSet<string> RegisteredDirectories =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IConfiguration _configuration;

    private bool _initialized;

    public PrintFontRegistry(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>正文默认字体族（含中文）。</summary>
    public string DefaultFontFamily { get; private set; } = "Microsoft YaHei";

    /// <summary>生僻字备用字体族；未配置或字体缺失时为 null。</summary>
    public string? RareCharacterFontFamily { get; private set; }

    /// <summary>配置的生僻字字体目录是否存在（用于启动日志与健康检查）。</summary>
    public bool RareCharacterFontAvailable { get; private set; }

    /// <summary>生僻字字体目录（绝对路径，可能为空）。</summary>
    public string? RareCharacterFontDirectory { get; private set; }

    /// <summary>是否使用系统已安装字体（默认 true，见类注释里的取舍说明）。</summary>
    public bool UseSystemFonts { get; private set; } = true;

    /// <summary>
    /// 初始化（幂等，进程内只做一次）。
    ///
    /// 之所以放在这里而不是模块初始化：字体目录来自配置，
    /// 而集成测试会替换配置，模块初始化时读到的未必是最终配置；
    /// 另外单元测试会直接 new 渲染器，跳过 ABP 装配也能正常初始化。
    /// </summary>
    public void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            // QuestPDF 要求在使用前声明许可证等级，否则第一次生成就抛异常。
            // Community 适用于年收入低于 100 万美元的组织；商用请自行购买并替换这里。
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

            var configuredDefault = _configuration["Xjgl:Print:DefaultFontFamily"];

            if (!configuredDefault.IsNullOrWhiteSpace())
            {
                DefaultFontFamily = configuredDefault!;
            }

            UseSystemFonts = !string.Equals(
                _configuration["Xjgl:Print:UseSystemFonts"], "false", StringComparison.OrdinalIgnoreCase);

            QuestPDF.Settings.UseSystemFonts = UseSystemFonts;

            // 缺少字体族本身不致命（会退到其它已注册字体），
            // 但**画不出字形**必须报错：纸质学籍卡上出现方块是绝对不能接受的，
            // 与其悄悄出一张糊弄人的卡片，不如让接口报错并提示运维去装字体。
            QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
            QuestPDF.Settings.ThrowOnMissingTextGlyphs = true;

            RareCharacterFontFamily = _configuration["Xjgl:Print:RareCharacterFontFamily"];
            RareCharacterFontDirectory = _configuration["Xjgl:Print:RareCharacterFontDirectory"];

            if (!RareCharacterFontDirectory.IsNullOrWhiteSpace()
                && Directory.Exists(RareCharacterFontDirectory))
            {
                // 用 FontManager.RegisterFontsFromDirectory 注册该目录下的字体
                // （ttf / otf / ttc / pfb，含子目录；不合法的文件会被跳过）。
                //
                // 【为什么不用 Settings.FontDiscoveryPaths.Add】
                //   那个属性在 QuestPDF 2026.9 起已 obsolete（原话 "has been replaced"），
                //   官方点名的替代品就是本方法。留着它眼下虽然还能跑，但
                //   (a) 它属于"随时可能被删掉"的写法，(b) 每台机器的构建输出里都会多一条
                //   无关警告，而警告多了就会把真问题淹没。
                //
                // 【为什么要 RegisteredDirectories 这一层】
                //   PrintFontRegistry 在 DI 里是单例，但**单元测试会直接 new 它**
                //   （每个用例一个新实例），于是同一个目录会被注册多次。
                //   用一个进程级集合记下"已经注册过的目录"，既避免重复扫描
                //   （目录里几百个字体文件时这个开销并不小），
                //   也让"注册"与"实例被创建了几次"解耦。
                if (RegisteredDirectories.Add(RareCharacterFontDirectory!))
                {
                    QuestPDF.Drawing.FontManager.RegisterFontsFromDirectory(RareCharacterFontDirectory!);
                }

                RareCharacterFontAvailable = HasFontFile(RareCharacterFontDirectory!);
            }
            else
            {
                RareCharacterFontAvailable = false;
            }

            if (!RareCharacterFontAvailable)
            {
                // 字体不可用时不要登记字体族：登记了只会在日志里刷"找不到字体"的警告
                RareCharacterFontFamily = null;
            }

            _initialized = true;
        }
    }

    private static bool HasFontFile(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Any(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                      || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                      || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase));
    }
}
