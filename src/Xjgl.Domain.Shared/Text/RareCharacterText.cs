using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Xjgl.Text;

/// <summary>
/// 生僻字安全的字符串处理。
///
/// 背景：汉字「𠮷」「𰻝」「䶮」等位于 CJK 扩展 A/B/C/D/E 区，
/// 在 UTF-16 里是**代理对**（占 2 个 char），因此：
///   - <c>string.Length</c> 会把 1 个生僻字算成 2，长度校验与截断全部失真；
///   - <c>Substring</c> 按 char 切分可能把代理对劈开，产生非法字符。
///
/// 本类统一按 **Unicode 码点（Rune）** 计数与截断，全系统禁止直接用
/// string.Length / Substring 处理姓名、地址等可能含生僻字的字段。
/// </summary>
public static class RareCharacterText
{
    /// <summary>CJK 统一表意文字（基本区）。</summary>
    public const int CjkBasicStart = 0x4E00;
    public const int CjkBasicEnd = 0x9FFF;

    /// <summary>CJK 扩展 A 区（BMP 内，但多数中文字体不覆盖）。</summary>
    public const int CjkExtAStart = 0x3400;
    public const int CjkExtAEnd = 0x4DBF;

    /// <summary>CJK 扩展 B ~ I 区（补充平面）。</summary>
    public const int CjkExtBStart = 0x20000;
    public const int CjkExtIEnd = 0x3FFFF;

    /// <summary>按 Unicode 码点计算长度。</summary>
    public static int RuneLength(string? value)
    {
        return string.IsNullOrEmpty(value) ? 0 : value.EnumerateRunes().Count();
    }

    /// <summary>按码点截断，绝不会劈开代理对。</summary>
    public static string TruncateByRune(string? value, int maxRunes)
    {
        if (string.IsNullOrEmpty(value) || maxRunes <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count >= maxRunes)
            {
                break;
            }

            builder.Append(rune);
            count++;
        }

        return builder.ToString();
    }

    /// <summary>是否是需要额外字体支持的「生僻字」。</summary>
    public static bool IsRareCharacter(Rune rune)
    {
        var v = rune.Value;

        // 补充平面上的 CJK 扩展 B 及以后
        if (v >= CjkExtBStart && v <= CjkExtIEnd)
        {
            return true;
        }

        // BMP 内的 CJK 扩展 A 区，主流中文字体同样不覆盖
        return v >= CjkExtAStart && v <= CjkExtAEnd;
    }

    /// <summary>字符串中是否含有生僻字（前端据此启用备用字体）。</summary>
    public static bool ContainsRareCharacter(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            if (IsRareCharacter(rune))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>是否 CJK 表意文字（含扩展区）。</summary>
    public static bool IsCjkIdeograph(Rune rune)
    {
        var v = rune.Value;
        return (v >= CjkBasicStart && v <= CjkBasicEnd)
               || (v >= CjkExtAStart && v <= CjkExtAEnd)
               || (v >= CjkExtBStart && v <= CjkExtIEnd);
    }

    /// <summary>
    /// 校验姓名是否合法。
    ///
    /// 允许：CJK 表意文字（含全部扩展区）、拉丁字母、少数民族姓名间隔号（·／•）、空格。
    /// 禁止：数字、控制字符、标点（除间隔号）、emoji 等。
    ///
    /// 注意：**不能**用 <c>^[\u4e00-\u9fa5]+$</c> 这类正则校验姓名 ——
    /// 它会把生僻字全部误判为非法。
    ///
    /// <paramref name="error"/> 上的 <see cref="NotNullWhenAttribute"/> 不是装饰：
    /// 调用方几乎都是"校验失败就把这句原因塞进 BusinessException"，例如
    /// <c>throw new BusinessException(...).WithData("reason", nameError)</c>。
    /// 没有这个标注时，编译器只能看到 <c>out string?</c>，
    /// 于是**每一处调用点**都要各写一次空值兜底（本项目原有 12 处 CS8604 警告就来自这里）——
    /// 而正确的做法是**在一处把契约说清楚**：返回 false 时 error 必然有值。
    /// </summary>
    public static bool IsValidPersonName(
        string? value,
        [NotNullWhen(false)] out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "姓名不能为空";
            return false;
        }

        var length = RuneLength(value);
        if (length > 60)
        {
            error = "姓名长度超出限制";
            return false;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            if (IsCjkIdeograph(rune))
            {
                continue;
            }

            // 少数民族姓名的间隔号
            if (rune.Value is 0x00B7 or 0x2022 or 0x30FB)
            {
                continue;
            }

            if (rune.Value == ' ')
            {
                continue;
            }

            // 拉丁字母（外籍学生、拼音名）
            if (Rune.IsLetter(rune) && rune.Value < 0x0250)
            {
                continue;
            }

            error = $"姓名包含非法字符“{rune}”";
            return false;
        }

        return true;
    }
}
