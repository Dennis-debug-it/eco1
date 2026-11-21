using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Seed;
using Ecosys.Payroll.Infrastructure.Services;
using Ecosys.Payroll.Infrastructure.Services.Saas;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record SignupRequest(
    string OrganisationName, string? Slug, string? LegalEntityName, string KraPin, string AdminName, string AdminEmail, string Password,
    string? Phone, string? PlanCode, bool AcceptTerms, int? PayDay = null);
public sealed record VerifyEmailRequest(string Token);
public sealed record ResendVerificationRequest(string Organisation, string Email);

/// <summary>Self-service sign-up (SaaS mode). Creates the organisation, its first administrator and a free trial.</summary>
public static partial class SignupEndpoints
{
    public const string SignupRateLimit = "signup";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(48);

    public static void MapSignup(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/signup").AllowAnonymous();

        g.MapGet("/options", async (SaasOptions options, PayrollDbContext db, CancellationToken ct) =>
        {
            if (!Open(options)) return Results.Ok(new { enabled = false });
            var plans = await db.Plans.Where(p => p.IsActive && p.IsPublic).OrderBy(p => p.SortOrder).ToListAsync(ct);
            return Results.Ok(new
            {
                enabled = true, trialDays = options.TrialDays, defaultPlan = options.DefaultPlan, vatRate = options.VatRate,
                requireEmailVerification = options.RequireEmailVerification, plans = plans.Select(BillingEndpoints.PlanView),
            });
        });

        g.MapGet("/slug", async (string? value, string? name, SaasOptions options, PayrollDbContext db, CancellationToken ct) =>
        {
            if (!Open(options)) return Results.NotFound();
            var slug = string.IsNullOrWhiteSpace(value) ? Suggest(name ?? "") : value.Trim().ToLowerInvariant();
            var valid = SlugRx().IsMatch(slug) && !options.ReservedSlugs.Contains(slug);
            var taken = valid && await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Slug == slug, ct);
            return Results.Ok(new { slug, valid, available = valid && !taken });
        });

