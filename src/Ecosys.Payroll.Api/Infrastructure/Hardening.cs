using System.Text;

namespace Ecosys.Payroll.Api.Infrastructure;

/// <summary>Browser security headers for the API and the single-page app it serves.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    // The app loads nothing from other origins. Inline styles are allowed because React style attributes need them.
    private const string Csp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "font-src 'self' data:; connect-src 'self'; frame-src 'self' blob:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext http)
    {
        var h = http.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h.ContentSecurityPolicy = Csp;
        // Payroll responses must not sit in shared or browser caches.
        if (http.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
        return next(http);
    }
}

/// <summary>
/// Refuses to start a non-development instance with settings that are only safe on a laptop: demo data, development
/// keys, MFA switched off. Failing at startup is better than discovering it after go-live.
/// </summary>
public static class ProductionGuard
{
    private static readonly string[] Placeholders = ["change-me", "changeme", "development", "dev-only", "example", "test"];

    public static void Check(IConfiguration config, IWebHostEnvironment env)
    {
        if (env.IsDevelopment() || env.IsEnvironment("Testing")) return;
        var problems = Problems(config).ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException("Refusing to start in " + env.EnvironmentName + ":\n - " + string.Join("\n - ", problems) +
                "\nFix the configuration (see .env.example), or set ASPNETCORE_ENVIRONMENT=Development on a non-production machine.");
    }

    public static IEnumerable<string> Problems(IConfiguration config)
    {
        if (config.GetValue("Payroll:SeedDemoData", false) && !config.GetValue("Payroll:AllowDemoData", false))
            yield return "Payroll:SeedDemoData is on. The demo tenant has published passwords. Set Payroll:AllowDemoData=true only on a demo server.";

        var encKey = config["Payroll:Encryption:Key"] ?? "";
        if (encKey == "ZGV2LW9ubHkta2V5LWRvLW5vdC11c2UtaW4tcHJvZCE=") yield return "Payroll:Encryption:Key is the development key. Generate one with: openssl rand -base64 32";

        if (config["Auth:Mode"] != "Ecosys")
        {
            var signing = config["Auth:SigningKey"] ?? "";
            if (Encoding.UTF8.GetByteCount(signing) < 32) yield return "Auth:SigningKey must be at least 32 characters. Generate one with: openssl rand -base64 48";
            else if (Placeholders.Any(p => signing.Contains(p, StringComparison.OrdinalIgnoreCase))) yield return "Auth:SigningKey looks like a placeholder. Generate a random one.";
        }
        else if (!(config["Auth:Authority"] ?? "").StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            yield return "Auth:Authority must be an https:// URL when Auth:Mode=Ecosys.";

        if (!config.GetValue("Auth:RequireMfaForPrivilegedRoles", true))
            yield return "Auth:RequireMfaForPrivilegedRoles is off. Payroll approvers and administrators must use MFA.";

        var cs = config.GetConnectionString("Payroll") ?? "";
        if (cs.Contains("Password=postgres", StringComparison.OrdinalIgnoreCase) || cs.Contains("Password=change-me", StringComparison.OrdinalIgnoreCase))
            yield return "The database password is a default. Set a strong POSTGRES_PASSWORD.";
    }
}
