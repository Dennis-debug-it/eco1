using System.Text.Json;
using Ecosys.Payroll.Domain.Integration;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;

namespace Ecosys.Payroll.Infrastructure.Services;

/// <summary>Appends immutable audit records. Callers save as part of their own transaction.</summary>
public sealed class AuditService(PayrollDbContext db, ITenantContext tenant)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Record(string action, string entityType, Guid? entityId, object? details = null, Guid? legalEntityId = null)
    {
        db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = tenant.TenantId ?? Guid.Empty,
            LegalEntityId = legalEntityId,
            ActorId = tenant.UserId,
            ActorName = tenant.UserName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details is null ? null : details is string s ? s : JsonSerializer.Serialize(details, Json),
            IpAddress = tenant.IpAddress,
        });
        RaiseEvent(action, entityType, entityId, details, legalEntityId);
    }

    /// <summary>
    /// Integration events come from audit actions, so an event is saved in the same unit of work as the change it
    /// describes and never exists for a change that was rolled back. Only record ids travel; never the details.
    /// </summary>
    private void RaiseEvent(string action, string entityType, Guid? entityId, object? details, Guid? legalEntityId)
    {
        if (tenant.TenantId is not { } tenantId || tenantId == Guid.Empty) return;
        var raised = IntegrationEventTypes.ForAuditAction(action, Property(details, "to")?.ToString());
        if (raised is not { } e) return;
        var id = e.Resource == IntegrationEventTypes.Resources.Employee && entityType != "Employee"
            ? Property(details, "employeeId") as Guid?
            : entityId;
        db.IntegrationEvents.Add(new IntegrationEvent
        {
            TenantId = tenantId, Type = e.Type, LegalEntityId = legalEntityId, EntityType = e.Resource, EntityId = id, ActorName = tenant.UserName,
        });
    }

    private static object? Property(object? details, string name) =>
        details is null or string ? null : details.GetType().GetProperty(name)?.GetValue(details);
}
