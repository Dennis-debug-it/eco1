using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Seed;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record InviteOperatorBody(string Email, string Name, List<string> Roles);
public sealed record OperatorRolesBody(List<string> Roles);
public sealed record OperatorReasonBody(string? Reason);

/// <summary>
/// Operator accounts: the people who run the platform. They live in the platform organisation and are managed only
/// here (tenant user administration refuses operator roles). Platform administrators only. Every change ends the
/// operator's current sessions and is audited in the platform organisation.
/// </summary>
public static class OperatorAccountEndpoints
{
    public static void MapOperatorAccounts(this RouteGroupBuilder platform)
    {
        var g = platform.MapGroup("/operators");

        g.MapGet("", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
            Results.Ok((await Operators(db, ctx, ct)).OrderBy(u => u.DisplayName).Select(View))).Need(PlatformPermissions.OperatorsManage);

        g.MapPost("", async (InviteOperatorBody b, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var email = (b.Email ?? "").Trim().ToLowerInvariant();
            if (!email.Contains('@') || email.Length > 254) throw new DomainException("Enter a valid email address.", "VALIDATION");
            if (string.IsNullOrWhiteSpace(b.Name)) throw new DomainException("Enter the operator's name.", "VALIDATION");
            var roles = ValidRoles(b.Roles);
            if (await db.Users.AnyAsync(u => u.TenantId == ctx.TenantId && u.Email == email, ct)) throw new DomainException($"{email} already has an operator account.", "DUPLICATE");
            var password = AuthEndpoints.TemporaryPassword();
            var u = new User { TenantId = ctx.RequireTenant(), Email = email, DisplayName = b.Name.Trim(), PasswordHash = PasswordHasher.Hash(password), Roles = roles, MustChangePassword = true };
            db.Users.Add(u);
            audit.Record("platform.operator.invited", nameof(User), u.Id, new { u.Email, u.Roles });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { operatorId = u.Id, signInCode = PlatformAdminProvisioner.PlatformSlug, u.Email, temporaryPassword = password });
        }).Need(PlatformPermissions.OperatorsManage);

        g.MapPut("/{id:guid}/roles", async (Guid id, OperatorRolesBody b, ITenantContext ctx, PayrollDbContext db, SessionValidator sessions, AuditService audit, CancellationToken ct) =>
        {
            var (u, all) = await TargetAsync(id, ctx, db, ct, notSelf: "You can't change your own operator roles. Ask another platform administrator.");
            var roles = ValidRoles(b.Roles);
            if (u.Roles.Contains(Roles.PlatformAdministrator) && !roles.Contains(Roles.PlatformAdministrator)) EnsureAnotherAdministrator(u, all);
            var before = u.Roles;
            u.Roles = roles;
            return await Changed(u, "platform.operator.roles_changed", new { before, after = roles }, db, sessions, audit, ct);
        }).Need(PlatformPermissions.OperatorsManage);

        g.MapPost("/{id:guid}/deactivate", async (Guid id, OperatorReasonBody b, ITenantContext ctx, PayrollDbContext db, SessionValidator sessions, AuditService audit, CancellationToken ct) =>
        {
            var (u, all) = await TargetAsync(id, ctx, db, ct, notSelf: "You can't deactivate your own operator account.");
            if (string.IsNullOrWhiteSpace(b.Reason)) throw new DomainException("Give a reason for deactivating the account.", "VALIDATION");
            if (u.Roles.Contains(Roles.PlatformAdministrator)) EnsureAnotherAdministrator(u, all);
            u.IsActive = false;
            return await Changed(u, "platform.operator.deactivated", new { reason = b.Reason.Trim() }, db, sessions, audit, ct);
        }).Need(PlatformPermissions.OperatorsManage);

        g.MapPost("/{id:guid}/reactivate", async (Guid id, ITenantContext ctx, PayrollDbContext db, SessionValidator sessions, AuditService audit, CancellationToken ct) =>
        {
            var (u, _) = await TargetAsync(id, ctx, db, ct, notSelf: "You can't reactivate your own operator account.");
            u.IsActive = true; u.FailedLoginCount = 0; u.LockedUntil = null;
            return await Changed(u, "platform.operator.reactivated", null, db, sessions, audit, ct);
        }).Need(PlatformPermissions.OperatorsManage);

