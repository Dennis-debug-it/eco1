using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Engine.Model;

namespace Ecosys.Payroll.Domain.Compensation;

public enum ComponentType { Earning, Deduction }

public enum CalculationMethod
{
    FixedAmount,
    PercentOfBasic,
    PercentOfGross,
    /// <summary>Amount supplied per run (overtime, commission, bonus).</summary>
    Variable,
}

public enum Recurrence { EveryPeriod, OneOff }

/// <summary>How basic pay is earned: a monthly salary, or a daily wage paid for the days actually worked.</summary>
public enum PayBasis { Monthly, Daily }

/// <summary>Configurable earning or deduction definition (PRD §3.3).</summary>
public class SalaryComponent : LegalEntityScopedEntity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public ComponentType Type { get; set; }
    public CalculationMethod Method { get; set; } = CalculationMethod.FixedAmount;
    public decimal? DefaultAmount { get; set; }
    public decimal? DefaultRate { get; set; }
    public bool Taxable { get; set; } = true;
    public bool Pensionable { get; set; }
    public bool IsBasic { get; set; }
    public DeductionTreatment DeductionTreatment { get; set; } = DeductionTreatment.Ordinary;
    /// <summary>Employer-side matching contribution rate for deductions (e.g. pension 1:1 = 1.0).</summary>
    public decimal EmployerMatchRate { get; set; }
    public Recurrence Recurrence { get; set; } = Recurrence.EveryPeriod;
    public string? GlAccountCode { get; set; }
    public string? LiabilityGlAccountCode { get; set; }
    public bool ShowOnPayslip { get; set; } = true;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A reusable bundle of components (e.g. "Management grade", "Field staff").</summary>
public class SalaryStructure : LegalEntityScopedEntity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public List<SalaryStructureLine> Lines { get; set; } = [];
}

public class SalaryStructureLine : TenantEntity
{
    public Guid SalaryStructureId { get; set; }
    public Guid SalaryComponentId { get; set; }
    public CalculationMethod Method { get; set; }
    public decimal? Amount { get; set; }
    public decimal? Rate { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Effective-dated remuneration for an employee. Only one assignment may be effective on a date.</summary>
public class EmployeeSalaryAssignment : LegalEntityScopedEntity, IEffectiveDated
{
    public Guid EmployeeId { get; set; }
    public Guid? SalaryStructureId { get; set; }
    /// <summary>
    /// Monthly basic salary. For a daily-paid worker this is the monthly equivalent (daily rate x the company's standard
    /// working days), kept so notice pay, final dues, reviews and reports still have a monthly figure; the payroll run
    /// pays the daily rate for the days worked instead.
    /// </summary>
    public decimal BasicSalary { get; set; }
    public PayBasis PayBasis { get; set; } = PayBasis.Monthly;
    /// <summary>The daily wage, when <see cref="PayBasis"/> is Daily.</summary>
    public decimal? DailyRate { get; set; }
    public string CurrencyCode { get; set; } = "KES";
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string? ChangeReason { get; set; }
    public Guid? ApprovedBy { get; set; }
    /// <summary>Per-employee overrides/additions beyond the structure.</summary>
    public List<EmployeeSalaryAssignmentLine> Lines { get; set; } = [];
}

public class EmployeeSalaryAssignmentLine : TenantEntity
{
    public Guid EmployeeSalaryAssignmentId { get; set; }
    public Guid SalaryComponentId { get; set; }
    public CalculationMethod Method { get; set; }
    public decimal? Amount { get; set; }
    public decimal? Rate { get; set; }
}

/// <summary>Variable input captured for one run (overtime hours already costed, commission, one-off deduction).</summary>
public class PayrollVariableInput : LegalEntityScopedEntity
{
    public Guid PayrollRunId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid SalaryComponentId { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}

public enum LoanStatus { Active, Settled, WrittenOff, Suspended }

/// <summary>Staff loan or salary advance recovered through payroll.</summary>
public class StaffLoan : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public Guid SalaryComponentId { get; set; }
    public required string Reference { get; set; }
    public decimal Principal { get; set; }
    public decimal Balance { get; set; }
    public decimal InstalmentAmount { get; set; }
    public DateOnly StartDate { get; set; }
    public LoanStatus Status { get; set; } = LoanStatus.Active;
    public string? Notes { get; set; }
}
