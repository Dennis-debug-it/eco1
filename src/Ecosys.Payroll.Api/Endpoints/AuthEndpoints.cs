using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record LoginRequest(string Tenant, string Email, string Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, MeResponse User);
public sealed record MeResponse(Guid Id, string Email, string DisplayName, Guid TenantId, string TenantName, Guid? EmployeeId, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions, IReadOnlyList<Guid> LegalEntityIds);
public sealed record CreateUserRequest(string Email, string DisplayName, string Password, List<string> Roles, Guid? EmployeeId, List<Guid>? LegalEntityIds);

public static class AuthEndpoints
{
    public static void MapAuth(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        // Local development identity. In Auth:Mode=Ecosys this endpoint is disabled and tokens come from the platform IdP.
        g.MapPost("/login", async (LoginRequest req, PayrollDbContext db, TokenService tokens, AuditService audit, IConfiguration config, CancellationToken ct) =>
        {
            if (config["Auth:Mode"] == "Ecosys") return Results.Problem("Local login is disabled; sign in through Ecosys identity.", statusCode: 400);
            db.IgnoreTenantFilter = true;
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == req.Tenant.ToLowerInvariant() && t.IsActive, ct);
            var user = tenant is null ? null : await db.Users.FirstOrDefaultAsync(u => u.TenantId == tenant.Id && u.Email == req.Email.ToLowerInvariant() && u.IsActive, ct);
            if (tenant is null || user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
                return Results.Problem("Invalid tenant, email or password.", statusCode: 401, title: "Unauthorised");

            user.LastLoginAt = DateTimeOffset.UtcNow;
            var (token, expires) = tokens.Issue(user, tenant);
            db.PayrollAuditLogs.Add(new Domain.Payroll.PayrollAuditLog { TenantId = tenant.Id, ActorId = user.Id, ActorName = user.DisplayName, Action = "auth.login", EntityType = "User", EntityId = user.Id });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new LoginResponse(token, expires, Me(user, tenant)));
        }).AllowAnonymous();

        g.MapGet("/me", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == ctx.UserId, ct);
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == ctx.TenantId, ct);
            if (user is null || tenant is null) return Results.Unauthorized();
            return Results.Ok(Me(user, tenant));
        }).RequireAuthorization();

        g.MapGet("/users", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            return Results.Ok(await db.Users.OrderBy(u => u.Email).Select(u => new { u.Id, u.Email, u.DisplayName, u.Roles, u.EmployeeId, u.LegalEntityIds, u.IsActive, u.LastLoginAt }).ToListAsync(ct));
        }).RequireAuthorization();

        g.MapPost("/users", async (CreateUserRequest req, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var bad = req.Roles.Except(Roles.All).ToList();
            if (bad.Count > 0) return Results.Problem($"Unknown roles: {string.Join(", ", bad)}", statusCode: 400);
            if (req.Roles.Contains(Roles.PlatformAdministrator) && !ctx.IsPlatformAdministrator) return Results.Problem("Cannot grant platform administrator.", statusCode: 403);
            if (req.Password.Length < 10) return Results.Problem("Password must be at least 10 characters.", statusCode: 400);
            var u = new User { Email = req.Email.ToLowerInvariant(), DisplayName = req.DisplayName, PasswordHash = PasswordHasher.Hash(req.Password), Roles = req.Roles, EmployeeId = req.EmployeeId, LegalEntityIds = req.LegalEntityIds ?? [] };
            db.Users.Add(u);
            audit.Record("user.created", nameof(User), u.Id, new { u.Email, u.Roles });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/auth/users/{u.Id}", new { u.Id, u.Email, u.DisplayName, u.Roles, u.EmployeeId });
        }).RequireAuthorization();

        g.MapGet("/roles", () => Results.Ok(Roles.All.Select(r => new { role = r, permissions = Permissions.ForRoles([r]).Order() }))).RequireAuthorization();
    }

    private static MeResponse Me(User u, Domain.Organisation.Tenant t) =>
        new(u.Id, u.Email, u.DisplayName, t.Id, t.Name, u.EmployeeId, u.Roles, Permissions.ForRoles(u.Roles).Order().ToList(), u.LegalEntityIds);
}
