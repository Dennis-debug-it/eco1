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

    public StatutoryRuleVersion? Find(string ruleCode) => _rules.GetValueOrDefault(ruleCode);

    public StatutoryRuleVersion Require(string ruleCode) =>
        Find(ruleCode) ?? throw new StatutoryConfigurationException(
            $"No approved statutory rule {ruleCode} is effective on {AsOf:yyyy-MM-dd} for {Jurisdiction}.");
}

public sealed class StatutoryConfigurationException(string message) : Exception(message);
