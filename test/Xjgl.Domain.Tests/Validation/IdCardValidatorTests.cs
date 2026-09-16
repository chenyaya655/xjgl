using Shouldly;
using Xunit;

namespace Xjgl.Validation;

/// <summary>
/// 身份证号校验的单元测试。
/// 身份证号是学籍查重与唯一索引的关键，必须在入口处严格校验。
/// </summary>
public class IdCardValidatorTests
{
    /// <summary>标准测试号：校验位为 X。</summary>
    private const string ValidId = "11010519491231002X";

    [Fact]
    public void 合法身份证号通过校验()
    {
        IdCardValidator.TryNormalize(ValidId, out var normalized, out var error).ShouldBeTrue(error);

        normalized.ShouldBe(ValidId);
    }

    [Fact]
    public void 校验位错误会被拒绝()
    {
        // 末位由 X 改成 1
        IdCardValidator.TryNormalize("110105194912310021", out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("11010519491331002X")] // 月份 13
    [InlineData("11010519490231002X")] // 2 月 31 日
    [InlineData("1101051949123A002X")] // 含字母
    [InlineData("1101051949123100")]   // 长度不足
    [InlineData("")]
    [InlineData(null)]
    public void 非法身份证号被拒绝(string? input)
    {
        IdCardValidator.TryNormalize(input, out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void 十五位老号会被补齐为十八位()
    {
        IdCardValidator.TryNormalize("110105491231002", out var normalized, out var error)
            .ShouldBeTrue(error);

        normalized.ShouldBe(ValidId);
    }

    [Fact]
    public void 小写字母_x_也能通过()
    {
        IdCardValidator.TryNormalize("11010519491231002x", out var normalized, out _).ShouldBeTrue();

        normalized.ShouldBe(ValidId);
    }

    [Fact]
    public void 解析出生日期()
    {
        IdCardValidator.TryGetBirthDate(ValidId, out var birthDate).ShouldBeTrue();

        birthDate.Year.ShouldBe(1949);
        birthDate.Month.ShouldBe(12);
        birthDate.Day.ShouldBe(31);
    }

    [Fact]
    public void 解析性别_第十七位奇数为男_偶数为女()
    {
        // 11010519491231002X 的第 17 位是 2（偶数）→ 女
        IdCardValidator.TryGetGender(ValidId, out var isMale).ShouldBeTrue();
        isMale.ShouldBeFalse();

        // 换一个第 17 位为奇数的合法号：11010519491231013X
        // （出生日期与校验位同步调整，见 GenerateValidId 测试辅助）
        var maleId = BuildValidId("11010519491231013");
        IdCardValidator.TryGetGender(maleId, out var male).ShouldBeTrue();
        male.ShouldBeTrue();
    }

    [Fact]
    public void 哈希键是确定性的且不泄露原号()
    {
        var key = System.Text.Encoding.UTF8.GetBytes("xjgl-dev-hmac-key");

        var h1 = IdCardValidator.ComputeIdCardHash(ValidId, key);
        var h2 = IdCardValidator.ComputeIdCardHash(ValidId.ToLowerInvariant(), key);

        h1.ShouldBe(h2);                       // 大小写归一后一致
        h1.Length.ShouldBe(64);                // SHA-256 十六进制
        h1.ShouldNotContain("110105");         // 不包含原文片段
    }

    /// <summary>给定前 17 位，补出正确的校验位。</summary>
    private static string BuildValidId(string first17)
    {
        var weights = new[] { 7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2 };
        const string checkCodes = "10X98765432";

        var sum = 0;
        for (var i = 0; i < 17; i++)
        {
            sum += (first17[i] - '0') * weights[i];
        }

        return first17 + checkCodes[sum % 11];
    }
}
