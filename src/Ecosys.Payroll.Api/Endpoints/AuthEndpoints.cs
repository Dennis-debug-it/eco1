using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record LoginRequest(string Tenant, string Email, string Password, string? Code = null);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, MeResponse User);
public sealed record MeResponse(Guid Id, string Email, string DisplayName, Guid TenantId, string TenantName, Guid? EmployeeId, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions, IReadOnlyList<Guid> LegalEntityIds, bool MfaEnabled = false, bool MfaRequired = false, bool MfaSatisfied = false);
public sealed record CreateUserRequest(string Email, string DisplayName, string Password, List<string> Roles, Guid? EmployeeId, List<Guid>? LegalEntityIds, string? Phone = null);
public sealed record MfaCode(string Code);
public sealed record MfaSetupResponse(string Secret, string ProvisioningUri);

public static class AuthEndpoints
{
    public static void MapAuth(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        // Local development identity. In Auth:Mode=Ecosys this endpoint is disabled and tokens come from the platform IdP.
        g.MapPost("/login", async (LoginRequest req, PayrollDbContext db, TokenService tokens, IConfiguration config, CancellationToken ct) =>
        {
            if (config["Auth:Mode"] == "Ecosys") return Results.Problem("Local login is disabled; sign in through Ecosys identity.", statusCode: 400);
            db.IgnoreTenantFilter = true;
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == req.Tenant.ToLowerInvariant() && t.IsActive, ct);
            var user = tenant is null ? null : await db.Users.FirstOrDefaultAsync(u => u.TenantId == tenant.Id && u.Email == req.Email.ToLowerInvariant() && u.IsActive, ct);
            if (tenant is null || user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
            {
                if (tenant is not null) db.PayrollAuditLogs.Add(new Domain.Payroll.PayrollAuditLog { TenantId = tenant.Id, ActorId = user?.Id, ActorName = req.Email, Action = "auth.login.failed", EntityType = "User", EntityId = user?.Id });
                await db.SaveChangesAsync(ct);
                return Results.Problem("Invalid tenant, email or password.", statusCode: 401, title: "Unauthorised");
            }
            if (user.MfaEnabled)
            {
                if (string.IsNullOrWhiteSpace(req.Code))
                    return Results.Json(new { title = "Authentication code required", status = 401, code = "MFA_CODE_REQUIRED", detail = "Enter the 6-digit code from your authenticator app." }, statusCode: 401);
                if (!Totp.Verify(user.MfaSecret!, req.Code, DateTimeOffset.UtcNow))
                {
                    db.PayrollAuditLogs.Add(new Domain.Payroll.PayrollAuditLog { TenantId = tenant.Id, ActorId = user.Id, ActorName = user.DisplayName, Action = "auth.mfa.failed", EntityType = "User", EntityId = user.Id });
                    await db.SaveChangesAsync(ct);
                    return Results.Json(new { title = "Invalid authentication code", status = 401, code = "MFA_CODE_INVALID", detail = "The authentication code is incorrect or has expired." }, statusCode: 401);
                }
            }

            user.LastLoginAt = DateTimeOffset.UtcNow;
            var (token, expires) = tokens.Issue(user, tenant, mfa: user.MfaEnabled);
            db.PayrollAuditLogs.Add(new Domain.Payroll.PayrollAuditLog { TenantId = tenant.Id, ActorId = user.Id, ActorName = user.DisplayName, Action = "auth.login", EntityType = "User", EntityId = user.Id, Details = user.MfaEnabled ? "{\"mfa\":true}" : null });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new LoginResponse(token, expires, Me(user, tenant, config, user.MfaEnabled)));
        }).AllowAnonymous();