        g.MapPost("", async (SignupRequest req, SaasOptions options, PayrollDbContext db, TenantProvisioner provisioner, BillingService billing, NotificationService notify,
            IConfiguration config, IWebHostEnvironment env, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!Open(options)) return Results.NotFound();
            if (!req.AcceptTerms) throw new DomainException("Accept the terms of service and the data processing agreement to continue.", "VALIDATION");
            var slug = string.IsNullOrWhiteSpace(req.Slug) ? Suggest(req.OrganisationName ?? "") : req.Slug.Trim().ToLowerInvariant();
            if (options.ReservedSlugs.Contains(slug)) throw new DomainException($"The sign-in code '{slug}' is reserved. Choose another.", "VALIDATION");
            if (string.IsNullOrEmpty(req.Password)) throw new DomainException("Choose a password.", "VALIDATION");
            var planCode = string.IsNullOrWhiteSpace(req.PlanCode) ? options.DefaultPlan : req.PlanCode.Trim().ToLowerInvariant();
            if (!await db.Plans.AnyAsync(p => p.Code == planCode && p.IsActive && p.IsPublic, ct)) throw new DomainException($"There is no plan '{req.PlanCode}'.", "VALIDATION");

            // One trial per company: the same employer KRA PIN cannot open a second account.
            var pin = KenyaIdentifiers.NormalisePin(req.KraPin);
            if (pin is not null && await db.LegalEntities.IgnoreQueryFilters().AnyAsync(l => l.TaxPin == pin && !l.IsDeleted, ct))
                throw new DomainException("A company with this KRA PIN already has an Ecosys Payroll account. Ask its administrator to add you as a user, or contact support.", "DUPLICATE");

            var today = BusinessClock.Today();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await provisioner.ProvisionAsync(new TenantProvisionRequest(
                req.OrganisationName ?? "", slug, string.IsNullOrWhiteSpace(req.LegalEntityName) ? req.OrganisationName ?? "" : req.LegalEntityName, req.KraPin ?? "",
                req.AdminEmail ?? "", req.AdminName ?? "", Email: req.AdminEmail, Phone: req.Phone, PayDay: req.PayDay ?? 28, AdminPassword: req.Password), ct);

            db.IgnoreTenantFilter = true;
            var sub = await billing.StartTrialAsync(result.TenantId, planCode, req.OrganisationName!.Trim(), result.AdminEmail, today, ct);
            string? token = null;
            if (options.RequireEmailVerification)
            {
                var admin = await db.Users.FirstAsync(u => u.Id == result.AdminUserId, ct);
                token = IssueToken(admin);
                notify.Enqueue(NotificationChannel.Email, admin.Email, admin.DisplayName, "signup.verify", "Confirm your email for Ecosys Payroll",
                    $"Welcome to Ecosys Payroll. Confirm your email address to finish setting up {req.OrganisationName!.Trim()}: {VerifyLink(config, token)} . The link works for 48 hours. Your organisation's sign-in code is '{result.Slug}'.",
                    nameof(User), admin.Id, result.TenantId);
            }
            db.PayrollAuditLogs.Add(new PayrollAuditLog
            {
                TenantId = result.TenantId, ActorId = result.AdminUserId, ActorName = result.AdminEmail, Action = "tenant.signup", EntityType = "Subscription", EntityId = sub.Id,
                Details = $"{{\"plan\":\"{planCode}\",\"trialEndsOn\":\"{sub.TrialEndsOn:yyyy-MM-dd}\"}}",
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (token is not null && env.IsDevelopment())
                logs.CreateLogger("Signup").LogInformation("Email verification link for {Slug}: {Link}", result.Slug, VerifyLink(config, token));

            return Results.Created($"/api/payroll/signup/{result.Slug}", new { slug = result.Slug, email = result.AdminEmail, verificationRequired = token is not null, trialEndsOn = sub.TrialEndsOn, plan = planCode });
        }).RequireRateLimiting(SignupRateLimit);

        g.MapPost("/verify", async (VerifyEmailRequest req, SaasOptions options, PayrollDbContext db, CancellationToken ct) =>
        {
            if (!options.Enabled) return Results.NotFound();
            var hash = Hash(req.Token ?? "");
            db.IgnoreTenantFilter = true;
            var user = await db.Users.FirstOrDefaultAsync(u => u.EmailVerificationTokenHash == hash, ct);
            if (user is null || user.EmailVerificationExpiresAt < DateTimeOffset.UtcNow)
                return Results.Json(new { title = "Link not valid", status = 400, code = "VERIFICATION_INVALID", detail = "This confirmation link has expired or was already used. Sign in to request a new one." }, statusCode: 400);
            user.EmailVerificationTokenHash = null; user.EmailVerificationExpiresAt = null; user.EmailVerifiedAt = DateTimeOffset.UtcNow;
            db.PayrollAuditLogs.Add(new PayrollAuditLog { TenantId = user.TenantId, ActorId = user.Id, ActorName = user.Email, Action = "auth.email.verified", EntityType = nameof(User), EntityId = user.Id });
            await db.SaveChangesAsync(ct);
            var slug = await db.Tenants.Where(t => t.Id == user.TenantId).Select(t => t.Slug).FirstAsync(ct);
            return Results.Ok(new { slug, email = user.Email });
        }).RequireRateLimiting(SignupRateLimit);

        // Always 204, so it cannot be used to find out who has an account.
        g.MapPost("/resend", async (ResendVerificationRequest req, SaasOptions options, PayrollDbContext db, NotificationService notify, IConfiguration config, CancellationToken ct) =>
        {
            if (!options.Enabled) return Results.NotFound();
            db.IgnoreTenantFilter = true;
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == (req.Organisation ?? "").Trim().ToLowerInvariant(), ct);
            var user = tenant is null ? null : await db.Users.FirstOrDefaultAsync(u => u.TenantId == tenant.Id && u.Email == (req.Email ?? "").Trim().ToLowerInvariant() && u.EmailVerificationTokenHash != null, ct);
            if (user is not null)
            {
                var token = IssueToken(user);
                notify.Enqueue(NotificationChannel.Email, user.Email, user.DisplayName, "signup.verify", "Confirm your email for Ecosys Payroll",
                    $"Confirm your email address to finish setting up Ecosys Payroll: {VerifyLink(config, token)} . The link works for 48 hours.", nameof(User), user.Id, user.TenantId);
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }).RequireRateLimiting(SignupRateLimit);
    }

    private static bool Open(SaasOptions o) => o.Enabled && o.SignupEnabled;

    private static string IssueToken(User user)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        user.EmailVerificationTokenHash = Hash(token);
        user.EmailVerificationExpiresAt = DateTimeOffset.UtcNow.Add(TokenLifetime);
        return token;
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string VerifyLink(IConfiguration config, string token) => $"{(config["Payroll:PortalUrl"] ?? "https://payroll.ecosys.example").TrimEnd('/')}/verify-email?token={token}";

    /// <summary>"Acme Kenya Ltd." → "acme-kenya-ltd".</summary>
    public static string Suggest(string name)
    {
        var s = NonSlugRx().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (s.Length > 40) s = s[..40].TrimEnd('-');
        return s.Length < 3 ? (s + "-org").TrimStart('-') : s;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$")] private static partial Regex SlugRx();
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NonSlugRx();
}
