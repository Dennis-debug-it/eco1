using Ecosys.Payroll.Domain.Compliance;
using Ecosys.Payroll.Engine;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Infrastructure.Services;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record RuleDecision(string? Note);

/// <summary>Ad-hoc "what would this employee's pay be" — the engine exposed for simulations and parallel-run checks.</summary>
public sealed record SimulateRequest(string Jurisdiction, DateOnly AsOf, decimal BasicSalary, List<EarningInput>? OtherEarnings, List<DeductionInput>? Deductions, bool HasDisabilityExemption = false, decimal DisabilityExemptAmount = 0);

public static class StatutoryEndpoints
{
    public static void MapStatutory(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/statutory-rules").RequireAuthorization();

        g.MapGet("", async (string? jurisdiction, string? ruleCode, StatutoryRuleService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(jurisdiction, ruleCode, ct)));

        g.MapGet("/effective", async (string jurisdiction, DateOnly asOf, StatutoryRuleService svc, CancellationToken ct) =>
        {
            var set = await svc.ResolveAsync(jurisdiction, asOf, ct);
            return Results.Ok(set.Rules.OrderBy(r => r.RuleCode));
        });

        g.MapPost("", async (StatutoryRuleVersionRecord body, StatutoryRuleService svc, CancellationToken ct) =>
        {
            var created = await svc.CreateDraftAsync(body, ct);
            return Results.Created($"/api/payroll/statutory-rules/{created.Id}", created);
        });
        g.MapPost("/{id:guid}/submit", async (Guid id, StatutoryRuleService svc, CancellationToken ct) => Results.Ok(await svc.SubmitAsync(id, ct)));
        g.MapPost("/{id:guid}/approve", async (Guid id, RuleDecision? d, StatutoryRuleService svc, CancellationToken ct) => Results.Ok(await svc.ApproveAsync(id, d?.Note, ct)));
        g.MapPost("/{id:guid}/reject", async (Guid id, RuleDecision d, StatutoryRuleService svc, CancellationToken ct) => Results.Ok(await svc.RejectAsync(id, d.Note ?? "Rejected", ct)));

        g.MapPost("/simulate", async (SimulateRequest req, StatutoryRuleService svc, PayrollCalculatorRegistry calculators, CancellationToken ct) =>
        {
            var rules = await svc.ResolveAsync(req.Jurisdiction, req.AsOf, ct);
            var earnings = new List<EarningInput> { new("BASIC", "Basic salary", req.BasicSalary, true, true, true) };
            earnings.AddRange(req.OtherEarnings ?? []);
            var input = new EmployeePayrollInput
            {
                EmployeeId = Guid.Empty, EmployeeNumber = "SIM", DisplayName = "Simulation", Earnings = earnings, Deductions = req.Deductions ?? [],
                HasDisabilityExemption = req.HasDisabilityExemption, DisabilityExemptAmount = req.DisabilityExemptAmount,
            };
            var period = new PayrollPeriodInput(Guid.Empty, new DateOnly(req.AsOf.Year, req.AsOf.Month, 1), req.AsOf, req.Jurisdiction, "KES");
            return Results.Ok(calculators.For(req.Jurisdiction).Calculate(input, period, rules));
        });
    }
}
