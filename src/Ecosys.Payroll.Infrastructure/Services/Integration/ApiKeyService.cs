using System.Security.Cryptography;
using System.Text;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Integration;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Integration;

public sealed record ApiKeyRequest(string Name, List<string>? Permissions, List<Guid>? LegalEntityIds, DateOnly? ExpiresOn, int? RequestsPerMinute);

/// <summary>The caller's view of a key. The key itself appears only in <see cref="ApiKeyCreated"/>.</summary>
public sealed record ApiKeyView(Guid Id, string Name, string KeyPrefix, IReadOnlyList<string> Permissions, IReadOnlyList<Guid> LegalEntityIds,
    int RequestsPerMinute, DateTimeOffset CreatedAt, string? CreatedByName, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, string? RevokedByName,
    DateTimeOffset? LastUsedAt, string? LastUsedIp, string State)
{
    public static ApiKeyView From(ApiClient c, DateTimeOffset now) => new(c.Id, c.Name, c.KeyPrefix, c.Permissions, c.LegalEntityIds, c.RequestsPerMinute,
        c.CreatedAt, c.CreatedByName, c.ExpiresAt, c.RevokedAt, c.RevokedByName, c.LastUsedAt, c.LastUsedIp,
        c.RevokedAt is not null ? "Revoked" : c.ExpiresAt is { } x && x <= now ? "Expired" : "Active");
}

public sealed record ApiKeyCreated(ApiKeyView Client, string Key);

/// <summary>What a valid key resolves to, before any tenant is known.</summary>
public sealed record ApiKeyIdentity(Guid ClientId, Guid TenantId, string Name, IReadOnlyList<string> Permissions, IReadOnlyList<Guid> LegalEntityIds, int RequestsPerMinute);

/// <summary>
/// API keys for programs. A key can never do more than the administrator who made it, never approve, finalise or
/// reverse payroll, and is stored only as a hash.
/// </summary>
public sealed class ApiKeyService(PayrollDbContext db, ITenantContext ctx, AuditService audit)
{
    public const string LivePrefix = "eco_live_";
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>43 random characters from a 62-letter alphabet (about 256 bits), after a recognisable prefix.</summary>
    public static string NewKey() => LivePrefix + RandomNumberGenerator.GetString(Alphabet, 43);

    public static bool LooksLikeKey(string? value) => value is { Length: > 20 } && value.StartsWith(LivePrefix, StringComparison.Ordinal);

