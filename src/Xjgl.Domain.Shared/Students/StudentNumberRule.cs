namespace Xjgl.Students;

/// <summary>
/// 学籍辅号规则。
///
/// 默认规则：<c>{SchoolCode:10}{Seq:6}</c> —— 共 16 位。
///   - <c>SchoolCode</c>：学校标识码（教育事业统计用机构标识码），10 位，
///     其**前 6 位就是行政区划码**，因此天然满足「行政区划码 + 学校代码」的思路；
///   - <c>Seq</c>：按学校维度**永久自增**的流水号，6 位，单校容量 100 万。
///
/// 唯一性：学校标识码全国唯一 × 校内流水唯一 ⇒ 全局唯一。
///
/// 与【全国学籍号】的区别：全国学籍号（G + 身份证号，19 位）由全国中小学生学籍
/// 信息管理系统分配，本系统只存储不生成；本规则生成的是省级/校级学籍辅号。
///
/// 规则刻意做成可配置：未来换号段或换长度，只改设置项 <c>Xjgl.StudentNumberRule</c>，
/// 不需要改代码。
/// </summary>
public sealed class StudentNumberRule
{
    /// <summary>默认规则：学校标识码(10) + 流水号(6) = 16 位。</summary>
    public const string DefaultTemplate = "{SchoolCode:10}{Seq:6}";

    public const string SchoolCodeSegment = "SchoolCode";
    public const string SequenceSegment = "Seq";

    private readonly List<Segment> _segments;

    private StudentNumberRule(string template, List<Segment> segments)
    {
        Template = template;
        _segments = segments;
    }

    public string Template { get; }

    /// <summary>规则生成的总位数（含模板中的固定文字，如省份前缀）。</summary>
    public int TotalLength => _segments.Sum(s => s.IsLiteral ? s.Literal.Length : s.Width);

    public IReadOnlyList<string> SegmentNames => _segments.Select(s => s.Name).ToList();

    public static StudentNumberRule Default => Parse(DefaultTemplate);

    /// <summary>解析规则模板，失败时抛 <see cref="FormatException"/>。</summary>
    public static StudentNumberRule Parse(string template)
    {
        if (!TryParse(template, out var rule, out var error))
        {
            throw new FormatException(error);
        }

        return rule!;
    }

    /// <summary>解析规则模板，形如 <c>{SchoolCode:10}{Seq:6}</c>。</summary>
    public static bool TryParse(string? template, out StudentNumberRule? rule, out string? error)
    {
        rule = null;
        error = null;

        if (string.IsNullOrWhiteSpace(template))
        {
            error = "学籍号规则不能为空";
            return false;
        }

        var segments = new List<Segment>();
        var index = 0;
        var literal = new System.Text.StringBuilder();

        while (index < template.Length)
        {
            var ch = template[index];

            if (ch == '{')
            {
                if (literal.Length > 0)
                {
                    // 允许模板里带固定文字（如省份前缀 "41"）
                    segments.Add(new Segment(string.Empty, literal.ToString(), IsLiteral: true));
                    literal.Clear();
                }

                var end = template.IndexOf('}', index);
                if (end < 0)
                {
                    error = "学籍号规则缺少右花括号";
                    return false;
                }

                var token = template[(index + 1)..end];
                var parts = token.Split(':');
                if (parts.Length != 2)
                {
                    error = $"学籍号规则段“{token}”格式应为 {{名称:位数}}";
                    return false;
                }

                var name = parts[0].Trim();
                if (name.Length == 0)
                {
                    error = "学籍号规则段名称不能为空";
                    return false;
                }

                if (!int.TryParse(parts[1].Trim(), out var width) || width <= 0 || width > 64)
                {
                    error = $"学籍号规则段“{name}”的位数必须是 1~64 的整数";
                    return false;
                }

                if (segments.Any(s => !s.IsLiteral && s.Name == name))
                {
                    error = $"学籍号规则中存在重复段“{name}”";
                    return false;
                }

                segments.Add(new Segment(name, string.Empty, IsLiteral: false, width));
                index = end + 1;
                continue;
            }

            if (ch == '}')
            {
                error = "学籍号规则存在多余的右花括号";
                return false;
            }

            literal.Append(ch);
            index++;
        }

        if (literal.Length > 0)
        {
            segments.Add(new Segment(string.Empty, literal.ToString(), IsLiteral: true));
        }

        if (segments.Count == 0 || segments.All(s => s.IsLiteral))
        {
            error = "学籍号规则至少要包含一个动态段";
            return false;
        }

        rule = new StudentNumberRule(template, segments);
        return true;
    }

    /// <summary>
    /// 按规则拼装学籍辅号。
    /// 值不足位数时左侧补 0；超出位数视为号段耗尽，直接报错而不是静默截断
    /// （静默截断会造成学籍号重复，是本系统必须避免的事故）。
    /// </summary>
    public string Format(IReadOnlyDictionary<string, string> values)
    {
        var builder = new System.Text.StringBuilder(TotalLength);

        foreach (var segment in _segments)
        {
            if (segment.IsLiteral)
            {
                builder.Append(segment.Literal);
                continue;
            }

            if (!values.TryGetValue(segment.Name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"学籍号规则缺少段“{segment.Name}”的取值");
            }

            value = value.Trim();

            if (value.Length > segment.Width)
            {
                throw new InvalidOperationException(
                    $"学籍号规则的段“{segment.Name}”取值“{value}”超过 {segment.Width} 位，号段已耗尽或配置有误");
            }

            builder.Append(value.PadLeft(segment.Width, '0'));
        }

        return builder.ToString();
    }

    /// <summary>便捷方法：按默认规则生成（学校标识码 + 流水号）。</summary>
    public string Format(string schoolCode, long sequence)
    {
        return Format(new Dictionary<string, string>
        {
            [SchoolCodeSegment] = schoolCode,
            [SequenceSegment] = sequence.ToString()
        });
    }

    /// <summary>校验一个学籍辅号是否符合本规则的形状（长度与数字构成）。</summary>
    public bool IsWellFormed(string? studentNumber)
    {
        if (string.IsNullOrWhiteSpace(studentNumber))
        {
            return false;
        }

        if (studentNumber.Length != TotalLength)
        {
            return false;
        }

        return studentNumber.All(char.IsAsciiDigit);
    }

    private sealed record Segment(string Name, string Literal, bool IsLiteral, int Width = 0);
}
