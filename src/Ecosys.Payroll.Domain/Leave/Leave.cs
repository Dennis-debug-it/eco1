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
    /// <summary>Set when unpaid leave has been deducted in a payroll run.</summary>
    public Guid? ProcessedInRunId { get; set; }
}
