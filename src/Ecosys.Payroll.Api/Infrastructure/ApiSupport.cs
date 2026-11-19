using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace Ecosys.Payroll.Api.Infrastructure;

public static class ClaimNames
{
    public const string TenantId = "tenant_id";
    public const string EmployeeId = "employee_id";
    public const string LegalEntityIds = "legal_entity_ids";
    public const string Role = "role";
    public const string AuthMethod = "amr";
    public const string SecurityStamp = "sstamp";
    public const string MustChangePassword = "pwd_change";
}

/// <summary>
/// Confirms a locally issued token still matches the user's security stamp, so password changes, deactivation,
/// role changes and "sign out everywhere" take effect within seconds instead of when the token expires.
/// </summary>
public sealed class SessionValidator(IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static string Key(Guid userId) => $"sstamp:{userId}";

    public async Task<bool> IsCurrentAsync(PayrollDbContext db, Guid userId, string? stamp, CancellationToken ct)
    {
        if (!Guid.TryParse(stamp, out var claimed)) return false;
        if (!cache.TryGetValue(Key(userId), out Guid? current))
        {
            var was = db.IgnoreTenantFilter;
            db.IgnoreTenantFilter = true;
            try { current = await db.Users.Where(u => u.Id == userId && u.IsActive).Select(u => (Guid?)u.SecurityStamp).FirstOrDefaultAsync(ct); }
            finally { db.IgnoreTenantFilter = was; }
            cache.Set(Key(userId), current, Ttl);
        }
        return current == claimed;
    }

    public void Invalidate(Guid userId) => cache.Remove(Key(userId));
}

/// <summary>Builds the per-request <see cref="TenantContext"/> from the validated principal.</summary>
public sealed class TenantContextMiddleware(RequestDelegate next, IConfiguration config)
{
    public async Task InvokeAsync(HttpContext http, TenantContext ctx, SessionValidator sessions, PayrollDbContext db)
    {
        var user = http.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var roles = user.FindAll(ClaimNames.Role).Select(c => c.Value).Concat(user.FindAll(ClaimTypes.Role).Select(c => c.Value)).ToHashSet(StringComparer.Ordinal);
            ctx.UserId = Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;
            ctx.UserName = user.FindFirstValue("name") ?? user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue(JwtRegisteredClaimNames.Email);
            ctx.TenantId = Guid.TryParse(user.FindFirstValue(ClaimNames.TenantId), out var tid) ? tid : null;
            ctx.EmployeeId = Guid.TryParse(user.FindFirstValue(ClaimNames.EmployeeId), out var eid) ? eid : null;
            ctx.Roles = roles;
            ctx.Permissions = Permissions.ForRoles(roles);
            ctx.IsPlatformAdministrator = roles.Contains(Roles.PlatformAdministrator);
            ctx.LegalEntityIds = user.FindAll(ClaimNames.LegalEntityIds).Select(c => c.Value).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();
        }
        ctx.IpAddress = http.Connection.RemoteIpAddress?.ToString();

        // Locally issued tokens are checked against the user's security stamp (revocation). Tokens from the shared
        // Ecosys identity provider are governed by that provider's session lifetime instead.
        if (user.Identity?.IsAuthenticated == true && config["Auth:Mode"] != "Ecosys" && ctx.UserId is { } sessionUser
            && !await sessions.IsCurrentAsync(db, sessionUser, user.FindFirstValue(ClaimNames.SecurityStamp), http.RequestAborted))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await http.Response.WriteAsJsonAsync(new { title = "Session ended", status = 401, code = "SESSION_REVOKED",
                detail = "Your session has ended because your password or access changed, or you signed out everywhere. Sign in again." });
            return;
        }

        // Administrator-issued passwords must be replaced before the user reaches payroll data.
        if (user.Identity?.IsAuthenticated == true && user.FindFirstValue(ClaimNames.MustChangePassword) == "1"
            && !http.Request.Path.StartsWithSegments("/api/payroll/auth"))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { title = "Password change required", status = 403, code = "PASSWORD_CHANGE_REQUIRED",
                detail = "Choose a new password before continuing." });
            return;
        }

        // PRD §13.5: privileged roles must complete MFA. A password-only token for such a user may only
        // reach the auth endpoints (to enrol or verify), never payroll data.
        if (user.Identity?.IsAuthenticated == true && config.GetValue("Auth:RequireMfaForPrivilegedRoles", true)
            && ctx.Roles.Overlaps(Roles.Privileged) && user.FindFirstValue(ClaimNames.AuthMethod) != "mfa"
            && !http.Request.Path.StartsWithSegments("/api/payroll/auth"))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { title = "Multi-factor authentication required", status = 403, code = "MFA_REQUIRED",
                detail = "Your role requires multi-factor authentication. Enrol an authenticator app, then sign in again with a code." });
            return;
        }
        await next(http);
    }
}

/// <summary>Issues development tokens (Local auth mode). In production the shared Ecosys identity provider issues tokens with the same claims.</summary>
public sealed class TokenService(IConfiguration config)
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(User user, Domain.Organisation.Tenant tenant, bool mfa)
    {
        var auth = config.GetSection("Auth");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth["SigningKey"]!));
        var expires = DateTimeOffset.UtcNow.AddHours(auth.GetValue("TokenHours", 8));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.DisplayName),
            new(ClaimNames.TenantId, tenant.Id.ToString()),
            new("tenant_slug", tenant.Slug),
            new(ClaimNames.AuthMethod, mfa ? "mfa" : "pwd"),
            new(ClaimNames.SecurityStamp, user.SecurityStamp.ToString()),
        };
        if (user.MustChangePassword) claims.Add(new Claim(ClaimNames.MustChangePassword, "1"));
        if (user.EmployeeId is { } eid) claims.Add(new Claim(ClaimNames.EmployeeId, eid.ToString()));
        if (user.LegalEntityIds.Count > 0) claims.Add(new Claim(ClaimNames.LegalEntityIds, string.Join(',', user.LegalEntityIds)));
        claims.AddRange(user.Roles.Select(r => new Claim(ClaimNames.Role, r)));

        var jwt = new JwtSecurityToken(auth["Issuer"] ?? "ecosys-payroll-dev", auth["Audience"] ?? "ecosys-payroll", claims, expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}

/// <summary>Maps domain exceptions to RFC 7807 problem responses without leaking internals.</summary>
public sealed class DomainExceptionHandler(ILogger<DomainExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        var (status, title, code) = ex switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Not found", "NOT_FOUND"),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden", "FORBIDDEN"),
            InvalidStateException => (StatusCodes.Status409Conflict, "Invalid state", "INVALID_STATE"),
            DomainException d => (StatusCodes.Status400BadRequest, "Validation failed", d.Code),
            Ecosys.Payroll.Engine.Rules.StatutoryConfigurationException => (StatusCodes.Status422UnprocessableEntity, "Statutory configuration error", "STATUTORY_CONFIG"),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Bad request", "BAD_REQUEST"),
            _ => (0, "", ""),
        };
        if (status == 0) { log.LogError(ex, "Unhandled exception"); return false; }
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new { type = $"https://ecosys.example/problems/{code.ToLowerInvariant()}", title, status, detail = ex.Message, code }, ct);
        return true;
    }
}

public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int Offset, int Limit);

public static class Paging
{
    public static (int Offset, int Limit) Normalise(int? offset, int? limit) => (Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 50, 1, 500));
}
