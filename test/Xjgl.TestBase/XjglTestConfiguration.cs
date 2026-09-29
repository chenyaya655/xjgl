using Microsoft.Extensions.Configuration;

namespace Xjgl;

/// <summary>
/// 集成测试的配置。
///
/// 连接串优先取环境变量 <c>XJGL_TEST_CONNECTION</c>，
/// 未设置时退回本机开发用的测试库（xjgl_test，端口 55432）。
/// CI 上只需注入环境变量即可指向自己的测试库。
/// </summary>
public static class XjglTestConfiguration
{
    /// <summary>本机测试库默认连接串（仅开发用，可用环境变量覆盖）。</summary>
    public const string DefaultConnectionString =
        "Host=127.0.0.1;Port=55432;Database=xjgl_test;User ID=xjgl;Password=Xjgl@Dev2026";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("XJGL_TEST_CONNECTION") is { Length: > 0 } cs
            ? cs
            : DefaultConnectionString;

    /// <summary>
    /// 照片测试的存储根目录（在测试输出目录下，测试运行时可任意增删）。
    ///
    /// 测试**不碰真实 NAS**：把「NAS 主存储」的根目录指到一个本地目录，
    /// 需要模拟 NAS 故障时就把这个目录换成一个同名文件 ——
    /// 目录创建必然失败，于是写入失败、熔断、降级全部真实发生，
    /// 而恢复只需把文件删掉。这样能在毫秒级、可重复地演练完整的故障链路。
    /// </summary>
    public static string PhotoBaseDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "App_Data", "photo-tests");

    /// <summary>模拟 NAS 主存储的根目录。</summary>
    public static string NasRoot => Path.Combine(PhotoBaseDirectory, "nas");

    /// <summary>降级存储根目录。</summary>
    public static string FallbackRoot => Path.Combine(PhotoBaseDirectory, "fallback");

    /// <summary>
    /// 异动附件的存储根目录（在测试输出目录下，测试运行时可任意增删）。
    ///
    /// 必须显式指定：生产默认值落在**程序所在目录**的 App_Data 下，
    /// 而集成测试的程序目录是 bin/Debug/net10.0 —— 用默认值会把附件写进构建产物目录，
    /// 下一轮 `dotnet build` 时被清掉，表现为"用例偶发找不到文件"。
    /// 指到 App_Data/change-attachment-tests 之后，失败也只会是断言失败，不是环境问题。
    /// </summary>
    public static string AttachmentRoot { get; } =
        Path.Combine(AppContext.BaseDirectory, "App_Data", "change-attachment-tests");

    public static IConfigurationRoot Build()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                ["Xjgl:SqlSugar:ConnectionStringName"] = "Default",
                ["Xjgl:SqlSugar:DbType"] = "PostgreSQL",
                ["Xjgl:SqlSugar:UseSnakeCaseTableName"] = "true",
                ["Xjgl:SqlSugar:CommandTimeoutSeconds"] = "60",
                ["Xjgl:SqlSugar:EnableSqlLog"] = "false",

                // 照片存储：把 NAS 主存储指向本地目录，测试里可以随时「弄坏/修好」它
                ["Xjgl:Photo:NasRoot"] = NasRoot,
                ["Xjgl:Photo:FallbackRoot"] = FallbackRoot,
                ["Xjgl:Photo:BulkheadSize"] = "4",
                ["Xjgl:Photo:TimeoutSeconds"] = "10",

                // 照片导出的取数页大小设为 2（生产默认 1000）。
                //
                // 这条取数逻辑的价值全在"第一页取不到足够可导出的照片时要继续翻页"
                // （债 24b），而要触发翻页必须让结果集超过一页。用真实页大小
                // （1000）就得往共享测试库里插 1000+ 条数据 —— 慢，而且每跑一次
                // 就多留 1000 条垃圾。把页大小调小，3 条数据就能覆盖同一条路径。
                ["Xjgl:Photo:ExportPageSize"] = "2",

                // 熔断参数调小，让测试能在几百毫秒内观察到「熔断→恢复」
                ["Xjgl:Photo:MinimumSamples"] = "3",
                ["Xjgl:Photo:FailureRateThreshold"] = "0.5",
                ["Xjgl:Photo:OpenSeconds"] = "1",

                // 打印字体：集成测试会真的渲染 PDF，因此必须能在当前环境找到含中文的字体。
                // Windows 上默认微软雅黑；CI 的 Linux 镜像装 fonts-noto-cjk 并通过
                // 环境变量 XJGL_PRINT_FONT 指定字体族。
                ["Xjgl:Print:DefaultFontFamily"] = PrintFontFamily,
                ["Xjgl:Print:UseSystemFonts"] = "true",

                // 异动附件存储：指向测试输出目录（理由见 AttachmentRoot 的说明）
                ["Xjgl:Change:AttachmentRootPath"] = AttachmentRoot
            })
            .Build();
    }

    /// <summary>打印用的中文字体族（可用环境变量覆盖，便于在 Linux/CI 上跑）。</summary>
    public static string PrintFontFamily =>
        Environment.GetEnvironmentVariable("XJGL_PRINT_FONT") ?? "Microsoft YaHei";
}