    public async Task<IReadOnlyList<ApiKeyView>> ListAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.IntegrationsManage);
        var now = DateTimeOffset.UtcNow;
        return (await db.ApiClients.OrderBy(c => c.RevokedAt != null).ThenByDescending(c => c.CreatedAt).ToListAsync(ct)).Select(c => ApiKeyView.From(c, now)).ToList();
    }

    public async Task<ApiKeyCreated> CreateAsync(ApiKeyRequest req, CancellationToken ct)
    {
        ctx.Require(Permissions.IntegrationsManage);
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 80) throw new DomainException("Give the key a name of up to 80 characters, such as the system that will use it.", "VALIDATION");
        if (await db.ApiClients.AnyAsync(c => c.Name == name && c.RevokedAt == null, ct)) throw new DomainException($"An active key is already called \"{name}\".", "DUPLICATE");

        var perms = (req.Permissions ?? []).Distinct().ToList();
        if (perms.Count == 0) throw new DomainException("Choose at least one thing the key may do.", "VALIDATION");
        var notGrantable = perms.Except(ApiScopes.Grantable).ToList();
        if (notGrantable.Count > 0) throw new DomainException($"An API key can't be given {string.Join(", ", notGrantable)}. Approving, finalising and reversing payroll need a person.", "VALIDATION");
        var notHeld = perms.Where(p => !ctx.Has(p)).ToList();
        if (notHeld.Count > 0) throw new ForbiddenException($"You can't give a key access you don't have yourself: {string.Join(", ", notHeld)}.");
        if (perms.Contains(Permissions.EmployeesWrite) && !perms.Contains(Permissions.EmployeesRead)) perms.Add(Permissions.EmployeesRead);

        var les = (req.LegalEntityIds ?? []).Distinct().ToList();
        // A person limited to some companies can only make keys limited to (some of) the same companies.
        if (ctx.LegalEntityIds.Count > 0 && (les.Count == 0 || les.Any(l => !ctx.LegalEntityIds.Contains(l))))
            throw new ForbiddenException("Limit the key to companies you have access to.");
        if (les.Count > 0 && await db.LegalEntities.CountAsync(l => les.Contains(l.Id), ct) != les.Count)
            throw new DomainException("One of the chosen companies was not found.", "VALIDATION");

        var today = BusinessClock.Today();
        if (req.ExpiresOn is { } exp && (exp <= today || exp > today.AddYears(3))) throw new DomainException("The expiry date must be in the future and within three years.", "VALIDATION");
        var rpm = req.RequestsPerMinute ?? 120;
        if (rpm is < 10 or > 1200) throw new DomainException("Requests per minute must be between 10 and 1,200.", "VALIDATION");

        var key = NewKey();
        var client = new ApiClient
        {
            Name = name, KeyPrefix = key[..(LivePrefix.Length + 4)], KeyHash = Hash(key), Permissions = perms, LegalEntityIds = les,
            CreatedByName = ctx.UserName, RequestsPerMinute = rpm,
            ExpiresAt = req.ExpiresOn is { } e ? new DateTimeOffset(e.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
        };
        db.ApiClients.Add(client);
        audit.Record("integration.key.created", nameof(ApiClient), client.Id, new { client.Name, client.KeyPrefix, client.Permissions, client.LegalEntityIds, client.ExpiresAt, client.RequestsPerMinute });
        await db.SaveChangesAsync(ct);
        return new ApiKeyCreated(ApiKeyView.From(client, DateTimeOffset.UtcNow), key);
    }

    public async Task<ApiKeyView> RevokeAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.IntegrationsManage);
        var c = await db.ApiClients.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(ApiClient), id);
        if (c.RevokedAt is null)
        {
            c.RevokedAt = DateTimeOffset.UtcNow; c.RevokedByName = ctx.UserName;
            audit.Record("integration.key.revoked", nameof(ApiClient), c.Id, new { c.Name, c.KeyPrefix });
            await db.SaveChangesAsync(ct);
        }
        return ApiKeyView.From(c, DateTimeOffset.UtcNow);
    }

    /// <summary>Finds the key across all organisations (no tenant is known yet). Null when unknown, revoked or expired.</summary>
    public static async Task<ApiKeyIdentity?> FindAsync(PayrollDbContext db, string key, CancellationToken ct)
    {
        var hash = Hash(key);
        var was = db.IgnoreTenantFilter;
        db.IgnoreTenantFilter = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var c = await db.ApiClients.AsNoTracking().FirstOrDefaultAsync(x => x.KeyHash == hash, ct);
            if (c is null || !c.IsUsable(now)) return null;
            if (!await db.Tenants.AnyAsync(t => t.Id == c.TenantId, ct)) return null;
            return new ApiKeyIdentity(c.Id, c.TenantId, c.Name, c.Permissions, c.LegalEntityIds, c.RequestsPerMinute);
        }
        finally { db.IgnoreTenantFilter = was; }
    }

    /// <summary>Records when and from where a key was last used, at most once a minute per key.</summary>
    public static Task TouchAsync(PayrollDbContext db, Guid clientId, string? ip, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-1);
        return db.ApiClients.IgnoreQueryFilters().Where(c => c.Id == clientId && (c.LastUsedAt == null || c.LastUsedAt < cutoff))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, now).SetProperty(c => c.LastUsedIp, ip), ct);
    }
}
