namespace Xjgl.Data;

/// <summary>
/// 空实现的迁移器，用于单元测试等不需要真实数据库的场景。
/// </summary>
public class NullXjglDbSchemaMigrator : IXjglDbSchemaMigrator
{
    public int Order => int.MaxValue;

    public Task MigrateAsync()
    {
        return Task.CompletedTask;
    }
}
