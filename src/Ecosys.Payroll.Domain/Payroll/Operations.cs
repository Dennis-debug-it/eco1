using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Domain.Payroll;

/// <summary>
/// Attendance summary for one employee in one run, imported from a time/biometric system or captured
/// manually (PRD §4.1 step 5). Converted into overtime earnings and absence deductions at calculation time.
/// </summary>
public class AttendanceRecord : LegalEntityScopedEntity
{
    public Guid PayrollRunId { get; set; }
    public Guid EmployeeId { get; set; }
    public decimal DaysWorked { get; set; }
    /// <summary>Unauthorised / unpaid absence days (not already covered by an approved unpaid-leave request).</summary>
    public decimal AbsentDays { get; set; }
    public decimal OvertimeHours { get; set; }
    /// <summary>Overtime on rest days and public holidays (higher multiplier).</summary>
    public decimal RestDayOvertimeHours { get; set; }
    public string Source { get; set; } = "manual";
    public string? Note { get; set; }
}

public enum NotificationChannel { Email, WhatsApp, Sms }
public enum NotificationStatus { Pending, Sent, Failed, Skipped }

/// <summary>
/// Transactional outbox for notifications (PRD §15: Ecosys Notifications). Written in the same transaction
/// as the business change, delivered asynchronously by a dispatcher, retried on failure.
/// Bodies never contain salary figures — they point the recipient to the secure portal.
/// </summary>
public class NotificationMessage : TenantEntity
{
    public NotificationChannel Channel { get; set; }
    public required string Recipient { get; set; }
    public string? RecipientName { get; set; }
    public required string Template { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? LastError { get; set; }
    public string? ProviderReference { get; set; }
}
