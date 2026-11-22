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
public sealed record MeResponse(Guid Id, string Email, string DisplayName, Guid TenantId, string TenantName, Guid? EmployeeId, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions, IReadOnlyList<Guid> LegalEntityIds, bool MfaEnabled = false, bool MfaRequired = false, bool MfaSatisfied = false, bool MustChangePassword = false, string? TenantSlug = null);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ResetPasswordRequest(string? NewPassword);
public sealed record UpdateUserRequest(string DisplayName, List<string> Roles, Guid? EmployeeId, List<Guid>? LegalEntityIds, bool IsActive, string? Phone = null);
public sealed record CreateUserRequest(string Email, string DisplayName, string Password, List<string> Roles, Guid? EmployeeId, List<Guid>? LegalEntityIds, string? Phone = null);
public sealed record MfaCode(string Code);
public sealed record MfaSetupResponse(string Secret, string ProvisioningUri);

public static class AuthEndpoints
{
    public static void MapAuth(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        // Local development identity. In Auth:Mode=Ecosys this endpoint is disabled and tokens come from the platform IdP.
        g.MapPost("/login", async (LoginRequest req, PayrollDbContext db, TokenService tokens, IConfiguration config, NotificationService notify, CancellationToken ct) =>
        {
            if (config["Auth:Mode"] == "Ecosys") return Results.Problem("Local login is disabled; sign in through Ecosys identity.", statusCode: 400);
            db.IgnoreTenantFilter = true;
            var now = DateTimeOffset.UtcNow;
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == (req.Tenant ?? "").ToLowerInvariant() && t.IsActive, ct);
            var user = tenant is null ? null : await db.Users.FirstOrDefaultAsync(u => u.TenantId == tenant.Id && u.Email == (req.Email ?? "").ToLowerInvariant() && u.IsActive, ct);
            void Log(string action, string? details = null) =>
                db.PayrollAuditLogs.Add(new Domain.Payroll.PayrollAuditLog { TenantId = tenant!.Id, ActorId = user?.Id, ActorName = user?.DisplayName ?? req.Email, Action = action, EntityType = "User", EntityId = user?.Id, Details = details });

            if (user?.LockedUntil is { } until && until > now)
            {
                Log("auth.login.locked");
                await db.SaveChangesAsync(ct);
                return Locked(until, now);
            }

            // A wrong password and a wrong authenticator code both count towards the lockout.
            async Task<IResult> FailAsync(string action, IResult response)
            {
                if (tenant is null) return response;
                Log(action);
                if (user is not null && ++user.FailedLoginCount >= config.GetValue("Auth:MaxFailedLogins", 5))
                {
                    user.FailedLoginCount = 0;
                    user.LockedUntil = now.AddMinutes(config.GetValue("Auth:LockoutMinutes", 15));
                    Log("auth.locked", $"{{\"until\":\"{user.LockedUntil:O}\"}}");
                    notify.Enqueue(Domain.Payroll.NotificationChannel.Email, user.Email, user.DisplayName, "auth.locked", "Your Ecosys Payroll account was locked",
                        $"Your account was locked at {BusinessClock.ToLocal(now):HH:mm} {BusinessClock.Abbreviation} after several failed sign-in attempts. It unlocks automatically in {config.GetValue("Auth:LockoutMinutes", 15)} minutes. If this wasn't you, tell your administrator.",
                        nameof(User), user.Id, tenant.Id);
                    await db.SaveChangesAsync(ct);
                    return Locked(user.LockedUntil.Value, now);
                }
                await db.SaveChangesAsync(ct);
                return response;
            }

            if (tenant is null || user?.PasswordHash is null || !PasswordHasher.Verify(req.Password ?? "", user.PasswordHash))
                return await FailAsync("auth.login.failed", Results.Problem("Invalid tenant, email or password.", statusCode: 401, title: "Unauthorised"));
            if (user.MfaEnabled)
            {
                if (string.IsNullOrWhiteSpace(req.Code))
                    return Results.Json(new { title = "Authentication code required", status = 401, code = "MFA_CODE_REQUIRED", detail = "Enter the 6-digit code from your authenticator app." }, statusCode: 401);
                var step = Totp.Match(user.MfaSecret!, req.Code, now);
                if (step is null || step <= user.LastTotpStep) // a code that was already used to sign in is rejected (RFC 6238 §5.2)
                    return await FailAsync("auth.mfa.failed", Results.Json(new { title = "Invalid authentication code", status = 401, code = "MFA_CODE_INVALID", detail = "The authentication code is incorrect, has expired or was already used. Wait for the next code." }, statusCode: 401));
                user.LastTotpStep = step;
            }

            user.LastLoginAt = now;
            user.FailedLoginCount = 0;
            user.LockedUntil = null;
            var (token, expires) = tokens.Issue(user, tenant, mfa: user.MfaEnabled);
            Log("auth.login", user.MfaEnabled ? "{\"mfa\":true}" : null);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new LoginResponse(token, expires, Me(user, tenant, config, user.MfaEnabled)));
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        // ---- Passwords and sessions ----
        g.MapPost("/password", async (ChangePasswordRequest body, HttpContext http, ITenantContext ctx, PayrollDbContext db, TokenService tokens, SessionValidator sessions, AuditService audit, IConfiguration config, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == ctx.UserId, ct) ?? throw new ForbiddenException("Unknown user.");
            if (user.PasswordHash is null || !PasswordHasher.Verify(body.CurrentPassword ?? "", user.PasswordHash))
                return Results.Problem("Your current password is incorrect.", statusCode: 400, title: "Incorrect password");
            if (body.NewPassword == body.CurrentPassword) return Results.Problem("Choose a password you haven't just used.", statusCode: 400, title: "Password not accepted");
            if (PasswordPolicy.Validate(body.NewPassword, user.Email, user.DisplayName) is { } why) return Results.Problem(why, statusCode: 400, title: "Password not accepted");
            user.PasswordHash = PasswordHasher.Hash(body.NewPassword);
            user.PasswordChangedAt = DateTimeOffset.UtcNow;
            user.MustChangePassword = false;
            user.RotateSecurityStamp(); // signs out every other session
            audit.Record("auth.password.changed", nameof(User), user.Id);
            await db.SaveChangesAsync(ct);
            sessions.Invalidate(user.Id);
            var tenant = await db.Tenants.FirstAsync(t => t.Id == user.TenantId, ct);
            var mfa = http.User.FindFirst(ClaimNames.AuthMethod)?.Value == "mfa";
            var (token, expires) = tokens.Issue(user, tenant, mfa);
            return Results.Ok(new LoginResponse(token, expires, Me(user, tenant, config, mfa)));
        }).RequireAuthorization();