        g.MapGet("/me", async (HttpContext http, ITenantContext ctx, PayrollDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == ctx.UserId, ct);
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == ctx.TenantId, ct);
            if (user is null || tenant is null) return Results.Unauthorized();
            return Results.Ok(Me(user, tenant, config, http.User.FindFirst(ClaimNames.AuthMethod)?.Value == "mfa"));
        }).RequireAuthorization();

        // ---- MFA (TOTP) enrolment: setup → enable with a valid code (returns an MFA-satisfied token) ----
        g.MapPost("/mfa/setup", async (ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == ctx.UserId, ct) ?? throw new ForbiddenException("Unknown user.");
            if (user.MfaEnabled) return Results.Problem("MFA is already enabled. Disable it first to re-enrol.", statusCode: 409);
            user.MfaSecret = Totp.NewSecret();
            audit.Record("auth.mfa.setup", nameof(User), user.Id);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new MfaSetupResponse(user.MfaSecret, Totp.ProvisioningUri(user.MfaSecret, user.Email)));
        }).RequireAuthorization();

        g.MapPost("/mfa/enable", async (MfaCode body, ITenantContext ctx, PayrollDbContext db, TokenService tokens, AuditService audit, IConfiguration config, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == ctx.UserId, ct) ?? throw new ForbiddenException("Unknown user.");
            if (user.MfaSecret is null) return Results.Problem("Call /auth/mfa/setup first.", statusCode: 409);
            if (!Totp.Verify(user.MfaSecret, body.Code, DateTimeOffset.UtcNow)) return Results.Problem("The authentication code is incorrect.", statusCode: 400, title: "Invalid code");
            user.MfaEnabled = true;
            audit.Record("auth.mfa.enabled", nameof(User), user.Id);
            await db.SaveChangesAsync(ct);
            var tenant = await db.Tenants.FirstAsync(t => t.Id == user.TenantId, ct);
            var (token, expires) = tokens.Issue(user, tenant, mfa: true);
            return Results.Ok(new LoginResponse(token, expires, Me(user, tenant, config, true)));
        }).RequireAuthorization();

        g.MapPost("/mfa/disable", async (MfaCode body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == ctx.UserId, ct) ?? throw new ForbiddenException("Unknown user.");
            if (!user.MfaEnabled || !Totp.Verify(user.MfaSecret!, body.Code, DateTimeOffset.UtcNow)) return Results.Problem("The authentication code is incorrect.", statusCode: 400);
            user.MfaEnabled = false; user.MfaSecret = null;
            audit.Record("auth.mfa.disabled", nameof(User), user.Id);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapPost("/users/{id:guid}/mfa/reset", async (Guid id, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            if (id == ctx.UserId) return Results.Problem("Use your own MFA settings to change your enrolment.", statusCode: 400);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException(nameof(User), id);
            user.MfaEnabled = false; user.MfaSecret = null;
            audit.Record("auth.mfa.reset", nameof(User), id, new { by = ctx.UserName });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapGet("/users", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            return Results.Ok(await db.Users.OrderBy(u => u.Email).Select(u => new { u.Id, u.Email, u.DisplayName, u.Roles, u.EmployeeId, u.LegalEntityIds, u.IsActive, u.LastLoginAt, u.MfaEnabled, u.Phone }).ToListAsync(ct));
        }).RequireAuthorization();

        g.MapPost("/users", async (CreateUserRequest req, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var bad = req.Roles.Except(Roles.All).ToList();
            if (bad.Count > 0) return Results.Problem($"Unknown roles: {string.Join(", ", bad)}", statusCode: 400);
            if (req.Roles.Contains(Roles.PlatformAdministrator) && !ctx.IsPlatformAdministrator) return Results.Problem("Cannot grant platform administrator.", statusCode: 403);
            if (req.Password.Length < 10) return Results.Problem("Password must be at least 10 characters.", statusCode: 400);
            if (await db.Users.AnyAsync(x => x.Email == req.Email.ToLowerInvariant(), ct)) return Results.Problem("A user with that email already exists.", statusCode: 409);
            var u = new User { Email = req.Email.ToLowerInvariant(), DisplayName = req.DisplayName, PasswordHash = PasswordHasher.Hash(req.Password), Roles = req.Roles, EmployeeId = req.EmployeeId, LegalEntityIds = req.LegalEntityIds ?? [], Phone = req.Phone };
            db.Users.Add(u);
            audit.Record("user.created", nameof(User), u.Id, new { u.Email, u.Roles });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/auth/users/{u.Id}", new { u.Id, u.Email, u.DisplayName, u.Roles, u.EmployeeId });
        }).RequireAuthorization();

        g.MapGet("/roles", () => Results.Ok(Roles.All.Select(r => new { role = r, permissions = Permissions.ForRoles([r]).Order() }))).RequireAuthorization();
    }

    private static MeResponse Me(User u, Domain.Organisation.Tenant t, IConfiguration config, bool mfaSatisfied) =>
        new(u.Id, u.Email, u.DisplayName, t.Id, t.Name, u.EmployeeId, u.Roles, Permissions.ForRoles(u.Roles).Order().ToList(), u.LegalEntityIds,
            u.MfaEnabled, config.GetValue("Auth:RequireMfaForPrivilegedRoles", true) && u.Roles.Intersect(Roles.Privileged).Any(), mfaSatisfied);
}
