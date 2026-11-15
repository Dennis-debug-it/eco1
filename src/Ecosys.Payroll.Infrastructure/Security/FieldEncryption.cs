using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ecosys.Payroll.Infrastructure.Security;

/// <summary>
/// AES-256-GCM field encryption for sensitive columns (bank account numbers, KRA PINs).
/// Ciphertext is stored as <c>enc:v1:&lt;base64 nonce|tag|cipher&gt;</c>, so plaintext legacy values
/// and future key versions can be told apart. The key comes from configuration (<c>Payroll:Encryption:Key</c>,
/// base64 of 32 bytes) and must be supplied by a secrets manager in production.
/// </summary>
public sealed class FieldEncryptor
{
    private const string Prefix = "enc:v1:";
    private readonly byte[] _key;

    public FieldEncryptor(string base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key)) throw new InvalidOperationException("Payroll:Encryption:Key is not configured.");
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32) throw new InvalidOperationException("Payroll:Encryption:Key must be 32 bytes (base64).");
    }

    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public string? Encrypt(string? plaintext)
    {
        if (plaintext is null) return null;
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal)) return plaintext;
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        var buffer = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(buffer, 0);
        tag.CopyTo(buffer, nonce.Length);
        cipher.CopyTo(buffer, nonce.Length + tag.Length);
        return Prefix + Convert.ToBase64String(buffer);
    }

    public string? Decrypt(string? stored)
    {
        if (stored is null) return null;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // legacy plaintext
        var buffer = Convert.FromBase64String(stored[Prefix.Length..]);
        var nonceLen = AesGcm.NonceByteSizes.MaxSize;
        var tagLen = AesGcm.TagByteSizes.MaxSize;
        var nonce = buffer.AsSpan(0, nonceLen);
        var tag = buffer.AsSpan(nonceLen, tagLen);
        var cipher = buffer.AsSpan(nonceLen + tagLen);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, tagLen);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public ValueConverter<string?, string?> Converter() => new(v => Encrypt(v), v => Decrypt(v));
    public ValueConverter<string, string> RequiredConverter() => new(v => Encrypt(v)!, v => Decrypt(v)!);

    /// <summary>Masks all but the last four characters, for logs, exports and screens without salary.read.</summary>
    public static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= 4 ? new string('*', value.Length) : new string('*', value.Length - 4) + value[^4..];
}

/// <summary>PBKDF2 password hashing for the built-in development identity provider.</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2:{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (stored is null) return false;
        var parts = stored.Split(':');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        var iterations = int.Parse(parts[1]);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
