using Shouldly;
using Xunit;

namespace Xjgl.Students;

/// <summary>
/// 学籍辅号规则与生成器的单元测试。
/// 默认规则：学校标识码(10) + 流水号(6) = 16 位，全局唯一。
/// </summary>
public class StudentNumberRuleTests
{
    [Fact]
    public void 默认规则是_16_位()
    {
        StudentNumberRule.Default.TotalLength.ShouldBe(16);

        StudentNumberRule.Default.SegmentNames.ShouldBe(
            new[] { StudentNumberRule.SchoolCodeSegment, StudentNumberRule.SequenceSegment });
    }

    [Fact]
    public void 按默认规则生成学籍辅号()
    {
        var number = StudentNumberRule.Default.Format("4141010001", 123);

        number.ShouldBe("4141010001000123");
        number.Length.ShouldBe(16);
    }

    [Fact]
    public void 流水号不足位时左侧补零()
    {
        StudentNumberRule.Default.Format("4141010001", 1).ShouldBe("4141010001000001");
        StudentNumberRule.Default.Format("4141010001", 999999).ShouldBe("4141010001999999");
    }

    [Fact]
    public void 流水号超出位数时报错而不是静默截断()
    {
        // 静默截断会导致学籍号重复，属于严重事故，必须直接抛错
        var ex = Should.Throw<InvalidOperationException>(
            () => StudentNumberRule.Default.Format("4141010001", 1_000_000));

        ex.Message.ShouldContain("号段已耗尽");
    }

    [Fact]
    public void 学校标识码超长时报错()
    {
        Should.Throw<InvalidOperationException>(
            () => StudentNumberRule.Default.Format("41410100011", 1));
    }

    [Fact]
    public void 缺少段取值时报错()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => StudentNumberRule.Default.Format(new Dictionary<string, string>
            {
                [StudentNumberRule.SchoolCodeSegment] = "4141010001"
            }));

        ex.Message.ShouldContain(StudentNumberRule.SequenceSegment);
    }

    [Theory]
    [InlineData("{SchoolCode:10}{Seq:6}", 16)]
    [InlineData("41{SchoolCode:10}{Seq:4}", 16)]   // 带固定前缀：2 + 10 + 4
    [InlineData("{County:6}{School:4}{Seq:6}", 16)]
    [InlineData("{Seq:8}", 8)]
    public void 支持自定义规则模板(string template, int expectedLength)
    {
        var rule = StudentNumberRule.Parse(template);

        rule.TotalLength.ShouldBe(expectedLength);
    }

    [Fact]
    public void 总位数包含模板中的固定文字()
    {
        // 固定文字也是学籍号的一部分，必须计入长度，否则 IsWellFormed 会误判
        var rule = StudentNumberRule.Parse("41{SchoolCode:10}{Seq:4}");

        rule.TotalLength.ShouldBe(16);
        rule.IsWellFormed("4141010000010007").ShouldBeTrue();
        rule.IsWellFormed("41010000010007").ShouldBeFalse(); // 少了固定前缀的 2 位
    }

    [Fact]
    public void 带固定前缀的模板能正确拼装()
    {
        var rule = StudentNumberRule.Parse("41{SchoolCode:10}{Seq:4}");

        rule.Format(new Dictionary<string, string>
        {
            ["SchoolCode"] = "4101000001",
            ["Seq"] = "7"
        }).ShouldBe("414101000001000" + "7");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{SchoolCode}")]          // 缺少位数
    [InlineData("{SchoolCode:0}")]        // 位数非法
    [InlineData("{SchoolCode:10")]        // 缺右括号
    [InlineData("SchoolCode:10}")]        // 多余右括号
    [InlineData("{SchoolCode:10}{SchoolCode:6}")] // 重复段
    [InlineData("固定文字")]               // 只有固定文字，没有动态段
    public void 非法模板被拒绝(string template)
    {
        StudentNumberRule.TryParse(template, out var rule, out var error).ShouldBeFalse();

        rule.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void 形状校验()
    {
        var rule = StudentNumberRule.Default;

        rule.IsWellFormed("4141010001000123").ShouldBeTrue();
        rule.IsWellFormed("414101000100012").ShouldBeFalse();   // 15 位
        rule.IsWellFormed("41410100010001AB").ShouldBeFalse();  // 含字母
        rule.IsWellFormed(null).ShouldBeFalse();
    }

    [Fact]
    public void 同校同流水生成的号是确定性的且不重复()
    {
        var rule = StudentNumberRule.Default;

        var numbers = Enumerable.Range(1, 1000)
            .Select(i => rule.Format("4141010001", i))
            .ToList();

        numbers.Distinct().Count().ShouldBe(1000);
        numbers.ShouldAllBe(n => n.Length == 16);
        numbers.ShouldAllBe(n => n.StartsWith("4141010001"));
    }

    [Fact]
    public void 不同学校的流水号互不影响()
    {
        var rule = StudentNumberRule.Default;

        // 两所学校各自的 1 号学生，学籍辅号不同（因为学校标识码不同）
        var a = rule.Format("4141010001", 1);
        var b = rule.Format("4141010002", 1);

        a.ShouldNotBe(b);
    }
}
