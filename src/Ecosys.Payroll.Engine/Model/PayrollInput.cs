namespace Ecosys.Payroll.Engine.Model;

public enum ComponentKind
{
    Earning,
    Deduction,
}

/// <summary>Classification of a deduction for statutory-relief purposes.</summary>
public enum DeductionTreatment
{
    /// <summary>Ordinary after-tax deduction (loan, advance, union dues, …).</summary>
    Ordinary,

    /// <summary>Contribution to a registered pension/provident scheme — deductible from taxable pay within statutory caps.</summary>
    RegisteredPension,

    /// <summary>Post-retirement medical fund contribution — deductible within statutory caps.</summary>
    PostRetirementMedicalFund,

    /// <summary>Owner-occupier mortgage interest — deductible within statutory caps.</summary>
    MortgageInterest,

    /// <summary>Life/health/education insurance premium qualifying for insurance relief.</summary>
    QualifyingInsurancePremium,
}

/// <summary>One earning line supplied to the engine (recurring or variable).</summary>
public sealed record EarningInput(
    string Code,
    string Name,
    decimal Amount,
    bool Taxable = true,
    bool Pensionable = false,
    bool IsBasic = false,
    Guid? ComponentId = null,
    string? GlAccountCode = null);

/// <summary>One non-statutory deduction line supplied to the engine.</summary>
public sealed record DeductionInput(
    string Code,
    string Name,
    decimal Amount,
    DeductionTreatment Treatment = DeductionTreatment.Ordinary,
    decimal EmployerAmount = 0m,
    Guid? ComponentId = null,
    string? GlAccountCode = null);

/// <summary>
/// Everything the engine needs to compute one employee's pay for one period.
/// The engine never reads from a database — the application layer assembles this.
/// </summary>
public sealed record EmployeePayrollInput
{
    public required Guid EmployeeId { get; init; }
    public required string EmployeeNumber { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Employee category used for statutory applicability (e.g. "Permanent", "Casual", "Expatriate").</summary>
    public string? Category { get; init; }

    /// <summary>Whether the employee has a certified disability exemption (KRA exemption certificate).</summary>
    public bool HasDisabilityExemption { get; init; }

    /// <summary>Monthly exempt amount under the disability exemption certificate, if any.</summary>
    public decimal DisabilityExemptAmount { get; init; }

    /// <summary>Statutory rule codes the employee is exempt from (e.g. expatriates exempt from NSSF). Logged on the result.</summary>
    public IReadOnlyList<string> StatutoryExemptions { get; init; } = [];

    public IReadOnlyList<EarningInput> Earnings { get; init; } = [];
    public IReadOnlyList<DeductionInput> Deductions { get; init; } = [];

    /// <summary>Unpaid-leave or absence deduction already expressed as an amount; reduces gross and taxable pay.</summary>
    public decimal UnpaidLeaveDeduction { get; init; }
}

public enum PeriodFrequency { Monthly, SemiMonthly, Weekly }

public sealed record PayrollPeriodInput(
    Guid PayrollPeriodId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Jurisdiction,
    string CurrencyCode,
    PeriodFrequency Frequency = PeriodFrequency.Monthly)
{
    /// <summary>Factor converting monthly statutory thresholds to this period's length.</summary>
    public decimal MonthlyScaleFactor => Frequency switch
    {
        PeriodFrequency.Weekly => 12m / 52m,
        PeriodFrequency.SemiMonthly => 0.5m,
        _ => 1m,
    };

    /// <summary>Statutory rules are selected as effective on the period end date.</summary>
    public DateOnly RuleDate => PeriodEnd;
}
