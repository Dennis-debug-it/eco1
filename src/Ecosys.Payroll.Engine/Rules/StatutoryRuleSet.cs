namespace Ecosys.Payroll.Engine.Rules;

/// <summary>
/// The approved statutory rules in force for one jurisdiction on one date.
/// Built once per payroll run and shared across every employee in it.
/// </summary>
public sealed class StatutoryRuleSet
{
    private readonly Dictionary<string, StatutoryRuleVersion> _rules;

    private StatutoryRuleSet(string jurisdiction, DateOnly asOf, Dictionary<string, StatutoryRuleVersion> rules)
    {
        Jurisdiction = jurisdiction;
        AsOf = asOf;
        _rules = rules;
    }

    public string Jurisdiction { get; }
    public DateOnly AsOf { get; }
    public IReadOnlyCollection<StatutoryRuleVersion> Rules => _rules.Values;

    /// <summary>
    /// Selects, for each rule code, the single approved version effective on <paramref name="asOf"/>.
    /// Overlapping approved versions are a configuration error and are rejected rather than guessed at.
    /// </summary>
    public static StatutoryRuleSet Resolve(string jurisdiction, DateOnly asOf, IEnumerable<StatutoryRuleVersion> candidates)
    {
        var selected = new Dictionary<string, StatutoryRuleVersion>(StringComparer.OrdinalIgnoreCase);
        var effective = candidates.Where(r =>
            string.Equals(r.Jurisdiction, jurisdiction, StringComparison.OrdinalIgnoreCase) &&
            r.ApprovalStatus == RuleApprovalStatus.Approved &&
            r.IsEffectiveOn(asOf));

        foreach (var group in effective.GroupBy(r => r.RuleCode, StringComparer.OrdinalIgnoreCase))
        {
            var versions = group.ToList();
            if (versions.Count > 1)
            {
                throw new StatutoryConfigurationException(
                    $"Rule {group.Key} has {versions.Count} approved versions effective on {asOf:yyyy-MM-dd} " +
                    $"(versions {string.Join(", ", versions.Select(v => v.Version))}). Effective periods must not overlap.");
            }
            selected[group.Key] = versions[0];
        }

        return new StatutoryRuleSet(jurisdiction, asOf, selected);
    }

    /// <summary>
    /// Kenyan statutory parameters are monthly. For weekly or semi-monthly payrolls every monetary threshold
    /// (bands, reliefs, caps, LEL/UEL, minimums, fixed levies) is scaled by <paramref name="factor"/>
    /// (= 12 / periods per year) while rates are unchanged. Rule ids are preserved for audit.
    /// </summary>
    public StatutoryRuleSet Scale(decimal factor)
    {
        if (factor == 1m) return this;
        if (factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        decimal? S(decimal? v) => v is null ? null : Math.Round(v.Value * factor, 4);
        var scaled = _rules.ToDictionary(kv => kv.Key, kv =>
        {
            var r = kv.Value;
            var p = r.Parameters;
            return r with
            {
                MinimumAmount = S(r.MinimumAmount),
                MaximumAmount = S(r.MaximumAmount),
                Parameters = p with
                {
                    Bands = p.Bands.Select(b => b with { LowerBound = Math.Round(b.LowerBound * factor, 4), UpperBound = S(b.UpperBound) }).ToList(),
                    PersonalRelief = S(p.PersonalRelief),
                    InsuranceReliefCap = S(p.InsuranceReliefCap),
                    PensionDeductionCap = S(p.PensionDeductionCap),
                    PostRetirementMedicalCap = S(p.PostRetirementMedicalCap),
                    MortgageInterestCap = S(p.MortgageInterestCap),
                    LowerEarningsLimit = S(p.LowerEarningsLimit),
                    UpperEarningsLimit = S(p.UpperEarningsLimit),
                    EmployeeFixedAmount = S(p.EmployeeFixedAmount),
                    EmployerFixedAmount = S(p.EmployerFixedAmount),
                },
            };
        }, StringComparer.OrdinalIgnoreCase);
        return new StatutoryRuleSet(Jurisdiction, AsOf, scaled) { ScaleFactor = factor };
    }

    /// <summary>1 for monthly payrolls; 12/52 for weekly; 0.5 for semi-monthly.</summary>
    public decimal ScaleFactor { get; private init; } = 1m;

    public StatutoryRuleVersion? Find(string ruleCode) => _rules.GetValueOrDefault(ruleCode);

    public StatutoryRuleVersion Require(string ruleCode) =>
        Find(ruleCode) ?? throw new StatutoryConfigurationException(
            $"No approved statutory rule {ruleCode} is effective on {AsOf:yyyy-MM-dd} for {Jurisdiction}.");
}

public sealed class StatutoryConfigurationException(string message) : Exception(message);
