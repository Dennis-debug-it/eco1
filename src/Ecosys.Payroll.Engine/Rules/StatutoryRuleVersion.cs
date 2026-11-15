namespace Ecosys.Payroll.Engine.Rules;

/// <summary>How a statutory rule turns a base amount into a contribution.</summary>
public enum StatutoryCalculationMethod
{
    /// <summary>Progressive tax bands (e.g. PAYE).</summary>
    ProgressiveBands,

    /// <summary>Contribution on earnings split into a lower tier (up to LEL) and an upper tier (LEL to UEL), e.g. NSSF.</summary>
    TieredContribution,

    /// <summary>Flat percentage of the base, optionally bounded by minimum/maximum amounts (e.g. SHIF, Housing Levy).</summary>
    Percentage,

    /// <summary>Fixed amount per employee per period (e.g. NITA).</summary>
    FixedAmount,

    /// <summary>Parameters only — consumed by another rule (e.g. pension/insurance relief limits).</summary>
    Parameters,
}

/// <summary>Which earnings figure a statutory rule is applied to.</summary>
public enum StatutoryBase
{
    GrossPay,
    TaxableEarnings,
    PensionableEarnings,
    TaxablePay,
}

public enum RuleApprovalStatus
{
    Draft,
    PendingApproval,
    Approved,
    Rejected,
    Retired,
}

public enum RoundingMode
{
    /// <summary>Round half away from zero (commercial rounding).</summary>
    HalfAwayFromZero,
    HalfToEven,
    Down,
    Up,
}

public sealed record RoundingRule(int Decimals = 2, RoundingMode Mode = RoundingMode.HalfAwayFromZero)
{
    public static readonly RoundingRule Default = new();

    public decimal Apply(decimal value)
    {
        if (Decimals < 0 || Decimals > 6) throw new InvalidOperationException($"Unsupported rounding precision {Decimals}.");
        return Mode switch
        {
            RoundingMode.HalfAwayFromZero => Math.Round(value, Decimals, MidpointRounding.AwayFromZero),
            RoundingMode.HalfToEven => Math.Round(value, Decimals, MidpointRounding.ToEven),
            RoundingMode.Down => Math.Round(value, Decimals, MidpointRounding.ToZero),
            RoundingMode.Up => Math.Round(value, Decimals, MidpointRounding.AwayFromZero) == value
                ? value
                : Math.Round(value, Decimals, MidpointRounding.ToPositiveInfinity),
            _ => throw new ArgumentOutOfRangeException(nameof(Mode)),
        };
    }
}

/// <summary>A progressive tax band. <see cref="UpperBound"/> null means unbounded.</summary>
public sealed record TaxBand(decimal LowerBound, decimal? UpperBound, decimal Rate);

/// <summary>
/// Method-specific parameters for a statutory rule version. Unused values stay null.
/// Persisted as JSON so new parameters can be introduced without schema changes.
/// </summary>
public sealed record StatutoryRuleParameters
{
    public IReadOnlyList<TaxBand> Bands { get; init; } = [];

    /// <summary>Monthly personal relief (PAYE).</summary>
    public decimal? PersonalRelief { get; init; }

    /// <summary>Insurance relief rate on qualifying premiums (PAYE).</summary>
    public decimal? InsuranceReliefRate { get; init; }

    /// <summary>Monthly cap on insurance relief (PAYE).</summary>
    public decimal? InsuranceReliefCap { get; init; }

    /// <summary>Monthly cap on allowable registered pension contributions deductible from taxable pay.</summary>
    public decimal? PensionDeductionCap { get; init; }

    /// <summary>Monthly cap on post-retirement medical fund contributions deductible from taxable pay.</summary>
    public decimal? PostRetirementMedicalCap { get; init; }

    /// <summary>Monthly cap on mortgage interest deductible from taxable pay.</summary>
    public decimal? MortgageInterestCap { get; init; }

    /// <summary>Lower earnings limit (NSSF Tier I ceiling).</summary>
    public decimal? LowerEarningsLimit { get; init; }

    /// <summary>Upper earnings limit (NSSF Tier II ceiling).</summary>
    public decimal? UpperEarningsLimit { get; init; }

    public decimal? EmployeeFixedAmount { get; init; }
    public decimal? EmployerFixedAmount { get; init; }

    /// <summary>Whether the employee portion reduces taxable pay before PAYE.</summary>
    public bool DeductibleFromTaxablePay { get; init; }

    /// <summary>Whether the employee portion counts towards the pension-deduction cap (e.g. NSSF).</summary>
    public bool CountsAsPensionContribution { get; init; }
}

/// <summary>
/// An immutable, effective-dated version of a statutory rule. Payroll results record the
/// <see cref="Id"/> of every version used so historical payrolls can be reproduced exactly.
/// </summary>
public sealed record StatutoryRuleVersion
{
    public required Guid Id { get; init; }

    /// <summary>Stable rule code, e.g. <c>KE.PAYE</c>.</summary>
    public required string RuleCode { get; init; }

    public required string Jurisdiction { get; init; }
    public required string Authority { get; init; }
    public required string Name { get; init; }
    public required int Version { get; init; }
    public required StatutoryCalculationMethod Method { get; init; }
    public StatutoryBase Base { get; init; } = StatutoryBase.GrossPay;

    public required DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }

    public decimal EmployeeRate { get; init; }
    public decimal EmployerRate { get; init; }
    public decimal? MinimumAmount { get; init; }
    public decimal? MaximumAmount { get; init; }

    public StatutoryRuleParameters Parameters { get; init; } = new();
    public RoundingRule Rounding { get; init; } = RoundingRule.Default;

    /// <summary>Employee categories the rule applies to; empty means all categories.</summary>
    public IReadOnlyList<string> ApplicableCategories { get; init; } = [];

    public RuleApprovalStatus ApprovalStatus { get; init; } = RuleApprovalStatus.Draft;

    /// <summary>Source reference (gazette notice, Act, circular) used to verify the parameters.</summary>
    public string? LegalReference { get; init; }

    public bool IsEffectiveOn(DateOnly date) =>
        date >= EffectiveFrom && (EffectiveTo is null || date <= EffectiveTo.Value);

    public bool AppliesToCategory(string? category) =>
        ApplicableCategories.Count == 0 ||
        (category is not null && ApplicableCategories.Contains(category, StringComparer.OrdinalIgnoreCase));
}
