using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Xjgl.EntityFrameworkCore;

/// <summary>
/// 供 EF Core 命令行工具（dotnet ef migrations add / database update）使用的工厂。
/// 迁移命令示例：
///   dotnet ef migrations add Initial --project src/Xjgl.EntityFrameworkCore --startup-project src/Xjgl.DbMigrator
/// </summary>
public class XjglDbContextFactory : IDesignTimeDbContextFactory<XjglDbContext>
{
    public XjglDbContext CreateDbContext(string[] args)
    {
        var configuration = BuildConfiguration();

        var builder = new DbContextOptionsBuilder<XjglDbContext>()
            .UseNpgsql(configuration.GetConnectionString("Default"));

        return new XjglDbContext(builder.Options);
    }

    private static IConfigurationRoot BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(FindDbMigratorFolder())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    /// <summary>
    /// 设计时命令的工作目录通常是项目目录，这里向上找到 DbMigrator 的 appsettings。
    /// </summary>
    private static string FindDbMigratorFolder()
    {
        var current = Directory.GetCurrentDirectory();
        var candidate = Path.Combine(current, "..", "Xjgl.DbMigrator");
        if (Directory.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        var candidate2 = Path.Combine(current, "src", "Xjgl.DbMigrator");
        if (Directory.Exists(candidate2))
        {
            return Path.GetFullPath(candidate2);
        }

        return current;
    }
}
