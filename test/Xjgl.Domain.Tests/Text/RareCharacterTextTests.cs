using Shouldly;
using Xjgl.Text;
using Xunit;

namespace Xjgl.Text;

/// <summary>
/// 生僻字处理的单元测试。
/// 这些用例的意义在于：把「不能用 string.Length 处理姓名」这条约束固化下来，
/// 避免以后有人写出 <c>^[\u4e00-\u9fa5]+$</c> 这种会误杀生僻字的校验。
/// </summary>
public class RareCharacterTextTests
{
    // U+20BB7，CJK 扩展 B 区
    private const string Ji = "\U00020BB7";
    // U+4DAE，CJK 扩展 A 区（BMP 内，但主流中文字体同样不覆盖）
    private const string Yan = "\u4DAE";
    // U+30EDD，CJK 扩展 G 区
    private const string Rare3 = "\U00030EDD";

    [Fact]
    public void 生僻字在_UTF16_里占两个_char_但按码点只算一个()
    {
        // 这是整类 bug 的根源
        Ji.Length.ShouldBe(2);

        RareCharacterText.RuneLength(Ji).ShouldBe(1);
    }

    [Theory]
    [InlineData("张三", 2)]
    [InlineData("𠮷田", 2)]        // 生僻字 + 常用字
    [InlineData("欧阳𠮷", 3)]
    [InlineData("\U00030EDD\U00020BB7\u4DAE", 3)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void 按码点统计长度(string? input, int expected)
    {
        RareCharacterText.RuneLength(input).ShouldBe(expected);
    }

    [Fact]
    public void 截断不会劈开代理对()
    {
        var name = Ji + "田张";

        // 截 2 个码点 = "𠮷田"
        var truncated = RareCharacterText.TruncateByRune(name, 2);

        truncated.ShouldBe(Ji + "田");
        // 结果必须是合法字符串：不能出现孤立代理项
        truncated.Length.ShouldBe(3); // 2 个 char 的生僻字 + 1 个 char 的常用字
        char.IsSurrogate(truncated[0]).ShouldBeTrue();
        truncated.EnumerateRunes().Count().ShouldBe(2);
    }

    [Fact]
    public void 截断超长输入时不会越界()
    {
        RareCharacterText.TruncateByRune("张三", 10).ShouldBe("张三");
        RareCharacterText.TruncateByRune("张三", 0).ShouldBeEmpty();
        RareCharacterText.TruncateByRune(null, 5).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("𠮷", true)]      // 扩展 B
    [InlineData("䶮", true)]      // 扩展 A
    [InlineData("\U00030EDD", true)] // 扩展 G
    [InlineData("张", false)]
    [InlineData("A", false)]
    public void 识别生僻字(string input, bool expected)
    {
        RareCharacterText.ContainsRareCharacter(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("张三")]
    [InlineData("𠮷田")]                 // 生僻字姓名必须通过
    [InlineData("䶮天")]
    [InlineData("阿依古丽·买买提")]        // 少数民族间隔号
    [InlineData("John Smith")]           // 外籍学生
    [InlineData("欧阳𠮷𰻝")]              // 多个生僻字
    public void 合法姓名通过校验(string name)
    {
        RareCharacterText.IsValidPersonName(name, out var error).ShouldBeTrue(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("张三123")]
    [InlineData("张三@")]
    [InlineData("<script>")]
    [InlineData(null)]
    public void 非法姓名被拒绝(string? name)
    {
        RareCharacterText.IsValidPersonName(name, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void 常用正则校验姓名会误杀生僻字_这个测试记录了这个坑()
    {
        var badRegex = new System.Text.RegularExpressions.Regex(@"^[\u4e00-\u9fa5]+$");

        // 生僻字姓名被错误地判为非法 —— 所以系统里禁止使用这种校验
        badRegex.IsMatch("𠮷田").ShouldBeFalse();

        // 本系统的校验则正确放行
        RareCharacterText.IsValidPersonName("𠮷田", out _).ShouldBeTrue();
    }
}
