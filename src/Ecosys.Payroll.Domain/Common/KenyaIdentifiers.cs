using System.Text.RegularExpressions;

namespace Ecosys.Payroll.Domain.Common;

/// <summary>
/// Format checks for Kenyan identifiers. A malformed KRA PIN is the most common reason an iTax P10 upload is rejected,
/// so catch it when the record is saved rather than at filing time. Checks are on format only; they don't confirm the
/// number is registered (KRA's PIN checker does that).
/// </summary>
public static partial class KenyaIdentifiers
{
    // KRA PIN: 11 characters. 'A' for individuals, 'P' for companies and other non-individuals, then 9 digits and a check letter.
    [GeneratedRegex("^[AP][0-9]{9}[A-Z]$")] private static partial Regex PinRx();
    // National ID card numbers are 7 or 8 digits (older cards as few as 6; Maisha numbers up to 9).
    [GeneratedRegex("^[0-9]{6,9}$")] private static partial Regex IdRx();
    // Kenyan mobile and fixed-mobile numbers after the 254 country code: 7xx xxx xxx or 1xx xxx xxx.
    [GeneratedRegex("^254[17][0-9]{8}$")] private static partial Regex KePhoneRx();
    [GeneratedRegex("^\\+[1-9][0-9]{7,14}$")] private static partial Regex E164Rx();
    [GeneratedRegex("^[A-Z0-9][A-Z0-9/-]{3,19}$")] private static partial Regex MembershipRx();

    public static string? NormalisePin(string? pin) => string.IsNullOrWhiteSpace(pin) ? null : pin.Trim().Replace(" ", "").ToUpperInvariant();

    public static bool IsValidPin(string? pin, bool individual = true) =>
        pin is not null && PinRx().IsMatch(pin) && (!individual || pin[0] == 'A');

    public static string? NormaliseNationalId(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim().Replace(" ", "");

    public static bool IsValidNationalId(string? id) => id is not null && IdRx().IsMatch(id);

    /// <summary>
    /// Returns the number in E.164 form (+2547XXXXXXXX), accepting 07…, 01…, 7…, 254… and +254… with spaces or dashes.
    /// Foreign numbers already in +country form are kept. Returns null if it isn't a phone number.
    /// </summary>
    public static string? NormalisePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var p = new string(phone.Where(c => char.IsAsciiDigit(c) || c == '+').ToArray());
        if (p.StartsWith("+254")) p = p[1..];
        else if (p.StartsWith("+")) return E164Rx().IsMatch(p) ? p : null;
        else if (p.StartsWith('0') && p.Length == 10) p = "254" + p[1..];
        else if (p.Length == 9 && (p[0] == '7' || p[0] == '1')) p = "254" + p;
        return KePhoneRx().IsMatch(p) ? "+" + p : null;
    }

    public static string? NormaliseMembership(string? n) => string.IsNullOrWhiteSpace(n) ? null : n.Trim().Replace(" ", "").ToUpperInvariant();

    /// <summary>NSSF and SHA member numbers vary in shape (NSSF also accepts the ID number); check only for obvious mistakes.</summary>
    public static bool IsPlausibleMembership(string? n) => n is not null && MembershipRx().IsMatch(n);
}
