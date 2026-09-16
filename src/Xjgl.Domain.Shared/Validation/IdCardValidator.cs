namespace Xjgl.Validation;

/// <summary>
/// 居民身份证号校验与规范化（GB 11643-1999）。
/// 学籍系统的身份证号是唯一性与查重的关键，必须在入口处严格校验。
/// </summary>
public static class IdCardValidator
{
    private static readonly int[] Weights = { 7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2 };
    private const string CheckCodes = "10X98765432";

    /// <summary>
    /// 校验并规范化为 18 位。15 位老号会被补齐为 18 位。
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "身份证号不能为空";
            return false;
        }

        var id = input.Trim().ToUpperInvariant();

        if (id.Length == 15)
        {
            if (!id.All(char.IsDigit))
            {
                error = "15 位身份证号只能包含数字";
                return false;
            }

            // 15 位 -> 18 位：插入 19 世纪前缀与校验位
            id = id[..6] + "19" + id[6..];
            id += ComputeCheckCode(id);
        }

        if (id.Length != 18)
        {
            error = "身份证号长度必须为 18 位（或 15 位老号）";
            return false;
        }

        for (var i = 0; i < 17; i++)
        {
            if (!char.IsDigit(id[i]))
            {
                error = "身份证号前 17 位必须为数字";
                return false;
            }
        }

        if (id[6] != '1' && id[6] != '2')
        {
            error = "身份证号的出生日期段不合法";
            return false;
        }

        if (!TryParseBirthDate(id.Substring(6, 8), out _, out error))
        {
            return false;
        }

        var expected = ComputeCheckCode(id);
        if (id[17] != expected)
        {
            error = "身份证号校验位不正确";
            return false;
        }

        normalized = id;
        return true;
    }

    /// <summary>从身份证号解析出生日期（已通过校验）。</summary>
    public static bool TryGetBirthDate(string idCardNumber, out DateOnly birthDate)
    {
        birthDate = default;
        if (!TryNormalize(idCardNumber, out var id, out _))
        {
            return false;
        }

        return TryParseBirthDate(id.Substring(6, 8), out birthDate, out _);
    }

    /// <summary>从身份证号解析性别：奇数为男，偶数为女。</summary>
    public static bool TryGetGender(string idCardNumber, out bool isMale)
    {
        isMale = false;
        if (!TryNormalize(idCardNumber, out var id, out _))
        {
            return false;
        }

        isMale = (id[16] - '0') % 2 == 1;
        return true;
    }

    /// <summary>
    /// 生成用于唯一索引与查重的哈希键。
    /// 身份证号本身以加密方式存储，查询用这个 HMAC 值。
    /// </summary>
    public static string ComputeIdCardHash(string idCardNumber, byte[] hmacKey)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(hmacKey);
        var bytes = System.Text.Encoding.UTF8.GetBytes(idCardNumber.Trim().ToUpperInvariant());
        return Convert.ToHexString(hmac.ComputeHash(bytes));
    }

    private static bool TryParseBirthDate(string yyyymmdd, out DateOnly birthDate, out string? error)
    {
        birthDate = default;
        error = null;

        if (!int.TryParse(yyyymmdd[..4], out var year) ||
            !int.TryParse(yyyymmdd.Substring(4, 2), out var month) ||
            !int.TryParse(yyyymmdd.Substring(6, 2), out var day))
        {
            error = "身份证号的出生日期段不合法";
            return false;
        }

        if (year is < 1900 or > 2200 || month is < 1 or > 12)
        {
            error = "身份证号的出生日期段不合法";
            return false;
        }

        if (day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            error = "身份证号的出生日期段不合法";
            return false;
        }

        birthDate = new DateOnly(year, month, day);
        return true;
    }

    private static char ComputeCheckCode(string id17)
    {
        var sum = 0;
        for (var i = 0; i < 17; i++)
        {
            sum += (id17[i] - '0') * Weights[i];
        }

        return CheckCodes[sum % 11];
    }
}
