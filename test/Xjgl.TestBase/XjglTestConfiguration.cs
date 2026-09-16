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
                ["Xjgl:SqlSugar:EnableSqlLog"] = "false"
            })
            .Build();
    }
}
