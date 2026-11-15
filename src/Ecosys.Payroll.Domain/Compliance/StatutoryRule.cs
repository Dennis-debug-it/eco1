using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Engine.Rules;

namespace Ecosys.Payroll.Domain.Compliance;

/// <summary>
/// Persisted statutory rule version. Platform-level rules (TenantId null) are shared by every tenant;
/// a tenant may add its own versions (e.g. an approved private pension scheme) which take precedence.
/// Approved versions are immutable — a change is a new version.
/// </summary>
public class StatutoryRuleVersionRecord : Entity
{
    public Guid? TenantId { get; set; }
    public required string RuleCode { get; set; }
    public required string Jurisdiction { get; set; }
    public required string Authority { get; set; }
    public required string Name { get; set; }
    public int Version { get; set; }
    public StatutoryCalculationMethod Method { get; set; }
    public StatutoryBase Base { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public decimal EmployeeRate { get; set; }
    public decimal EmployerRate { get; set; }
    public decimal? MinimumAmount { get; set; }
    public decimal? MaximumAmount { get; set; }
    public StatutoryRuleParameters Parameters { get; set; } = new();
    public RoundingRule Rounding { get; set; } = RoundingRule.Default;
    public List<string> ApplicableCategories { get; set; } = [];
    public RuleApprovalStatus ApprovalStatus { get; set; } = RuleApprovalStatus.Draft;
    public string? LegalReference { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovalNote { get; set; }

    public StatutoryRuleVersion ToEngine() => new()
    {
        Id = Id, RuleCode = RuleCode, Jurisdiction = Jurisdiction, Authority = Authority, Name = Name, Version = Version,
        Method = Method, Base = Base, EffectiveFrom = EffectiveFrom, EffectiveTo = EffectiveTo,
        EmployeeRate = EmployeeRate, EmployerRate = EmployerRate, MinimumAmount = MinimumAmount, MaximumAmount = MaximumAmount,
        Parameters = Parameters, Rounding = Rounding, ApplicableCategories = ApplicableCategories,
        ApprovalStatus = ApprovalStatus, LegalReference = LegalReference,
    };

    public static StatutoryRuleVersionRecord FromEngine(StatutoryRuleVersion r, Guid? tenantId = null) => new()
    {
        Id = r.Id, TenantId = tenantId, RuleCode = r.RuleCode, Jurisdiction = r.Jurisdiction, Authority = r.Authority, Name = r.Name,
        Version = r.Version, Method = r.Method, Base = r.Base, EffectiveFrom = r.EffectiveFrom, EffectiveTo = r.EffectiveTo,
        EmployeeRate = r.EmployeeRate, EmployerRate = r.EmployerRate, MinimumAmount = r.MinimumAmount, MaximumAmount = r.MaximumAmount,
        Parameters = r.Parameters, Rounding = r.Rounding, ApplicableCategories = r.ApplicableCategories.ToList(),
        ApprovalStatus = r.ApprovalStatus, LegalReference = r.LegalReference,
    };
}
