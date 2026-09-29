using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Volo.Abp.Autofac;

namespace Xjgl;

/// <summary>
/// 数据库迁移与种子数据初始化控制台程序。
/// 执行顺序：EF Core 迁移（框架表）→ SqlSugar CodeFirst（业务表）→ 种子数据。
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Async(c => c.Console())
            .WriteTo.Async(c => c.File("Logs/migrator-logs.txt", rollingInterval: RollingInterval.Day))
            .CreateLogger();

        try
        {
            Log.Information("========== 学籍管理系统 数据库迁移工具 ==========");
            await CreateHostBuilder(args).RunConsoleAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "数据库迁移失败！");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    internal static IHostBuilder CreateHostBuilder(string[] args)
    {
        return Host.CreateDefaultBuilder(args)
            // 【为什么钉内容根】迁移器是被 CI、部署脚本、VS 从**各自的当前目录**调起来的
            // （CI 里是仓库根：`dotnet run --project src/Xjgl.DbMigrator`），
            // 而默认的内容根就是"当前目录"，于是它会在仓库根找 appsettings.json 并直接崩：
            //   The configuration file 'appsettings.json' was not found and is not optional.
            //   The expected physical path was '<当前目录>\appsettings.json'
            // appsettings.json 是随生成复制到输出目录的，所以按"程序所在目录"找才是确定的。
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureAppConfiguration((hostContext, config) =>
            {
                config
                    .AddJsonFile("appsettings.json", optional: false)
                    .AddJsonFile($"appsettings.{hostContext.HostingEnvironment.EnvironmentName}.json", optional: true)
                    .AddEnvironmentVariables();
            })
            .ConfigureLogging((_, logging) =>
            {
                logging.ClearProviders();
                logging.AddSerilog(Log.Logger, dispose: false);
            })
            // 注意：外层宿主不要用 UseAutofac()。
            // ABP 的 Autofac 集成要求容器里已有 IModuleContainer，
            // 而它是在 AbpApplicationFactory.CreateAsync 里注册的（见 DbMigratorHostedService）。
            .ConfigureServices((_, services) =>
            {
                services.AddHostedService<DbMigratorHostedService>();
            });
    }
}
