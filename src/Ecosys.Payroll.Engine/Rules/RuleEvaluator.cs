namespace Ecosys.Payroll.Engine.Rules;

/// <summary>Employee and employer amounts produced by one statutory rule, with a human-readable working.</summary>
public sealed record RuleOutcome(decimal Employee, decimal Employer, string Working);

/// <summary>A tiered contribution split into its tiers (NSSF Tier I up to the LEL, Tier II from the LEL to the UEL), each rounded as the rule rounds.</summary>
public sealed record TierSplit(decimal Tier1Base, decimal Tier2Base, decimal Tier1Employee, decimal Tier1Employer, decimal Tier2Employee, decimal Tier2Employer, decimal Lel, decimal Uel);

/// <summary>Pure, stateless evaluation of the generic calculation methods.</summary>
public static class RuleEvaluator
{
    public static RuleOutcome Evaluate(StatutoryRuleVersion rule, decimal baseAmount) => rule.Method switch
    {
        StatutoryCalculationMethod.Percentage => Percentage(rule, baseAmount),
        StatutoryCalculationMethod.TieredContribution => Tiered(rule, baseAmount),
        StatutoryCalculationMethod.FixedAmount => Fixed(rule),
        StatutoryCalculationMethod.ProgressiveBands => Progressive(rule, baseAmount),
        _ => throw new StatutoryConfigurationException($"Rule {rule.RuleCode} ({rule.Method}) cannot be evaluated directly."),
    };

    private static RuleOutcome Percentage(StatutoryRuleVersion rule, decimal baseAmount)
    {
        var basis = Math.Max(0m, baseAmount);
        var employee = Bound(rule.Rounding.Apply(basis * rule.EmployeeRate), rule.MinimumAmount, rule.MaximumAmount, rule.EmployeeRate);
        var employer = rule.Rounding.Apply(basis * rule.EmployerRate);
        var working = $"{Pct(rule.EmployeeRate)} x {basis:N2}";
        if (rule.MinimumAmount is { } min && rule.EmployeeRate > 0) working += $", min {min:N2}";
        if (rule.MaximumAmount is { } max && rule.EmployeeRate > 0) working += $", max {max:N2}";
        if (rule.EmployerRate > 0) working += $"; employer {Pct(rule.EmployerRate)} x {basis:N2}";
        return new RuleOutcome(employee, employer, working);
    }

    private static decimal Bound(decimal amount, decimal? min, decimal? max, decimal rate)
    {
        if (rate == 0) return amount;
        if (min is { } lo && amount < lo) amount = lo;
        if (max is { } hi && amount > hi) amount = hi;
        return amount;
    }

    private static RuleOutcome Tiered(StatutoryRuleVersion rule, decimal baseAmount)
    {
        var t = Tiers(rule, baseAmount);
        var working = $"Tier I {Pct(rule.EmployeeRate)} x {t.Tier1Base:N2}; Tier II {Pct(rule.EmployeeRate)} x {t.Tier2Base:N2} (LEL {t.Lel:N0}, UEL {t.Uel:N0})";
        return new RuleOutcome(t.Tier1Employee + t.Tier2Employee, t.Tier1Employer + t.Tier2Employer, working);
    }

    /// <summary>
    /// The tiers of a tiered contribution. Returns files that report Tier I and Tier II on separate rows (NSSF) use this,
    /// so the split always adds back to exactly what the payroll deducted.
    /// </summary>
    public static TierSplit Tiers(StatutoryRuleVersion rule, decimal baseAmount)
    {
        var lel = rule.Parameters.LowerEarningsLimit
            ?? throw new StatutoryConfigurationException($"Rule {rule.RuleCode} v{rule.Version} has no lower earnings limit.");
        var uel = rule.Parameters.UpperEarningsLimit
            ?? throw new StatutoryConfigurationException($"Rule {rule.RuleCode} v{rule.Version} has no upper earnings limit.");
        if (uel < lel) throw new StatutoryConfigurationException($"Rule {rule.RuleCode} v{rule.Version}: UEL is below LEL.");

        var basis = Math.Max(0m, baseAmount);
        var tier1Base = Math.Min(basis, lel);
        var tier2Base = Math.Max(0m, Math.Min(basis, uel) - lel);
        return new TierSplit(tier1Base, tier2Base,
            rule.Rounding.Apply(tier1Base * rule.EmployeeRate), rule.Rounding.Apply(tier1Base * rule.EmployerRate),
            rule.Rounding.Apply(tier2Base * rule.EmployeeRate), rule.Rounding.Apply(tier2Base * rule.EmployerRate), lel, uel);
    }

    private static RuleOutcome Fixed(StatutoryRuleVersion rule)
    {
        var employee = rule.Parameters.EmployeeFixedAmount ?? 0m;
        var employer = rule.Parameters.EmployerFixedAmount ?? 0m;
        return new RuleOutcome(employee, employer, $"Fixed: employee {employee:N2}, employer {employer:N2}");
    }

    private static RuleOutcome Progressive(StatutoryRuleVersion rule, decimal taxable)
    {
        var tax = ProgressiveTax(rule.Parameters.Bands, taxable, out var working);
        return new RuleOutcome(rule.Rounding.Apply(tax), 0m, working);
    }

    /// <summary>Applies progressive bands. Bands must be contiguous and ordered; this is validated.</summary>
    public static decimal ProgressiveTax(IReadOnlyList<TaxBand> bands, decimal taxable, out string working)
    {
        ValidateBands(bands);
        var parts = new List<string>();
        var tax = 0m;
        var amount = Math.Max(0m, taxable);
        foreach (var band in bands)
        {
            if (amount <= band.LowerBound) break;
            var top = band.UpperBound is { } ub ? Math.Min(amount, ub) : amount;
            var slice = top - band.LowerBound;
            if (slice <= 0) continue;
            var bandTax = slice * band.Rate;
            tax += bandTax;
            parts.Add($"{slice:N2} @ {Pct(band.Rate)} = {bandTax:N2}");
        }
        working = parts.Count == 0 ? "No taxable pay" : string.Join("; ", parts);
        return tax;
    }

    public static void ValidateBands(IReadOnlyList<TaxBand> bands)
    {
        if (bands.Count == 0) throw new StatutoryConfigurationException("Progressive rule has no tax bands.");
        if (bands[0].LowerBound != 0) throw new StatutoryConfigurationException("The first tax band must start at zero.");
        for (var i = 0; i < bands.Count; i++)
        {
            var b = bands[i];
            if (b.Rate < 0 || b.Rate > 1) throw new StatutoryConfigurationException($"Band {i + 1} has an invalid rate {b.Rate}.");
            if (b.UpperBound is { } ub && ub <= b.LowerBound) throw new StatutoryConfigurationException($"Band {i + 1} upper bound must exceed its lower bound.");
            if (i < bands.Count - 1)
            {
                if (b.UpperBound is null) throw new StatutoryConfigurationException("Only the last tax band may be unbounded.");
                if (bands[i + 1].LowerBound != b.UpperBound) throw new StatutoryConfigurationException($"Tax bands {i + 1} and {i + 2} are not contiguous.");
            }
        }
    }

    public static string Pct(decimal rate) => $"{rate * 100:0.###}%";
}