        // A new one-time password; the operator chooses their own at next sign-in.
        g.MapPost("/{id:guid}/password", async (Guid id, ITenantContext ctx, PayrollDbContext db, SessionValidator sessions, AuditService audit, CancellationToken ct) =>
        {
            var (u, _) = await TargetAsync(id, ctx, db, ct, notSelf: "Change your own password from your security settings.");
            var password = AuthEndpoints.TemporaryPassword();
            u.PasswordHash = PasswordHasher.Hash(password);
            u.MustChangePassword = true; u.FailedLoginCount = 0; u.LockedUntil = null; u.PasswordChangedAt = DateTimeOffset.UtcNow;
            PasswordResetEndpoints.ClearResetToken(u);
            await Changed(u, "platform.operator.password_reset", null, db, sessions, audit, ct);
            return Results.Ok(new { temporaryPassword = password });
        }).Need(PlatformPermissions.OperatorsManage);

        // For a lost authenticator. The operator must enrol a new one at next sign-in.
        g.MapPost("/{id:guid}/mfa/reset", async (Guid id, OperatorReasonBody b, ITenantContext ctx, PayrollDbContext db, SessionValidator sessions, AuditService audit, CancellationToken ct) =>
        {
            var (u, _) = await TargetAsync(id, ctx, db, ct, notSelf: "Use your own security settings to change your authenticator.");
            if (string.IsNullOrWhiteSpace(b.Reason)) throw new DomainException("Give a reason for resetting the authenticator.", "VALIDATION");
            u.MfaEnabled = false; u.MfaSecret = null; u.LastTotpStep = null;
            return await Changed(u, "platform.operator.mfa_reset", new { reason = b.Reason.Trim() }, db, sessions, audit, ct);
        }).Need(PlatformPermissions.OperatorsManage);

        g.MapPost("/{id:guid}/sessions/revoke", async (Guid id, ITenantContext ctx, PayrollDbContext db, SessionValidator sessions, AuditService audit, CancellationToken ct) =>
        {
            var (u, _) = await TargetAsync(id, ctx, db, ct, notSelf: "Use \"sign out everywhere\" in your own security settings.");
            return await Changed(u, "platform.operator.sessions_revoked", null, db, sessions, audit, ct);
        }).Need(PlatformPermissions.OperatorsManage);
    }

    private static object View(User u) => new
    {
        u.Id, u.Email, u.DisplayName, roles = u.Roles.Where(Roles.IsPlatform).Order(), u.IsActive, u.MfaEnabled, u.LastLoginAt, u.LockedUntil,
        u.MustChangePassword, u.CreatedAt,
    };

    /// <summary>Everyone in the caller's (platform) organisation. The console's group filter has turned the tenant filter off, so scope explicitly.</summary>
    private static Task<List<User>> Operators(PayrollDbContext db, ITenantContext ctx, CancellationToken ct) =>
        db.Users.Where(u => u.TenantId == ctx.TenantId).ToListAsync(ct);

    private static async Task<(User Target, List<User> All)> TargetAsync(Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct, string notSelf)
    {
        if (id == ctx.UserId) throw new DomainException(notSelf, "VALIDATION");
        var all = await Operators(db, ctx, ct);
        return (all.FirstOrDefault(u => u.Id == id) ?? throw new NotFoundException("Operator", id), all);
    }

    private static List<string> ValidRoles(List<string>? roles)
    {
        var r = (roles ?? []).Distinct().ToList();
        if (r.Count == 0) throw new DomainException("Choose at least one operator role.", "VALIDATION");
        var bad = r.Where(x => !Roles.IsPlatform(x)).ToList();
        if (bad.Count > 0) throw new DomainException($"Not an operator role: {string.Join(", ", bad)}.", "VALIDATION");
        return r;
    }

    /// <summary>The platform must always keep one active administrator, or nobody could manage operators or prices.</summary>
    private static void EnsureAnotherAdministrator(User leaving, List<User> all)
    {
        if (!all.Any(u => u.Id != leaving.Id && u.IsActive && u.Roles.Contains(Roles.PlatformAdministrator)))
            throw new DomainException("This is the only active platform administrator. Make someone else an administrator first.", "VALIDATION");
    }

    /// <summary>Ends the operator's sessions (their token carries the old access) and audits the change.</summary>
    private static async Task<IResult> Changed(User u, string action, object? details, PayrollDbContext db, SessionValidator sessions, AuditService audit, CancellationToken ct)
    {
        u.RotateSecurityStamp();
        audit.Record(action, nameof(User), u.Id, details);
        await db.SaveChangesAsync(ct);
        sessions.Invalidate(u.Id);
        return Results.NoContent();
    }
}
