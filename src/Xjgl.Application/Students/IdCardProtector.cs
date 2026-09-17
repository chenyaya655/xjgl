using System.Text;
using Microsoft.Extensions.Configuration;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Security.Encryption;
using Xjgl.Validation;

namespace Xjgl.Students;

/// <summary>
/// 身份证件的加解密 / 摘要 / 脱敏。
///
/// 抽出来的原因：学生主档、监护人（子表）都要处理身份证号，
/// 而"密文入库 + 摘要查重 + 默认只给掩码"这三条规则**必须一致** ——
/// 如果每处各写一遍，迟早会出现某个界面把明文吐出去。
/// </summary>
public interface IIdCardProtector
{
    /// <summary>明文 → 密文（AES）。空值原样返回。</summary>
    string? Encrypt(string? idCardPlain);

    /// <summary>密文 → 明文；解密失败返回 null（不抛异常，避免一个坏数据让整页 500）。</summary>
    string? Decrypt(string? idCardEncrypted);

    /// <summary>密文 → 掩码（前 6 后 4）。</summary>
    string? MaskEncrypted(string? idCardEncrypted);

    /// <summary>明文 → 掩码（前 6 后 4）。</summary>
    string MaskPlain(string idCardPlain);

    /// <summary>查重用的 HMAC-SHA256 摘要（可做唯一索引，不可逆）。</summary>
    string ComputeHash(string normalizedIdCard);
}

/// <inheritdoc />
public class IdCardProtector : IIdCardProtector, ITransientDependency
{
    private readonly IStringEncryptionService _encryption;
    private readonly IConfiguration _configuration;

    public IdCardProtector(IStringEncryptionService encryption, IConfiguration configuration)
    {
        _encryption = encryption;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public string? Encrypt(string? idCardPlain)
    {
        return idCardPlain.IsNullOrWhiteSpace() ? null : _encryption.Encrypt(idCardPlain!);
    }

    /// <inheritdoc />
    public string? Decrypt(string? idCardEncrypted)
    {
        if (idCardEncrypted.IsNullOrWhiteSpace())
        {
            return null;
        }

        try
        {
            return _encryption.Decrypt(idCardEncrypted!);
        }
        catch (Exception)
        {
            // 密钥轮换后老数据可能解不开 —— 返回 null 让界面显示「—」，
            // 而不是让整个列表接口 500
            return null;
        }
    }

    /// <inheritdoc />
    public string? MaskEncrypted(string? idCardEncrypted)
    {
        var plain = Decrypt(idCardEncrypted);

        return plain == null ? null : MaskPlain(plain);
    }

    /// <inheritdoc />
    public string MaskPlain(string idCardPlain)
    {
        return idCardPlain.Length >= 10
            ? $"{idCardPlain[..6]}********{idCardPlain[^4..]}"
            : new string('*', idCardPlain.Length);
    }

    /// <inheritdoc />
    public string ComputeHash(string normalizedIdCard)
    {
        var key = _configuration["Xjgl:Security:IdCardHmacKey"];

        if (key.IsNullOrWhiteSpace())
        {
            key = "xjgl-dev-idcard-hmac-key-please-change-in-production";
        }

        return IdCardValidator.ComputeIdCardHash(normalizedIdCard, Encoding.UTF8.GetBytes(key!));
    }
}
