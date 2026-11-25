using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Infrastructure.Security;

/// <summary>
/// The caller's tenant, legal-entity scope, identity and permissions for the current request.
/// Populated by the API from the validated token; consumed by the DbContext for row-level isolation
/// and by services for authorisation.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    Guid? UserId { get; }
    string? UserName { get; }
    Guid? EmployeeId { get; }
    IReadOnlySet<string> Roles { get; }
    IReadOnlySet<string> Permissions { get; }
    /// <summary>Legal entities the user may act on; empty = all in tenant.</summary>
    IReadOnlySet<Guid> LegalEntityIds { get; }
    /// <summary>Holds the <see cref="Domain.Identity.Roles.PlatformAdministrator"/> role. Grants no tenant permission by itself.</summary>
    bool IsPlatformAdministrator { get; }
    /// <summary>
    /// Code acting on its own behalf (seeding, provisioning, background jobs), never a signed-in person. Passes every
    /// permission check and is exempt from separation of duties. Never set from a token.
    /// </summary>
    bool IsSystem { get; }
    string? IpAddress { get; }
    /// <summary>Set when a program is calling with an API key; <see cref="UserId"/> is then the key's id, not a person.</summary>
    Guid? ApiClientId => null;
}

public static class TenantContextExtensions
{
    public static Guid RequireTenant(this ITenantContext ctx) =>
        ctx.TenantId ?? throw new ForbiddenException("No tenant context.");

    public static Guid RequireUser(this ITenantContext ctx) =>
        ctx.UserId ?? throw new ForbiddenException("No authenticated user.");

    public static bool Has(this ITenantContext ctx, string permission) =>
        ctx.IsSystem || ctx.Permissions.Contains(permission);

    /// <summary>Holds any operator role (see <see cref="Domain.Identity.Roles.Platform"/>).</summary>
    public static bool IsOperator(this ITenantContext ctx) => ctx.Roles.Overlaps(Domain.Identity.Roles.Platform);

    public static void Require(this ITenantContext ctx, string permission)
    {
        if (!ctx.Has(permission)) throw new ForbiddenException($"Permission '{permission}' is required.");
    }

    public static void RequireLegalEntity(this ITenantContext ctx, Guid legalEntityId)
    {
        if (ctx.LegalEntityIds.Count > 0 && !ctx.LegalEntityIds.Contains(legalEntityId))
            throw new ForbiddenException("You do not have access to this legal entity.");
    }
}

/// <summary>Mutable implementation populated per request (or by background jobs/tests).</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public Guid? EmployeeId { get; set; }
    public IReadOnlySet<string> Roles { get; set; } = new HashSet<string>();
    public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>();
    public IReadOnlySet<Guid> LegalEntityIds { get; set; } = new HashSet<Guid>();
    public bool IsPlatformAdministrator { get; set; }
    public bool IsSystem { get; set; }
    public string? IpAddress { get; set; }
    public Guid? ApiClientId { get; set; }

    public static TenantContext System(Guid tenantId) => new()
    {
        TenantId = tenantId, UserName = "system", IsSystem = true,
        Permissions = new HashSet<string>(Domain.Identity.Permissions.ForRoles([Domain.Identity.Roles.TenantAdministrator])),
    };
}
