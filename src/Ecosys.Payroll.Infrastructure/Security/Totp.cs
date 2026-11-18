using System.Security.Cryptography;
using System.Text;

namespace Ecosys.Payroll.Infrastructure.Security;

/// <summary>
/// RFC 6238 time-based one-time passwords (HMAC-SHA1, 30-second step, 6 digits) — compatible with
/// Google Authenticator, Microsoft Authenticator, Authy and 1Password. Used for MFA on privileged roles.
/// </summary>
public static class Totp
{
    private const int Step = 30, Digits = 6;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public static string ProvisioningUri(string secret, string account, string issuer = "Ecosys Payroll") =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={Step}";

    public static string Code(string secret, DateTimeOffset at) => Code(Base32Decode(secret), at.ToUnixTimeSeconds() / Step);

    /// <summary>Accepts the current code and one step either side to tolerate clock drift.</summary>
    public static bool Verify(string secret, string? code, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        code = code.Trim().Replace(" ", "");
        if (code.Length != Digits || !code.All(char.IsAsciiDigit)) return false;
        var key = Base32Decode(secret);
        var counter = now.ToUnixTimeSeconds() / Step;
        for (var d = -1; d <= 1; d++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(key, counter + d)), Encoding.ASCII.GetBytes(code))) return true;
        return false;
    }

    private static string Code(byte[] key, long counter)
    {
        Span<byte> msg = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { msg[i] = (byte)(counter & 0xff); counter >>= 8; }
        var hash = HMACSHA1.HashData(key, msg);
        var offset = hash[^1] & 0x0f;
        var bin = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string s)
    {
        s = s.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "");
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Invalid base32 secret.");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xff)); bits -= 8; }
        }
        return [.. bytes];
    }
}
