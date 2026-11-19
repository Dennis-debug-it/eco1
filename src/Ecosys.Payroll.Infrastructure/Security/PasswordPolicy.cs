namespace Ecosys.Payroll.Infrastructure.Security;

/// <summary>
/// Password rules for the built-in identity provider, following NIST SP 800-63B: a minimum length and a block-list
/// of common or context-specific passwords, rather than composition rules that push people to predictable patterns.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    // Frequent choices in breach corpora, plus local words that show up in Kenyan workplace passwords.
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passw0rd", "p@ssword", "p@ssw0rd", "qwerty", "qwertyuiop", "asdfghjkl", "letmein", "welcome", "admin",
        "administrator", "iloveyou", "monkey", "dragon", "football", "baseball", "sunshine", "princess", "trustno1", "abc123",
        "123456", "1234567890", "0123456789", "0987654321", "changeme", "default", "secret", "master", "login", "starwars",
        "kenya", "nairobi", "mombasa", "kisumu", "nakuru", "eldoret", "safaricom", "mpesa", "jamhuri", "madaraka", "mashujaa",
        "harambee", "uhuru", "hakuna", "matata", "karibu", "payroll", "ecosys", "salary", "company", "summer", "winter",
    };

    /// <summary>Returns a reason the password is unacceptable, or null if it passes.</summary>
    public static string? Validate(string? password, string? email = null, string? displayName = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return $"Use at least {MinLength} characters.";
        if (password.Length > MaxLength) return $"Use at most {MaxLength} characters.";
        if (password.Distinct().Count() < 4) return "Use a password with more variety.";

        // Strip the usual decorations ("Password123!", "Nairobi2026#") before checking the block-list.
        var core = new string(password.Trim().TrimEnd("0123456789!@#$%^&*?._- ".ToCharArray()).ToArray());
        core = core.TrimStart("0123456789!@#$%^&*?._- ".ToCharArray());
        if (Common.Contains(password) || Common.Contains(core) || IsSequence(password))
            return "That password is too common. Choose something less predictable — a short phrase works well.";

        var lower = password.ToLowerInvariant();
        var local = email?.Split('@')[0].ToLowerInvariant();
        if (local is { Length: >= 4 } && lower.Contains(local)) return "Don't include your email address in your password.";
        foreach (var part in (displayName ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (part.Length >= 4 && lower.Contains(part)) return "Don't include your name in your password.";
        return null;
    }

    private static bool IsSequence(string s)
    {
        var up = true; var down = true;
        for (var i = 1; i < s.Length; i++) { up &= s[i] == s[i - 1] + 1; down &= s[i] == s[i - 1] - 1; }
        return up || down;
    }
}
