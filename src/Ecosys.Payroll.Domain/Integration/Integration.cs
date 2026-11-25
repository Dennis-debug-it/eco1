using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;

namespace Ecosys.Payroll.Domain.Integration;

/// <summary>
/// A program (biometric system, ERP, HR tool) that calls the public API with a key instead of a person signing in.
/// Only the SHA-256 of the key is stored; the key itself is shown once, when it is created.
/// </summary>
public class ApiClient : TenantEntity
{
    public required string Name { get; set; }
    /// <summary>The first characters of the key (e.g. <c>eco_live_3Fk9</c>) so people can tell keys apart. Not secret.</summary>
    public required string KeyPrefix { get; set; }
    /// <summary>Hex SHA-256 of the whole key. The key has 256 bits of randomness, so a fast hash is enough.</summary>
    public required string KeyHash { get; set; }
    /// <summary>What the key may do: a subset of <see cref="ApiScopes.Grantable"/>, and of what its creator holds.</summary>
    public List<string> Permissions { get; set; } = [];
    /// <summary>Companies the key may act on; empty means all in the organisation.</summary>
    public List<Guid> LegalEntityIds { get; set; } = [];
    public string? CreatedByName { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedByName { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public string? LastUsedIp { get; set; }
    /// <summary>Requests allowed per minute before the API answers 429.</summary>
    public int RequestsPerMinute { get; set; } = 120;

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

/// <summary>What an API key can be given. Approving, finalising and reversing payroll stay with people, who sign in with MFA.</summary>
public static class ApiScopes
{
    public static readonly string[] Grantable =
    [
        Permissions.EmployeesRead, Permissions.EmployeesWrite, Permissions.SalaryRead,
        Permissions.PayrollPrepare, Permissions.JournalsRead, Permissions.ReportsRead, Permissions.AuditRead,
    ];

    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [Permissions.EmployeesRead] = "Read employees and the organisation structure",
        [Permissions.EmployeesWrite] = "Create and update employees (identity numbers shown in full)",
        [Permissions.SalaryRead] = "See pay: salaries and payroll results",
        [Permissions.PayrollPrepare] = "Send attendance and one-off pay items into open payroll runs",
        [Permissions.JournalsRead] = "Read payroll journals and download accounting files",
        [Permissions.ReportsRead] = "Read payroll runs and reports",
        [Permissions.AuditRead] = "Read the audit log",
    };
}

/// <summary>Something that happened, offered to integrations as a webhook and through <c>GET /api/v1/events</c>.</summary>
public class IntegrationEvent : TenantEntity
{
    /// <summary>Increasing number, used as the cursor for the events feed.</summary>
    public long Sequence { get; set; }
    public required string Type { get; set; }
    public Guid? LegalEntityId { get; set; }
    public required string EntityType { get; set; }
    public Guid? EntityId { get; set; }
    /// <summary>Who caused it: a person's name, or "API: key name".</summary>
    public string? ActorName { get; set; }
    /// <summary>Set once deliveries have been created for every webhook listening at the time.</summary>
    public bool FannedOut { get; set; }
}

/// <summary>
/// Event names. Each comes from an audit action recorded in the same unit of work, so an event exists exactly when
/// the change was saved. Payloads name the record, never pay figures: receivers fetch details with their key.
/// </summary>
public static class IntegrationEventTypes
{
    public const string EmployeeCreated = "employee.created";
    public const string EmployeeUpdated = "employee.updated";
    public const string EmployeeSeparated = "employee.separated";
    public const string PayrollRunApproved = "payroll_run.approved";
    public const string PayrollRunPaid = "payroll_run.paid";
    public const string PayrollRunFinalised = "payroll_run.finalised";
    public const string PayrollRunReversed = "payroll_run.reversed";
    public const string LeaveApproved = "leave.approved";
    public const string LeaveCancelled = "leave.cancelled";
    public const string EmployeesImported = "employees.imported";
    /// <summary>Sent only by "Send test event"; never stored in the feed.</summary>
    public const string Ping = "ping";

    public static readonly string[] All =
    [
        EmployeeCreated, EmployeeUpdated, EmployeeSeparated, PayrollRunApproved, PayrollRunPaid, PayrollRunFinalised, PayrollRunReversed,
        LeaveApproved, LeaveCancelled, EmployeesImported,
    ];

    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [EmployeeCreated] = "An employee was added",
        [EmployeeUpdated] = "An employee's details, statutory numbers, salary or verified bank account changed",
        [EmployeeSeparated] = "An exit was completed",
        [PayrollRunApproved] = "A payroll run received its final approval",
        [PayrollRunPaid] = "Payments for a run were confirmed",
        [PayrollRunFinalised] = "A run was finalised and its journal posted",
        [PayrollRunReversed] = "A finalised run was reversed",
        [LeaveApproved] = "A leave request was approved",
        [LeaveCancelled] = "A leave request was cancelled",
        [EmployeesImported] = "A data-migration import was applied",
    };

    /// <summary>
    /// The event an audit action raises, if any, and the kind of record it is about. <paramref name="toStatus"/> is the
    /// run's status after a run action.
    /// </summary>
    public static (string Type, string Resource)? ForAuditAction(string action, string? toStatus) => action switch
    {
        "employee.created" => (EmployeeCreated, Resources.Employee),
        "employee.updated" or "employee.statutory.updated" or "employee.salary.assigned" or "employee.bank.verified" or "employee.contract_end.updated" => (EmployeeUpdated, Resources.Employee),
        "separation.completed" => (EmployeeSeparated, Resources.Employee),
        // A partial approval records the approver but leaves the run under review; only the final one counts.
        "payroll.run.approved" => toStatus == "Approved" ? (PayrollRunApproved, Resources.PayrollRun) : null,
        "payroll.run.paymentconfirmed" => toStatus == "Paid" ? (PayrollRunPaid, Resources.PayrollRun) : null,
        "payroll.run.finalised" => (PayrollRunFinalised, Resources.PayrollRun),
        "payroll.run.reversed" => (PayrollRunReversed, Resources.PayrollRun),
        "leave.approved" => (LeaveApproved, Resources.LeaveRequest),
        "leave.cancelled" => (LeaveCancelled, Resources.LeaveRequest),
        "migration.import.applied" => (EmployeesImported, Resources.ImportBatch),
        _ => null,
    };

    /// <summary>Record kinds named in event payloads, with where the API serves them.</summary>
    public static class Resources
    {
        public const string Employee = "employee";
        public const string PayrollRun = "payroll_run";
        public const string LeaveRequest = "leave_request";
        public const string ImportBatch = "import_batch";

        public static string? Url(string resource, Guid? id) => id is null ? null : resource switch
        {
            Employee => $"/api/v1/employees/{id}",
            PayrollRun => $"/api/v1/payroll-runs/{id}",
            LeaveRequest => $"/api/v1/leave-requests/{id}",
            _ => null,
        };
    }
}

/// <summary>An HTTPS address that receives signed event notifications.</summary>
public class WebhookEndpoint : TenantEntity
{
    public required string Name { get; set; }
    public required string Url { get; set; }
    /// <summary>Event types to send; empty means all.</summary>
    public List<string> Events { get; set; } = [];
    /// <summary>Signing secret (<c>whsec_…</c>), encrypted at rest. Shown once when created or rotated.</summary>
    public required string Secret { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Deliveries in a row that used up all their retries. Reset by any success.</summary>
    public int ConsecutiveFailures { get; set; }
    public string? DisabledReason { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }

    public bool Wants(string type) => Events.Count == 0 || Events.Contains(type);
}

public enum WebhookDeliveryStatus { Pending, Delivered, Failed }

/// <summary>One attempt chain to send one event to one endpoint. Retried with back-off; can be sent again by hand.</summary>
public class WebhookDelivery : TenantEntity
{
    public Guid WebhookEndpointId { get; set; }
    public Guid? IntegrationEventId { get; set; }
    public required string EventType { get; set; }
    /// <summary>The exact JSON body sent, so retries are byte-identical and the signature can be checked against it.</summary>
    public required string Payload { get; set; }
    public WebhookDeliveryStatus Status { get; set; } = WebhookDeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public int? DurationMs { get; set; }
}

/// <summary>
/// The stored answer to a write sent with an <c>Idempotency-Key</c> header, so a retried request gets the same answer
/// instead of doing the work twice. Kept for 24 hours.
/// </summary>
public class IdempotencyRecord : TenantEntity
{
    public Guid ApiClientId { get; set; }
    public required string Key { get; set; }
    /// <summary>SHA-256 of method, path, query and body. The same key with a different request is refused.</summary>
    public required string RequestHash { get; set; }
    /// <summary>0 while the first request is still running.</summary>
    public int StatusCode { get; set; }
    public string? ContentType { get; set; }
    public string? ResponseBody { get; set; }
    public string? Location { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
