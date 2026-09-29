using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Modularity;
using Volo.Abp.Testing;

namespace Xjgl;

/// <summary>
/// 集成测试基类。
/// 继承它的测试类可以直接 <c>GetRequiredService&lt;T&gt;()</c> 拿到应用服务，
/// 数据访问由 ABP 的工作单元自动管理。
///
/// **默认以超级管理员身份运行**：绝大多数测试只关心业务行为（能不能建学生、
/// 流程走得对不对），不关心"当前是谁"。而数据范围一开启，
/// 没有身份的调用会解析不出组织、进而什么都看不到 ——
/// 那会让所有与身份无关的用例一起失败，掩盖真正的回归。
/// 需要差异身份的测试（如数据范围）自己在用例里覆盖即可。
/// </summary>
public abstract class XjglIntegrationTestBase : AbpIntegratedTest<XjglTestBaseModule>
{
    protected XjglIntegrationTestBase()
    {
        GetRequiredService<FakeCurrentPrincipalAccessor>()
            .SetUser(Guid.NewGuid(), "test-admin", "admin");
    }

    protected override void SetAbpApplicationCreationOptions(AbpApplicationCreationOptions options)
    {
        options.UseAutofac();
    }

    protected override void BeforeAddApplication(IServiceCollection services)
    {
        services.ReplaceConfiguration(XjglTestConfiguration.Build());
    }

    /// <summary>
    /// 身份证号顺序码计数器。每个测试进程从随机值起步，避免与上一轮运行"从同一个号段开始"。
    /// </summary>
    private static int _idCardSequence = Random.Shared.Next(0, 900_000);

    /// <summary>
    /// 生成一个**合法**且几乎不会重复的身份证号（校验位按国标算出来）。
    ///
    /// 为什么要专门做这件事：集成测试打的是**长期累积**的真实数据库，
    /// 学生主档又对身份证号做了查重（`DuplicateIdCard`）。
    /// 早期用「固定地区码 + 固定出生年 + 3 位随机顺序码」生成，号段只有 899 个，
    /// 跑过若干轮之后就会随机撞上历史记录 —— 表现为**用例偶发变红**。
    /// 这种"偶发红"最消耗排查时间：看起来像业务回归，实际是测试数据的问题。
    ///
    /// 现在：出生日期由计数器推导（12×28 天）+ 顺序码取自计数器，
    /// 单轮内不会重复，跨轮也只是概率极低的小概率；
    /// 再配合 <see cref="RetryOnDuplicateIdCardAsync{T}"/> 兜底。
    /// </summary>
    protected static string NewUniqueIdCard(int birthYear = 2015, string areaCode = "410103")
    {
        var n = Interlocked.Increment(ref _idCardSequence);
        var month = n / 1000 % 12 + 1;
        var day = n / 12000 % 28 + 1;
        var seq = n % 1000;

        var first17 = $"{areaCode}{birthYear}{month:D2}{day:D2}{seq:D3}";
        var weights = new[] { 7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2 };
        const string checkCodes = "10X98765432";

        var sum = 0;

        for (var i = 0; i < 17; i++)
        {
            sum += (first17[i] - '0') * weights[i];
        }

        return first17 + checkCodes[sum % 11];
    }

    /// <summary>
    /// 跨轮唯一的数据前缀，用于**不需要通过姓名校验**的地方（学校名、查询关键字等）。
    ///
    /// 集成测试打的是**长期累积**的真实测试库（仓库的既有约定，见 ROADMAP 第 8/29 条），
    /// 所以任何"统计类"的断言（共几条、导出多少行、进度分母是多少）都必须只看到
    /// **本轮自己造的那批数据**。用固定名字造数据的话，第二轮跑时上一轮留下的记录
    /// 会让 `total` 变大 —— 表现为用例**偶发**变红，而根因在测试数据、不在被测代码。
    /// 于是统一给每轮一个随机前缀，所有查询都按它过滤。
    /// </summary>
    protected static string NewDataPrefix(string purpose)
    {
        return $"{purpose}{Random.Shared.Next(100_000, 999_999)}";
    }

    /// <summary>
    /// 跨轮唯一的**学生姓名**前缀（随机数用汉字写出来）。
    ///
    /// 【为什么不能直接用 <see cref="NewDataPrefix"/>】
    ///   学生姓名走的是与手工录入**完全相同**的校验，`RareCharacterText.IsValidPersonName`
    ///   不接受数字与拉丁字母。用带阿拉伯数字的前缀会在"造数据"这一步就抛
    ///   ValidationFailed，而报错位置与真正要验的东西看起来毫无关系。
    ///   用汉字写数字既保住了跨轮唯一（一万种组合），又是合法姓名。
    /// </summary>
    protected static string NewNamePrefix(string purpose)
    {
        const string digits = "零一二三四五六七八九";

        var n = Random.Shared.Next(1000, 9999);

        return purpose
               + digits[n / 1000]
               + digits[n / 100 % 10]
               + digits[n / 10 % 10]
               + digits[n % 10];
    }

    /// <summary>
    /// 撞上「身份证号已存在」时自动换个号重试。
    ///
    /// **不要**为了让用例稳定而放宽断言（比如不再断言查重），
    /// 也不要因为偶发失败就删掉用例 —— 正确的做法是把不确定性收敛在测试数据生成这一层。
    /// 传入的委托每次重试都会重新执行，因此里面的 <see cref="NewUniqueIdCard"/> 会取到新号。
    /// </summary>
    protected static async Task<T> RetryOnDuplicateIdCardAsync<T>(
        Func<Task<T>> action,
        int maxAttempts = 8)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (BusinessException ex)
                when (attempt < maxAttempts && ex.Code == XjglDomainErrorCodes.DuplicateIdCard)
            {
                // 换一个身份证号再来
            }
        }
    }
}