        g.MapPost("/sessions/revoke", async (ITenantContext ctx, PayrollDbContext db, SessionValidator sessions, AuditService audit, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == ctx.UserId, ct) ?? throw new ForbiddenException("Unknown user.");
            user.RotateSecurityStamp();
            audit.Record("auth.sessions.revoked", nameof(User), user.Id);
            await db.SaveChangesAsync(ct);
            sessions.Invalidate(user.Id);
            return Results.NoContent();
        }).RequireAuthorization();

        // What the sign-in page may show before anyone is signed in. Demo hints only on servers that load demo data.
        g.MapGet("/options", (IConfiguration config) => Results.Ok(new
        {
            demo = config.GetValue("Payroll:SeedDemoData", false),
            localLogin = config["Auth:Mode"] != "Ecosys",
        })).AllowAnonymous();

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
            return Results.Ok(await db.Users.OrderBy(u => u.Email).Select(u => new { u.Id, u.Email, u.DisplayName, u.Roles, u.EmployeeId, u.LegalEntityIds, u.IsActive, u.LastLoginAt, u.MfaEnabled, u.Phone, u.LockedUntil, u.MustChangePassword, u.PasswordChangedAt }).ToListAsync(ct));
        }).RequireAuthorization();

        g.MapPost("/users", async (CreateUserRequest req, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var bad = req.Roles.Except(Roles.All).ToList();
            if (bad.Count > 0) return Results.Problem($"Unknown roles: {string.Join(", ", bad)}", statusCode: 400);
            if (req.Roles.Contains(Roles.PlatformAdministrator) && !ctx.IsPlatformAdministrator) return Results.Problem("Cannot grant platform administrator.", statusCode: 403);
            if (PasswordPolicy.Validate(req.Password, req.Email, req.DisplayName) is { } why) return Results.Problem(why, statusCode: 400, title: "Password not accepted");
            if (req.EmployeeId is { } emp && !await db.Employees.AnyAsync(e => e.Id == emp, ct)) return Results.Problem("Unknown employee.", statusCode: 400);
            if (await db.Users.AnyAsync(x => x.Email == req.Email.ToLowerInvariant(), ct)) return Results.Problem("A user with that email already exists.", statusCode: 409);
            var u = new User { Email = req.Email.ToLowerInvariant(), DisplayName = req.DisplayName, PasswordHash = PasswordHasher.Hash(req.Password), Roles = req.Roles, EmployeeId = req.EmployeeId, LegalEntityIds = req.LegalEntityIds ?? [], Phone = Phone(req.Phone), MustChangePassword = true };
            db.Users.Add(u);
            audit.Record("user.created", nameof(User), u.Id, new { u.Email, u.Roles });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/auth/users/{u.Id}", new { u.Id, u.Email, u.DisplayName, u.Roles, u.EmployeeId });
        }).RequireAuthorization();

        g.MapPut("/users/{id:guid}", async (Guid id, UpdateUserRequest req, ITenantContext ctx, PayrollDbContext db, SessionValidator sessions, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(User), id);
            var bad = req.Roles.Except(Roles.All).ToList();
            if (bad.Count > 0) return Results.Problem($"Unknown roles: {string.Join(", ", bad)}", statusCode: 400);
            var platformChange = req.Roles.Contains(Roles.PlatformAdministrator) != u.Roles.Contains(Roles.PlatformAdministrator);
            if (platformChange && !ctx.IsPlatformAdministrator) return Results.Problem("Only a platform administrator can change platform administration.", statusCode: 403);
            if (id == ctx.UserId && (!req.IsActive || !req.Roles.Contains(Roles.TenantAdministrator) && u.Roles.Contains(Roles.TenantAdministrator)))
                return Results.Problem("You can't deactivate yourself or remove your own administrator role. Ask another administrator.", statusCode: 400);
            if (req.EmployeeId is { } emp && !await db.Employees.AnyAsync(e => e.Id == emp, ct)) return Results.Problem("Unknown employee.", statusCode: 400);
            if (string.IsNullOrWhiteSpace(req.DisplayName)) return Results.Problem("Enter a display name.", statusCode: 400);

            var accessChanged = !u.Roles.Order().SequenceEqual(req.Roles.Order()) || u.IsActive != req.IsActive || u.EmployeeId != req.EmployeeId
                || !u.LegalEntityIds.Order().SequenceEqual((req.LegalEntityIds ?? []).Order());
            var before = new { u.Roles, u.IsActive, u.EmployeeId, u.LegalEntityIds };
            u.DisplayName = req.DisplayName.Trim(); u.Roles = req.Roles; u.EmployeeId = req.EmployeeId; u.LegalEntityIds = req.LegalEntityIds ?? []; u.IsActive = req.IsActive; u.Phone = Phone(req.Phone);
            if (accessChanged) u.RotateSecurityStamp(); // existing sessions carry the old roles; end them
            audit.Record(req.IsActive ? "user.updated" : "user.deactivated", nameof(User), u.Id, new { before, after = new { u.Roles, u.IsActive, u.EmployeeId, u.LegalEntityIds } });
            await db.SaveChangesAsync(ct);
            sessions.Invalidate(u.Id);
            return Results.NoContent();
        }).RequireAuthorization();

        // Administrator reset: issues a one-time password the user must change at next sign-in.
        g.MapPost("/users/{id:guid}/password", async (Guid id, ResetPasswordRequest body, ITenantContext ctx, PayrollDbContext db, SessionValidator sessions, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            if (id == ctx.UserId) return Results.Problem("Change your own password from your security settings.", statusCode: 400);
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(User), id);
            if (u.Roles.Contains(Roles.PlatformAdministrator) && !ctx.IsPlatformAdministrator) return Results.Problem("Cannot reset a platform administrator.", statusCode: 403);
            var password = string.IsNullOrWhiteSpace(body.NewPassword) ? TemporaryPassword() : body.NewPassword;
            if (PasswordPolicy.Validate(password, u.Email, u.DisplayName) is { } why) return Results.Problem(why, statusCode: 400, title: "Password not accepted");
            u.PasswordHash = PasswordHasher.Hash(password);
            u.MustChangePassword = true; u.FailedLoginCount = 0; u.LockedUntil = null; u.PasswordChangedAt = DateTimeOffset.UtcNow;
            u.RotateSecurityStamp();
            audit.Record("auth.password.reset", nameof(User), u.Id, new { by = ctx.UserName });
            await db.SaveChangesAsync(ct);
            sessions.Invalidate(u.Id);
            return Results.Ok(new { temporaryPassword = password });
        }).RequireAuthorization();

        g.MapPost("/users/{id:guid}/unlock", async (Guid id, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(User), id);
            u.LockedUntil = null; u.FailedLoginCount = 0;
            audit.Record("auth.unlocked", nameof(User), u.Id, new { by = ctx.UserName });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapGet("/roles", () => Results.Ok(Roles.All.Select(r => new { role = r, permissions = Permissions.ForRoles([r]).Order() }))).RequireAuthorization();
    }

    public const string LoginRateLimit = "login";

    private static string? Phone(string? raw) => string.IsNullOrWhiteSpace(raw) ? null
        : KenyaIdentifiers.NormalisePhone(raw) ?? throw new DomainException($"Mobile '{raw}' isn't a valid number. Use 07XX XXX XXX or +country code.", "VALIDATION");

    private static IResult Locked(DateTimeOffset until, DateTimeOffset now) =>
        Results.Json(new { title = "Account temporarily locked", status = 429, code = "ACCOUNT_LOCKED",
            detail = $"Too many failed sign-in attempts. Try again in {Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes))} minute(s), or ask an administrator to unlock your account." }, statusCode: 429);

    /// <summary>Four random words from a short list plus digits: easy to read out over the phone, ~40 bits.</summary>
    private static string TemporaryPassword()
    {
        string[] words = ["acacia", "baobab", "cheetah", "dhow", "eland", "flamingo", "gazelle", "hornbill", "impala", "jacaranda", "kudu", "lake", "mango", "nyati", "oryx", "pelican", "rhino", "savanna", "tusker", "umbrella", "valley", "weaver", "zebra", "kestrel"];
        var pick = () => words[System.Security.Cryptography.RandomNumberGenerator.GetInt32(words.Length)];
        return $"{pick()}-{pick()}-{pick()}-{System.Security.Cryptography.RandomNumberGenerator.GetInt32(100, 1000)}";
    }

    private static MeResponse Me(User u, Domain.Organisation.Tenant t, IConfiguration config, bool mfaSatisfied) =>
        new(u.Id, u.Email, u.DisplayName, t.Id, t.Name, u.EmployeeId, u.Roles, Permissions.ForRoles(u.Roles).Order().ToList(), u.LegalEntityIds,
            u.MfaEnabled, config.GetValue("Auth:RequireMfaForPrivilegedRoles", true) && u.Roles.Intersect(Roles.Privileged).Any(), mfaSatisfied, u.MustChangePassword, t.Slug);
}
