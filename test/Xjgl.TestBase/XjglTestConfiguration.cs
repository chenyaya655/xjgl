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

                // 熔断参数调小，让测试能在几百毫秒内观察到「熔断→恢复」
                ["Xjgl:Photo:MinimumSamples"] = "3",
                ["Xjgl:Photo:FailureRateThreshold"] = "0.5",
                ["Xjgl:Photo:OpenSeconds"] = "1",

                // 打印字体：集成测试会真的渲染 PDF，因此必须能在当前环境找到含中文的字体。
                // Windows 上默认微软雅黑；CI 的 Linux 镜像装 fonts-noto-cjk 并通过
                // 环境变量 XJGL_PRINT_FONT 指定字体族。
                ["Xjgl:Print:DefaultFontFamily"] = PrintFontFamily,
                ["Xjgl:Print:UseSystemFonts"] = "true"
            })
            .Build();
    }

    /// <summary>打印用的中文字体族（可用环境变量覆盖，便于在 Linux/CI 上跑）。</summary>
    public static string PrintFontFamily =>
        Environment.GetEnvironmentVariable("XJGL_PRINT_FONT") ?? "Microsoft YaHei";
}
