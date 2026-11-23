using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Domain.Migration;

public enum ImportKind { Employees, OpeningBalances, YearToDate }

public enum ImportBatchStatus { Applied, Undone }

/// <summary>
/// One file loaded into a company while moving it from another payroll system. Remembers exactly what it created or
/// changed, so the whole load can be undone until payroll has used it. The file itself is not kept.
/// </summary>
public class ImportBatch : LegalEntityScopedEntity
{
    public ImportKind Kind { get; set; }
    public required string FileName { get; set; }
    public int RowCount { get; set; }
    public ImportBatchStatus Status { get; set; } = ImportBatchStatus.Applied;
    public string? AppliedByName { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }
    public string? UndoneByName { get; set; }
    /// <summary>What the load did, in counts (employees, bank accounts, loans…), for the import history.</summary>
    public Dictionary<string, int> Summary { get; set; } = [];
    public ImportRecords Records { get; set; } = new();
}

/// <summary>The records an import touched. Created rows are removed on undo; changed values are put back.</summary>
public sealed class ImportRecords
{
    public List<Guid> Employees { get; set; } = [];
    public List<Guid> Loans { get; set; } = [];
    public List<Guid> LeaveBalances { get; set; } = [];
    public List<Guid> HistoryMonths { get; set; } = [];
    public List<LeaveBalanceBefore> LeaveBalanceUpdates { get; set; } = [];
    public List<NssfOpeningBefore> NssfUpdates { get; set; } = [];
}

public sealed record LeaveBalanceBefore(Guid Id, decimal Entitlement, decimal Accrued, decimal Taken, decimal CarriedOver);

/// <summary><see cref="StatutoryId"/> null = the import created the statutory record, so undo removes it.</summary>
public sealed record NssfOpeningBefore(Guid EmployeeId, Guid? StatutoryId, decimal Previous);

/// <summary>
/// A month this tax year that the previous payroll system paid, per employee, carried in so the P9 tax deduction card and
/// the annual summary cover the whole year after a mid-year switch. Imported figures are never recalculated; a month
/// that Ecosys has finalised can't also be imported.
/// </summary>
public class PayrollHistoryMonth : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Gross { get; set; }
    public decimal TaxableEarnings { get; set; }
    public decimal AllowableDeductions { get; set; }
    public decimal TaxablePay { get; set; }
    public decimal GrossTax { get; set; }
    public decimal Reliefs { get; set; }
    public decimal Paye { get; set; }
    public decimal Nssf { get; set; }
    public decimal Shif { get; set; }
    public decimal HousingLevy { get; set; }
    public decimal NetPay { get; set; }
    public Guid ImportBatchId { get; set; }
}
