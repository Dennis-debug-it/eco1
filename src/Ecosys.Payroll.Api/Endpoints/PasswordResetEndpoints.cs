using System.Security.Cryptography;
using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record ForgotPasswordRequest(string? Organisation, string Email);
public sealed record CompletePasswordResetRequest(string Token, string NewPassword);

/// <summary>
/// "Forgot password": an emailed, single-use link that lets the user choose a new password. Local sign-in only; with the
/// shared Ecosys identity provider, passwords are reset there. MFA is untouched, so a mailbox alone does not open an
/// account that has an authenticator app.
/// </summary>
public static class PasswordResetEndpoints
{
    public const string RateLimit = "password-reset";
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);
    /// <summary>A second request inside this window sends nothing, so the endpoint cannot be used to flood a mailbox.</summary>
    private static readonly TimeSpan ResendInterval = TimeSpan.FromMinutes(1);
    /// <summary>Without an organisation code, at most this many accounts sharing the email address get a link.</summary>
    private const int MaxAccountsPerRequest = 10;

    public static void MapPasswordReset(this RouteGroupBuilder auth)
    {
        // Always 204, so it cannot be used to find out who has an account. Without an organisation code, every account
        // with that email gets its own link naming its organisation: people forget the code as often as the password.
        auth.MapPost("/password/forgot", async (ForgotPasswordRequest req, PayrollDbContext db, NotificationService notify, IConfiguration config,
            IWebHostEnvironment env, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (config["Auth:Mode"] == "Ecosys") return Results.NotFound();
            var email = (req.Email ?? "").Trim().ToLowerInvariant();
            var slug = (req.Organisation ?? "").Trim().ToLowerInvariant();
            if (!email.Contains('@')) return Results.NoContent();
            db.IgnoreTenantFilter = true;
            var now = DateTimeOffset.UtcNow;
            var accounts = await db.Users
                .Where(u => u.Email == email && u.IsActive && u.PasswordHash != null)
                .Join(db.Tenants.Where(t => t.IsActive && (slug == "" || t.Slug == slug)), u => u.TenantId, t => t.Id, (u, t) => new { u, t })
                .OrderBy(x => x.t.Name).Take(MaxAccountsPerRequest).ToListAsync(ct);
            foreach (var (user, tenant) in accounts.Select(x => (x.u, x.t)))
            {
                if (user.PasswordResetExpiresAt is { } exp && exp - TokenLifetime > now - ResendInterval) continue;
                var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                user.PasswordResetTokenHash = SignupEndpoints.Hash(token);
                user.PasswordResetExpiresAt = now.Add(TokenLifetime);
                var link = Link(config, token);
                notify.Enqueue(NotificationChannel.Email, user.Email, user.DisplayName, "auth.password.reset.link", "Reset your Ecosys Payroll password",
                    $"We received a request to reset the password for {user.Email} in {tenant.Name} (organisation code '{tenant.Slug}'). Choose a new password here: {link} . " +
                    "The link works for one hour and only once. If you didn't ask for this, ignore this email: your password has not changed.",
                    nameof(User), user.Id, tenant.Id);
                db.PayrollAuditLogs.Add(new PayrollAuditLog { TenantId = tenant.Id, ActorId = user.Id, ActorName = user.Email, Action = "auth.password.reset.requested", EntityType = nameof(User), EntityId = user.Id });
                if (env.IsDevelopment()) logs.CreateLogger("PasswordReset").LogInformation("Password reset link for {Email} in {Slug}: {Link}", user.Email, tenant.Slug, link);
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting(RateLimit);

        auth.MapPost("/password/reset", async (CompletePasswordResetRequest req, PayrollDbContext db, NotificationService notify, SessionValidator sessions,
            IConfiguration config, CancellationToken ct) =>
        {
            if (config["Auth:Mode"] == "Ecosys") return Results.NotFound();
            db.IgnoreTenantFilter = true;
            var now = DateTimeOffset.UtcNow;
            var hash = SignupEndpoints.Hash(req.Token ?? "");
            var user = string.IsNullOrEmpty(req.Token) ? null : await db.Users.FirstOrDefaultAsync(u => u.PasswordResetTokenHash == hash, ct);
            var tenant = user is null ? null : await db.Tenants.FirstOrDefaultAsync(t => t.Id == user.TenantId, ct);
            if (user is null || tenant is null || !user.IsActive || !tenant.IsActive || user.PasswordResetExpiresAt is not { } exp || exp < now)
                return Results.Json(new { title = "Link not valid", status = 400, code = "RESET_LINK_INVALID", detail = "This reset link has expired or was already used. Request a new one from the sign-in page." }, statusCode: 400);
            if (PasswordPolicy.Validate(req.NewPassword, user.Email, user.DisplayName) is { } why) return Results.Problem(why, statusCode: 400, title: "Password not accepted");
            if (user.PasswordHash is not null && PasswordHasher.Verify(req.NewPassword, user.PasswordHash))
                return Results.Problem("Choose a password you haven't just used.", statusCode: 400, title: "Password not accepted");

            user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
            user.PasswordResetTokenHash = null; user.PasswordResetExpiresAt = null;
            user.PasswordChangedAt = now; user.MustChangePassword = false;
            user.FailedLoginCount = 0; user.LockedUntil = null;
            // The link reached the mailbox, which is what confirming the address proves.
            if (user.EmailVerificationTokenHash is not null) { user.EmailVerificationTokenHash = null; user.EmailVerificationExpiresAt = null; user.EmailVerifiedAt = now; }
            user.RotateSecurityStamp(); // signs out every session
            db.PayrollAuditLogs.Add(new PayrollAuditLog { TenantId = tenant.Id, ActorId = user.Id, ActorName = user.Email, Action = "auth.password.reset.completed", EntityType = nameof(User), EntityId = user.Id });
            notify.Enqueue(NotificationChannel.Email, user.Email, user.DisplayName, "auth.password.changed", "Your Ecosys Payroll password was changed",
                $"The password for {user.Email} in {tenant.Name} was changed at {BusinessClock.ToLocal(now):HH:mm} {BusinessClock.Abbreviation} using a reset link, and every session was signed out. If this wasn't you, tell your administrator now.",
                nameof(User), user.Id, tenant.Id);
            await db.SaveChangesAsync(ct);
            sessions.Invalidate(user.Id);
            return Results.Ok(new { slug = tenant.Slug, email = user.Email, mfaEnabled = user.MfaEnabled });
        }).AllowAnonymous().RequireRateLimiting(RateLimit);
    }

    /// <summary>Any other way of setting the password makes an outstanding reset link useless.</summary>
    public static void ClearResetToken(User u) { u.PasswordResetTokenHash = null; u.PasswordResetExpiresAt = null; }

    private static string Link(IConfiguration config, string token) =>
        $"{(config["Payroll:PortalUrl"] ?? "https://payroll.ecosys.example").TrimEnd('/')}/reset-password?token={token}";
}
