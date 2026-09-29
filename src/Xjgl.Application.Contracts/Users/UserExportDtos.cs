using System.ComponentModel.DataAnnotations;

namespace Xjgl.Users;

/// <summary>
/// 账号清单导出的筛选条件（ROADMAP 第 17 条）。
///
/// 【为什么只有一个 Filter，而不是把用户实体上的字段都摊开】
///   这里刻意**只放页面上那一个关键字**。多写几个筛选字段看着更"完备"，
///   但导出实现读的是 `IIdentityUserRepository.GetListAsync(filter, ...)` ——
///   它只认这一个参数，别的字段会被静默忽略；而"选了按角色导出、结果导出了全部"
///   正是本仓库反复强调要避免的静默失败（同样的取舍写在 `types.ts` 的 `orgQuery` 注释里）。
///   `filter` 的语义与页面上那个输入框完全一致：用户名 / 姓名 / 邮箱 / 手机号，
///   因此"看到什么就导出什么"这句话在这里是字面成立的。
/// </summary>
public class UserExportQueryDto
{
    /// <summary>关键字（用户名 / 姓名 / 邮箱 / 手机号）。留空 = 全部账号。</summary>
    [StringLength(128)]
    public string? Filter { get; set; }
}
