using Serilog;
using Serilog.Events;

namespace Xjgl;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Async(c => c.File("Logs/logs.txt", rollingInterval: RollingInterval.Day))
            .WriteTo.Async(c => c.Console())
            .CreateLogger();

        try
        {
            Log.Information("正在启动 Xjgl.HttpApi.Host ...");

            // 【为什么把内容根钉在「程序所在目录」而不是当前工作目录】
            // 容器里是 `WORKDIR /app` + `dotnet /app/api/Xjgl.HttpApi.Host.dll`；
            // CI 与脚本里是 `dotnet run --project src/Xjgl.HttpApi.Host`（当前目录是仓库根）。
            // 这两种情况下"当前目录"都不是 appsettings.json 所在目录，而 ASP.NET Core
            // 默认把 appsettings.json 当**可选**文件 —— 于是应用会**安静地**用代码默认值启动：
            // 照片存储路径、打印字体、学籍号规则、Redis 连接全都悄悄换了值，日志里一句话都没有。
            // 这正是本项目一直在防的「静默降级」，所以改为显式指定，并在找不到时报出来。
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });

            if (!File.Exists(Path.Combine(builder.Environment.ContentRootPath, "appsettings.json")))
            {
                Log.Warning(
                    "内容根下没有 appsettings.json（{ContentRoot}）：本次只用环境变量与代码默认值，请确认这不是配置漏挂",
                    builder.Environment.ContentRootPath);
            }

            Log.Information("内容根：{ContentRoot}", builder.Environment.ContentRootPath);

            builder.Host
                .AddAppSettingsSecretsJson()
                .UseAutofac()
                .UseSerilog();

            await builder.AddApplicationAsync<XjglHttpApiHostModule>();

            var app = builder.Build();
            await app.InitializeApplicationAsync();
            await app.RunAsync();

            return 0;
        }
        catch (Exception ex)
        {
            if (ex is HostAbortedException)
            {
                throw;
            }

            Log.Fatal(ex, "宿主异常终止！");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
