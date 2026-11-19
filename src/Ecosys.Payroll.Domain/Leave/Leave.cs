using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Domain.Leave;

public class LeaveType : LegalEntityScopedEntity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public decimal AnnualEntitlementDays { get; set; }
    public bool IsPaid { get; set; } = true;
    public bool AccruesMonthly { get; set; } = true;
    public decimal MaxCarryOverDays { get; set; }
    public bool RequiresAttachment { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Pay tiers (Employment Act s.30 for sick leave: 7 days full pay then 7 days half pay per year).
    /// Null means every entitled day is on full pay. Days beyond full + half tiers are unpaid.
    /// </summary>
    public decimal? FullPayDaysPerYear { get; set; }
    public decimal HalfPayDaysPerYear { get; set; }
    /// <summary>Minimum months of service before the leave can be taken (sick leave: 2).</summary>
    public int MinServiceMonths { get; set; }
    /// <summary>Count only working days (excluding weekends and public holidays); otherwise calendar days (e.g. maternity).</summary>
    public bool CountsWorkingDaysOnly { get; set; } = true;
}

/// <summary>Gazetted public holiday for a legal entity. <see cref="ObservedDate"/> applies the Sunday substitution rule.</summary>
public class PublicHoliday : LegalEntityScopedEntity
{
    public DateOnly Date { get; set; }
    public DateOnly ObservedDate { get; set; }
    public required string Name { get; set; }
    public bool IsGazettedAdHoc { get; set; }
}

public class EmployeeLeaveBalance : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public int Year { get; set; }
    public decimal Entitlement { get; set; }
    public decimal Accrued { get; set; }
    public decimal Taken { get; set; }
    public decimal CarriedOver { get; set; }
    public decimal Available => CarriedOver + Accrued - Taken;
}

public enum LeaveRequestStatus { Pending, Approved, Rejected, Cancelled }

public class LeaveRequest : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public decimal Days { get; set; }
    public string? Reason { get; set; }
    public LeaveRequestStatus Status { get; set; } = LeaveRequestStatus.Pending;
    public Guid? ApproverId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? ApproverComment { get; set; }
    /// <summary>Last payroll run that deducted pay for this leave (display and cancel guard only).</summary>
    public Guid? ProcessedInRunId { get; set; }

    /// <summary>Pay split computed at approval from the type's pay tiers and days already taken this year.</summary>
    public decimal FullPayDays { get; set; }
    public decimal HalfPayDays { get; set; }
    public decimal UnpaidDays { get; set; }
    public Guid? AttachmentFileId { get; set; }
    /// <summary>Line manager (employee) the request was routed to.</summary>
    public Guid? ApproverEmployeeId { get; set; }
}
