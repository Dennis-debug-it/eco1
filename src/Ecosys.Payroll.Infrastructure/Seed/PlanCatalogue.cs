using Ecosys.Payroll.Domain.Billing;
using Ecosys.Payroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Seed;

/// <summary>
/// Starter price list, created once when SaaS mode is first switched on. Prices are placeholders in KES excluding VAT:
/// the operator sets real prices in the operator console. Later edits are never overwritten by this seed.
/// </summary>
public static class PlanCatalogue
{
    public static IEnumerable<Plan> Defaults()
    {
        yield return new Plan
        {
            Code = "starter", Name = "Starter", SortOrder = 1, Description = "Payroll, statutory returns, leave and employee self-service for one company.",
            MonthlyBaseFee = 2_500m, IncludedEmployees = 10, PricePerAdditionalEmployee = 150m, MaxEmployees = 50, MaxLegalEntities = 1, Features = [],
        };
        yield return new Plan
        {
            Code = "growth", Name = "Growth", SortOrder = 2, Description = "Everything in Starter plus the HR suite (recruitment, performance, learning, cases, benefits and assets) and the API for connecting other systems. Up to 3 companies.",
            MonthlyBaseFee = 7_500m, IncludedEmployees = 25, PricePerAdditionalEmployee = 200m, MaxEmployees = 500, MaxLegalEntities = 3, Features = [PlanFeatures.HrSuite, PlanFeatures.Api],
        };
        yield return new Plan
        {
            Code = "enterprise", Name = "Enterprise", SortOrder = 3, IsPublic = false, Description = "Unlimited employees and companies, negotiated pricing. Assigned by the operator.",
            MonthlyBaseFee = 0m, IncludedEmployees = 0, PricePerAdditionalEmployee = 0m, MaxEmployees = null, MaxLegalEntities = null, Features = [PlanFeatures.HrSuite, PlanFeatures.Api],
        };
    }

    public static async Task<int> EnsureAsync(PayrollDbContext db, CancellationToken ct = default)
    {
        if (await db.Plans.IgnoreQueryFilters().AnyAsync(ct)) return 0;
        var plans = Defaults().ToList();
        db.Plans.AddRange(plans);
        await db.SaveChangesAsync(ct);
        return plans.Count;
    }
}
