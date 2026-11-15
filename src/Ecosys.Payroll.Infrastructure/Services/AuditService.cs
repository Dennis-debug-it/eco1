using System.Text.Json;
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
    }
}
