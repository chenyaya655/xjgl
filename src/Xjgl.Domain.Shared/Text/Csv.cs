using System.Text;

namespace Xjgl.Text;

/// <summary>
/// 极简 CSV 读写（RFC 4180 的子集 + 实用妥协）。
///
/// 【为什么要自己写而不引库】
///   需要的功能只有三件：按行拆、处理引号、写出去时正确转义。
///   引一个 CSV 库会带来依赖、版本与安全面的成本，而这点逻辑用 80 行就能写清楚、
///   也容易用单元测试钉死。真正复杂的场景（多编码探测、超大文件流式读）不在本期范围内。
///
/// 【两个刻意的设计】
///   1) **按表头名映射，不按列序**：Excel 里插一列、调一下顺序是家常便饭，
///      按列序解析会在用户毫不知情的情况下把"班级"读成"年级"；
///   2) **兼容 BOM**：Excel 另存为 CSV 时会带 UTF-8 BOM（U+FEFF），
///      不处理的话第一个表头会变成 "\uFEFF姓名"，于是整列都匹配不上 ——
///      而用户看到的只是"姓名列全是空"，几乎不可能联想到 BOM。
/// </summary>
public static class Csv
{
    /// <summary>UTF-8 BOM 字符（Excel 导出的 CSV 常见）。</summary>
    private const char Bom = '\uFEFF';

    /// <summary>
    /// 解析 CSV 文本为「行 × 列」。
    /// 支持：双引号包裹的字段、字段内的逗号/换行、两个双引号表示一个双引号（"" → "）。
    /// </summary>
    public static List<string[]> Parse(string? text)
    {
        var rows = new List<string[]>();

        if (string.IsNullOrEmpty(text))
        {
            return rows;
        }

        if (text[0] == Bom)
        {
            text = text[1..];
        }

        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // 连续两个双引号 = 一个字面双引号
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;

                case '\r':
                    // 归一化 CRLF：只在遇到 \n 时收行，避免多出一个空行
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        break;
                    }

                    goto case '\n';

                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    rows.Add(fields.ToArray());
                    fields.Clear();
                    break;

                default:
                    field.Append(c);
                    break;
            }
        }

        // 最后一行没有换行符结尾
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            rows.Add(fields.ToArray());
        }

        return rows;
    }

    /// <summary>
    /// 把「行 × 列」写成 CSV 文本。
    /// 约定：字段里出现逗号、双引号或换行时自动加引号并转义。
    /// </summary>
    public static string Write(IEnumerable<IEnumerable<string?>> rows)
    {
        var builder = new StringBuilder();

        foreach (var row in rows)
        {
            var first = true;

            foreach (var cell in row)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(Escape(cell));
            }

            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 给 Excel 用的导出文本：**带 UTF-8 BOM**。
    ///
    /// 不加 BOM 的话，Excel 在中文 Windows 上会按 GBK 打开 UTF-8 文件，
    /// 结果整份名单都是乱码 —— 用户会认为"系统导出的文件坏了"。
    /// 加 BOM 是让 Excel 认出 UTF-8 的最省事办法。
    /// </summary>
    public static string WriteForExcel(IEnumerable<IEnumerable<string?>> rows)
        => Bom + Write(rows);

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var needsQuote = value.Contains(',') || value.Contains('"')
                         || value.Contains('\n') || value.Contains('\r');

        return needsQuote ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    /// <summary>
    /// 把「表头 → 列索引」建索引，便于按名字取值。
    /// 表头会做 Trim；重复表头以第一个为准。
    /// </summary>
    public static Dictionary<string, int> BuildHeaderIndex(string[] header)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < header.Length; i++)
        {
            var name = header[i]?.Trim().Trim(Bom) ?? string.Empty;

            if (name.Length > 0 && !index.ContainsKey(name))
            {
                index[name] = i;
            }
        }

        return index;
    }

    /// <summary>安全取单元格（列不存在或越界时返回空串）。</summary>
    public static string Cell(string[] row, Dictionary<string, int> header, string columnName)
    {
        if (!header.TryGetValue(columnName, out var index) || index >= row.Length)
        {
            return string.Empty;
        }

        return row[index]?.Trim() ?? string.Empty;
    }
}
