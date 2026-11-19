using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Domain.Organisation;

public enum BreachRisk { Low, Medium, High }
public enum BreachStatus { Open, Contained, Closed }

/// <summary>
/// A personal-data breach. Data Protection Act 2019 s.43: the controller notifies the Data Commissioner within
/// 72 hours of becoming aware of a breach that risks data subjects, and notifies affected people without delay
/// unless their identity can't be established or the data was unintelligible (e.g. encrypted).
/// </summary>
public class DataBreach : TenantEntity
{
    public string Reference { get; set; } = "";
    public DateTimeOffset DetectedAt { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public required string Description { get; set; }
    /// <summary>Categories of personal data affected, e.g. "Bank details", "KRA PIN", "Payslips".</summary>
    public List<string> DataCategories { get; set; } = [];
    public int? SubjectsAffected { get; set; }
    public BreachRisk Risk { get; set; } = BreachRisk.Medium;
    public string? Cause { get; set; }
    public string? Containment { get; set; }
    public DateTimeOffset? ReportedToCommissionerAt { get; set; }
    public string? CommissionerReference { get; set; }
    /// <summary>Why the breach was not reported, when the assessment found no real risk to data subjects.</summary>
    public string? NotReportedReason { get; set; }
    public DateTimeOffset? SubjectsNotifiedAt { get; set; }
    public string? SubjectsNotNotifiedReason { get; set; }
    public BreachStatus Status { get; set; } = BreachStatus.Open;
    public DateTimeOffset? ClosedAt { get; set; }

    public DateTimeOffset CommissionerDeadline => DetectedAt.AddHours(72);
}
