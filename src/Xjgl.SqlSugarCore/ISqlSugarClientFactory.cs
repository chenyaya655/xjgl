using SqlSugar;

namespace Xjgl.SqlSugarCore;

/// <summary>
/// SqlSugar 客户端工厂（单例）。
///
/// 只负责「按配置创建一个客户端」，不缓存任何实例。
///   - 当前工作单元用的客户端由 <see cref="ISqlSugarClientAccessor"/>（Scoped）缓存；
///   - 后台任务、照片补偿任务等需要**脱离当前事务**的场景，
///     用本工厂创建独立客户端。
///
/// 之所以拆成「工厂（Singleton）+ 持有者（Scoped）」：
/// 单例里注入 IServiceProvider 再解析 Scoped 服务会形成 captive dependency，
/// 开发环境下启用作用域校验时会直接抛异常。
/// </summary>
public interface ISqlSugarClientFactory
{
    ISqlSugarClient Create();
}
