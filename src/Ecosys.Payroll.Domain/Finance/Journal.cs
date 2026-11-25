using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Domain.Finance;

public enum JournalStatus { Draft, Posted, Reversed }

/// <summary>Accounting journal generated from a finalised run. Balanced by construction.</summary>
public class PayrollJournal : LegalEntityScopedEntity
{
    /// <summary>Null for non-payroll journals (leave provision, training cost).</summary>
    public Guid? PayrollRunId { get; set; }
    /// <summary>Payroll, LeaveProvision, TrainingCost, Expenses.</summary>
    public string Source { get; set; } = "Payroll";
    public string? Narrative { get; set; }
    public required string Reference { get; set; }
    public DateOnly PostingDate { get; set; }
    public string CurrencyCode { get; set; } = "KES";
    public JournalStatus Status { get; set; } = JournalStatus.Draft;
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public Guid? ReversalOfJournalId { get; set; }
    public string? ExternalReference { get; set; }
    public List<PayrollJournalLine> Lines { get; set; } = [];

    public bool IsBalanced => TotalDebit == TotalCredit;
}

public class PayrollJournalLine : TenantEntity
{
    public Guid PayrollJournalId { get; set; }
    public required string AccountCode { get; set; }
    public required string Description { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public Guid? CostCentreId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? BranchId { get; set; }
    public int Sequence { get; set; }
}

/// <summary>Maps payroll result codes (component or statutory code) to GL accounts.</summary>
public class GlAccountMapping : LegalEntityScopedEntity
{
    public required string SourceCode { get; set; }
    public required string ExpenseAccount { get; set; }
    public string? LiabilityAccount { get; set; }
    public string? Description { get; set; }
}
