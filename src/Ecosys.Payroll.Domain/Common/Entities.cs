namespace Ecosys.Payroll.Domain.Common;

/// <summary>Base for every persisted record. UUID keys, audit stamps, soft deletion.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Every tenant-owned record carries the tenant id; row-level isolation is enforced by global query filters.</summary>
public abstract class TenantEntity : Entity
{
    public Guid TenantId { get; set; }
}

/// <summary>Tenant-owned record that also belongs to a single legal entity.</summary>
public abstract class LegalEntityScopedEntity : TenantEntity
{
    public Guid LegalEntityId { get; set; }
}

public interface IEffectiveDated
{
    DateOnly EffectiveFrom { get; }
    DateOnly? EffectiveTo { get; }
}

public static class EffectiveDatedExtensions
{
    public static bool IsEffectiveOn(this IEffectiveDated e, DateOnly date) =>
        date >= e.EffectiveFrom && (e.EffectiveTo is null || date <= e.EffectiveTo.Value);
}

public class DomainException(string message, string code = "DOMAIN_ERROR") : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class NotFoundException(string entity, Guid id) : DomainException($"{entity} {id} was not found.", "NOT_FOUND");

public sealed class ForbiddenException(string message) : DomainException(message, "FORBIDDEN");

public sealed class InvalidStateException(string message) : DomainException(message, "INVALID_STATE");
