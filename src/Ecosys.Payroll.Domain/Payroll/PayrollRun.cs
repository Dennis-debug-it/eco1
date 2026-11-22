using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Engine.Model;

namespace Ecosys.Payroll.Domain.Payroll;

public enum PayrollRunStatus
{
    Draft,
    Prepared,
    Calculated,
    UnderReview,
    Approved,
    PaymentProcessing,
    Paid,
    Finalised,
    Reversed,
    Rejected,
}

public enum PayrollRunType { Regular, Supplementary, Correction }

/// <summary>One execution of payroll for a group and period. Status transitions are enforced in <see cref="Transition"/>.</summary>
public class PayrollRun : LegalEntityScopedEntity
{
    public Guid PayrollGroupId { get; set; }
    public Guid PayrollPeriodId { get; set; }
    public required string Reference { get; set; }
    public PayrollRunType RunType { get; set; } = PayrollRunType.Regular;
    public PayrollRunStatus Status { get; set; } = PayrollRunStatus.Draft;
    public string Jurisdiction { get; set; } = "KE";
    public string CurrencyCode { get; set; } = "KES";
    public string? Notes { get; set; }

    public Guid? PreparedBy { get; set; }
    public DateTimeOffset? PreparedAt { get; set; }
    public DateTimeOffset? CalculatedAt { get; set; }
    public Guid? SubmittedBy { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    /// <summary>Set when a reviewer has checked the run in the current submission (legal entity setting RequireReviewBeforeApproval).</summary>
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    /// <summary>Distinct approvals received in the current submission; the run is approved once it reaches MinimumApprovals.</summary>
    public int ApprovalsReceived { get; set; }
    public int ApprovalsRequired { get; set; } = 1;
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? FinalisedBy { get; set; }
    public DateTimeOffset? FinalisedAt { get; set; }
    public Guid? ReversedBy { get; set; }
    public DateTimeOffset? ReversedAt { get; set; }
    public string? ReversalReason { get; set; }
    /// <summary>For a reversal or correction run, the run it corrects.</summary>
    public Guid? OriginalRunId { get; set; }

    /// <summary>Set when an authorised user permits a second regular run for the same group/period (PRD §9.2).</summary>
    public bool DuplicateAuthorised { get; set; }

    // Totals snapshot (recomputed on calculate, frozen on approve)
    public int EmployeeCount { get; set; }
    public decimal TotalGross { get; set; }
    public decimal TotalTaxable { get; set; }
    public decimal TotalPaye { get; set; }
    public decimal TotalNssfEmployee { get; set; }
    public decimal TotalNssfEmployer { get; set; }
    public decimal TotalShif { get; set; }
    public decimal TotalHousingLevyEmployee { get; set; }
    public decimal TotalHousingLevyEmployer { get; set; }
    public decimal TotalNita { get; set; }
    public decimal TotalOtherDeductions { get; set; }
    public decimal TotalEmployeeDeductions { get; set; }
    public decimal TotalNet { get; set; }
    public decimal TotalEmployerContributions { get; set; }
    public decimal TotalEmployerCost { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }

    public List<PayrollRunEmployee> Employees { get; set; } = [];

    public bool IsLocked => Status is PayrollRunStatus.Approved or PayrollRunStatus.PaymentProcessing
        or PayrollRunStatus.Paid or PayrollRunStatus.Finalised or PayrollRunStatus.Reversed;

    private static readonly Dictionary<PayrollRunStatus, PayrollRunStatus[]> Allowed = new()
    {
        [PayrollRunStatus.Draft] = [PayrollRunStatus.Prepared],
        [PayrollRunStatus.Prepared] = [PayrollRunStatus.Calculated, PayrollRunStatus.Draft],
        [PayrollRunStatus.Calculated] = [PayrollRunStatus.UnderReview, PayrollRunStatus.Prepared, PayrollRunStatus.Calculated],
        [PayrollRunStatus.UnderReview] = [PayrollRunStatus.Approved, PayrollRunStatus.Rejected],
        [PayrollRunStatus.Rejected] = [PayrollRunStatus.Prepared],
        [PayrollRunStatus.Approved] = [PayrollRunStatus.PaymentProcessing, PayrollRunStatus.Reversed],
        [PayrollRunStatus.PaymentProcessing] = [PayrollRunStatus.Paid, PayrollRunStatus.Approved],
        [PayrollRunStatus.Paid] = [PayrollRunStatus.Finalised],
        [PayrollRunStatus.Finalised] = [PayrollRunStatus.Reversed],
        [PayrollRunStatus.Reversed] = [],
    };

    public void Transition(PayrollRunStatus to)
    {
        if (!Allowed[Status].Contains(to))
            throw new InvalidStateException($"Payroll run {Reference} cannot move from {Status} to {to}.");
        Status = to;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void EnsureEditable()
    {
        if (IsLocked) throw new InvalidStateException($"Payroll run {Reference} is {Status} and cannot be modified. Use an adjustment or reversal.");
    }
}

/// <summary>Per-employee result snapshot. Immutable once the run is approved.</summary>
public class PayrollRunEmployee : LegalEntityScopedEntity
{
    public Guid PayrollRunId { get; set; }
    public Guid EmployeeId { get; set; }
    public required string EmployeeNumber { get; set; }
    public required string EmployeeName { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? CostCentreId { get; set; }
    public Guid? SalaryAssignmentId { get; set; }

    public decimal BasicPay { get; set; }
    public decimal GrossPay { get; set; }
    public decimal TaxableEarnings { get; set; }
    public decimal PensionableEarnings { get; set; }
    public decimal TaxablePay { get; set; }
    public decimal GrossTax { get; set; }
    public decimal TotalReliefs { get; set; }
    public decimal Paye { get; set; }
    public decimal Nssf { get; set; }
    public decimal Shif { get; set; }
    public decimal HousingLevy { get; set; }
    public decimal TotalStatutoryDeductions { get; set; }
    public decimal TotalOtherDeductions { get; set; }
    public decimal TotalEmployeeDeductions { get; set; }
    public decimal NetPay { get; set; }
    public decimal TotalEmployerContributions { get; set; }
    public decimal TotalEmployerCost { get; set; }

    /// <summary>Net pay in the previous finalised run for variance checks.</summary>
    public decimal? PreviousNetPay { get; set; }
    public bool HasErrors { get; set; }
    public List<PayrollValidationIssue> Issues { get; set; } = [];
    public List<Guid> StatutoryRuleVersionIds { get; set; } = [];

    public List<PayrollResultLineRecord> Lines { get; set; } = [];
}

public class PayrollResultLineRecord : TenantEntity
{
    public Guid PayrollRunEmployeeId { get; set; }
    public ResultLineType Type { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public decimal Amount { get; set; }
    public string? Working { get; set; }
    public Guid? StatutoryRuleVersionId { get; set; }
    public string? StatutoryRuleCode { get; set; }
    public Guid? ComponentId { get; set; }
    public string? GlAccountCode { get; set; }
    public bool Taxable { get; set; }
    public bool Pensionable { get; set; }
    public int Sequence { get; set; }

    public static PayrollResultLineRecord From(PayrollResultLine l, Guid tenantId, Guid runEmployeeId) => new()
    {
        TenantId = tenantId, PayrollRunEmployeeId = runEmployeeId, Type = l.Type, Code = l.Code, Name = l.Name, Amount = l.Amount,
        Working = l.Working, StatutoryRuleVersionId = l.StatutoryRuleVersionId, StatutoryRuleCode = l.StatutoryRuleCode,
        ComponentId = l.ComponentId, GlAccountCode = l.GlAccountCode, Taxable = l.Taxable, Pensionable = l.Pensionable, Sequence = l.Sequence,
    };
}

public enum ApprovalAction { Submitted, Reviewed, Approved, Rejected, Finalised, Reversed, PaymentGenerated, PaymentConfirmed }

public class PayrollApproval : LegalEntityScopedEntity
{
    public Guid PayrollRunId { get; set; }
    public ApprovalAction Action { get; set; }
    public Guid ActorId { get; set; }
    public required string ActorName { get; set; }
    public string? Comment { get; set; }
    public PayrollRunStatus FromStatus { get; set; }
    public PayrollRunStatus ToStatus { get; set; }
}

public enum PaymentStatus { Pending, FileGenerated, Sent, Confirmed, Failed, Cancelled }

/// <summary>A payment instruction for one employee from one run. Distinct from the approved payroll and from bank confirmation (PRD §7).</summary>
public class PayrollPayment : LegalEntityScopedEntity
{
    public Guid PayrollRunId { get; set; }
    public Guid PayrollRunEmployeeId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid? PaymentBatchId { get; set; }
    /// <summary>The employee account paid; null on payments made before this was recorded.</summary>
    public Guid? EmployeeBankAccountId { get; set; }
    public required string Reference { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "KES";
    public Employees.PaymentMethod Method { get; set; }
    public string? BankName { get; set; }
    public string? BankCode { get; set; }
    public string? BranchCode { get; set; }
    public string? AccountName { get; set; }
    /// <summary>Encrypted at rest.</summary>
    public string? AccountNumber { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public string? BankReference { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
}

public enum PaymentFileFormat { GenericCsv, KcbBulk, EquityBulk, CoopBulk, MpesaB2C }

public class PaymentBatch : LegalEntityScopedEntity
{
    public Guid PayrollRunId { get; set; }
    public required string Reference { get; set; }
    public PaymentFileFormat Format { get; set; }
    /// <summary>The company account the bank debits for this batch.</summary>
    public Guid? DebitAccountId { get; set; }
    public int PaymentCount { get; set; }
    public decimal TotalAmount { get; set; }
    public required string FileName { get; set; }
    public required string ContentHash { get; set; }
    public Guid GeneratedBy { get; set; }
    public DateOnly ValueDate { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.FileGenerated;
}

public class Payslip : LegalEntityScopedEntity
{
    public Guid PayrollRunId { get; set; }
    public Guid PayrollRunEmployeeId { get; set; }
    public Guid EmployeeId { get; set; }
    public required string PayslipNumber { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public decimal GrossPay { get; set; }
    public decimal NetPay { get; set; }
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Content hash of the rendered PDF so a stored document can be verified.</summary>
    public string? ContentHash { get; set; }
    public DateTimeOffset? FirstViewedAt { get; set; }
    /// <summary>
    /// NSSF contributions (employee + employer) up to and including this payslip: the opening balance plus every
    /// approved, unreversed run. Captured when the payslip is issued so it reprints the same. Null on payslips issued
    /// before this was recorded; those are worked out when printed.
    /// </summary>
    public decimal? NssfContributionsToDate { get; set; }
    /// <summary>Leave days remaining at the end of the pay period, captured when the payslip is issued.</summary>
    public List<PayslipLeaveBalance>? LeaveBalances { get; set; }
}

public sealed class PayslipLeaveBalance
{
    public required string LeaveType { get; set; }
    public decimal DaysRemaining { get; set; }
}

public enum AdjustmentType { Arrears, Overpayment, Correction, Backpay }
public enum AdjustmentStatus { Pending, Applied, Cancelled }

/// <summary>Controlled correction to an approved run, applied in a later run (PRD §5).</summary>
public class PayrollAdjustment : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public Guid OriginalRunId { get; set; }
    public Guid? AppliedInRunId { get; set; }
    public Guid SalaryComponentId { get; set; }
    public AdjustmentType Type { get; set; }
    public decimal Amount { get; set; }
    public required string Reason { get; set; }
    public AdjustmentStatus Status { get; set; } = AdjustmentStatus.Pending;
    public Guid RequestedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
}

public class PayrollAuditLog : TenantEntity
{
    public Guid? LegalEntityId { get; set; }
    public Guid? ActorId { get; set; }
    public string? ActorName { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
