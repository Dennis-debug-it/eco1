using Ecosys.Payroll.Domain.Billing;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Engine.Rules;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Ecosys.Payroll.Infrastructure.Services.Saas;

/// <summary>What the caller's tenant may do under its plan. <see cref="Managed"/> tenants (no subscription, or SaaS off) are unrestricted.</summary>
public sealed record Entitlements(bool Managed, SubscriptionStatus? Status, string? PlanCode, string? PlanName, IReadOnlyList<string> Features,
    int? MaxEmployees, int? MaxLegalEntities, DateOnly? TrialEndsOn, bool Activated, bool CancelAtPeriodEnd, string? SuspensionReason)
{
    public static readonly Entitlements Unrestricted = new(true, null, null, null, PlanFeatures.All, null, null, null, true, false, null);
    public bool ReadOnly => Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled;
    public bool Has(string feature) => Managed || Features.Contains(feature);
}

/// <summary>Pure invoice arithmetic, so prices can be checked by hand: base fee covers the included employees, each one above is charged, then VAT.</summary>
public static class InvoiceCalculator
{
    public static (List<InvoiceLine> Lines, decimal Subtotal, decimal Vat, decimal Total) Calculate(Plan plan, int headcount, decimal vatRate, DateOnly periodStart, DateOnly periodEnd)
    {
        var r = RoundingRule.Default;
        var lines = new List<InvoiceLine>
        {
            new() { Description = $"{plan.Name} plan, {periodStart:d MMM yyyy} – {periodEnd:d MMM yyyy} (includes {plan.IncludedEmployees} employees)", Quantity = 1, UnitPrice = plan.MonthlyBaseFee, Amount = r.Apply(plan.MonthlyBaseFee) },
        };
        var extra = Math.Max(0, headcount - plan.IncludedEmployees);
        if (extra > 0)
            lines.Add(new InvoiceLine { Description = $"Additional employees ({headcount} on the books)", Quantity = extra, UnitPrice = plan.PricePerAdditionalEmployee, Amount = r.Apply(extra * plan.PricePerAdditionalEmployee) });
        var subtotal = lines.Sum(l => l.Amount);
        var vat = r.Apply(subtotal * vatRate);
        return (lines, subtotal, vat, subtotal + vat);
    }
}

/// <summary>
/// The caller's own subscription: entitlement checks used by the API, and the customer's billing actions
/// (activate, change plan, cancel). Cross-tenant work (billing runs, payments, operator actions) is in <see cref="BillingService"/>.
/// </summary>
public sealed class SubscriptionService(PayrollDbContext db, ITenantContext tenant, AuditService audit, IConfiguration config)
{
    private readonly SaasOptions _options = SaasOptions.From(config);

    /// <summary>Employees that count towards plan limits and per-employee pricing: everyone on the books who has not left.</summary>
    public static readonly EmploymentStatus[] CountedStatuses = [EmploymentStatus.Active, EmploymentStatus.OnLeave, EmploymentStatus.Suspended];

    public async Task<Entitlements> GetEntitlementsAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (!_options.Enabled) return Entitlements.Unrestricted;
        var was = db.IgnoreTenantFilter;
        db.IgnoreTenantFilter = true;
        try
        {
            var row = await db.Subscriptions.Where(s => s.TenantId == tenantId)
                .Join(db.Plans.IgnoreQueryFilters(), s => s.PlanId, p => p.Id, (s, p) => new { s, p }).AsNoTracking().FirstOrDefaultAsync(ct);
            if (row is null) return Entitlements.Unrestricted;
            return new Entitlements(false, row.s.Status, row.p.Code, row.p.Name, row.p.Features, row.p.MaxEmployees, row.p.MaxLegalEntities,
                row.s.TrialEndsOn, row.s.ActivatedAt is not null, row.s.CancelAtPeriodEnd, row.s.SuspensionReason);
        }
        finally { db.IgnoreTenantFilter = was; }
    }

    public Task<int> HeadcountAsync(CancellationToken ct = default) => db.Employees.CountAsync(e => CountedStatuses.Contains(e.Status), ct);

    /// <summary>Call before adding employees (or returning a leaver to the books).</summary>
    public async Task EnsureCanAddEmployeesAsync(int adding = 1, CancellationToken ct = default)
    {
        var ent = await GetEntitlementsAsync(tenant.RequireTenant(), ct);
        if (ent.Managed || ent.MaxEmployees is not { } max) return;
        var now = await HeadcountAsync(ct);
        if (now + adding > max)
            throw new PlanLimitException($"Your {ent.PlanName} plan covers up to {max} employees and you have {now}. Upgrade your plan under Billing to add more.");
    }

    public async Task EnsureCanAddLegalEntityAsync(CancellationToken ct = default)
    {
        var ent = await GetEntitlementsAsync(tenant.RequireTenant(), ct);
        if (ent.Managed || ent.MaxLegalEntities is not { } max) return;
        var now = await db.LegalEntities.CountAsync(ct);
        if (now + 1 > max)
            throw new PlanLimitException($"Your {ent.PlanName} plan covers {max} {(max == 1 ? "company" : "companies")}. Upgrade your plan under Billing to add another.");
    }

    // ------------------------------------------------------------------ Customer actions

    public async Task<Subscription> RequireOwnAsync(CancellationToken ct)
    {
        if (!_options.Enabled) throw new DomainException("Billing is not enabled on this server.", "NOT_FOUND");
        return await db.Subscriptions.FirstOrDefaultAsync(ct)
            ?? throw new DomainException("Your organisation is on a managed contract. Contact your account manager about billing.", "NOT_FOUND");
    }

    /// <summary>
    /// Confirms billing details. During a trial, billing starts when the trial ends. A suspended (trial ended, or
    /// manually suspended with nothing overdue) or cancelled account is reactivated at once and a new period starts today.
    /// </summary>
    public async Task<Subscription> ActivateAsync(string billingName, string billingEmail, string? taxPin, string? address, string? planCode, DateOnly today, CancellationToken ct)
    {
        tenant.Require(Permissions.BillingManage);
        var sub = await RequireOwnAsync(ct);
        if (string.IsNullOrWhiteSpace(billingName)) throw new DomainException("Enter the name to put on invoices.", "VALIDATION");
        if (string.IsNullOrWhiteSpace(billingEmail) || !billingEmail.Contains('@')) throw new DomainException("Enter an email address for invoices.", "VALIDATION");
        var pin = string.IsNullOrWhiteSpace(taxPin) ? null : KenyaIdentifiers.NormalisePin(taxPin);
        if (pin is not null && !KenyaIdentifiers.IsValidPin(pin, individual: false) && !KenyaIdentifiers.IsValidPin(pin, individual: true))
            throw new DomainException("The KRA PIN should look like P051234567A.", "VALIDATION");
        if (!string.IsNullOrWhiteSpace(planCode)) await ApplyPlanAsync(sub, planCode, operatorAssigned: false, ct);

        if (sub.Status == SubscriptionStatus.Suspended && await db.SubscriptionInvoices.Where(i => i.SubscriptionId == sub.Id && i.Status == InvoiceStatus.Open).OrderBy(i => i.DueOn).FirstOrDefaultAsync(ct) is { } unpaid)
            throw new InvalidStateException($"Pay invoice {unpaid.Number} first. The account reopens as soon as the payment is recorded.");
        if (sub.Status == SubscriptionStatus.Suspended && sub.SuspensionReason != Subscription.TrialEndedReason)
            throw new InvalidStateException($"The account was suspended by Ecosys ({sub.SuspensionReason}). Contact {_options.SupportEmail ?? "support"} to reopen it.");

        sub.BillingName = billingName.Trim(); sub.BillingEmail = billingEmail.Trim().ToLowerInvariant(); sub.BillingTaxPin = pin; sub.BillingAddress = address?.Trim();
        var before = sub.Status;
        sub.ActivatedAt ??= DateTimeOffset.UtcNow;
        if (sub.Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled)
        {
            sub.ActivatedAt = DateTimeOffset.UtcNow;
            sub.Status = SubscriptionStatus.Active;
            sub.SuspendedAt = null; sub.SuspensionReason = null; sub.CancelledAt = null; sub.CancellationReason = null; sub.CancelAtPeriodEnd = false;
            sub.CurrentPeriodStart = today; sub.CurrentPeriodEnd = today.AddMonths(1).AddDays(-1);
        }
        audit.Record("billing.activated", nameof(Subscription), sub.Id, new { before = before.ToString(), after = sub.Status.ToString(), sub.BillingEmail });
        await db.SaveChangesAsync(ct);
        return sub;
    }

    public async Task<Subscription> ChangePlanAsync(string planCode, CancellationToken ct)
    {
        tenant.Require(Permissions.BillingManage);
        var sub = await RequireOwnAsync(ct);
        if (sub.IsReadOnly) throw new InvalidStateException("Reactivate the account before changing plan.");
        var from = await db.Plans.IgnoreQueryFilters().Where(p => p.Id == sub.PlanId).Select(p => p.Code).FirstAsync(ct);
        await ApplyPlanAsync(sub, planCode, operatorAssigned: false, ct);
        audit.Record("billing.plan.changed", nameof(Subscription), sub.Id, new { from, to = planCode });
        await db.SaveChangesAsync(ct);
        return sub;
    }

    /// <summary>Validates the plan and that the tenant fits inside its limits, then switches. The next invoice uses the new prices.</summary>
    internal async Task ApplyPlanAsync(Subscription sub, string planCode, bool operatorAssigned, CancellationToken ct)
    {
        var code = planCode.Trim().ToLowerInvariant();
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Code == code && p.IsActive, ct) ?? throw new DomainException($"There is no plan '{planCode}'.", "VALIDATION");
        if (!plan.IsPublic && !operatorAssigned) throw new DomainException($"The {plan.Name} plan is arranged with our sales team.", "VALIDATION");
        var was = db.IgnoreTenantFilter;
        db.IgnoreTenantFilter = true;
        try
        {
            var headcount = await db.Employees.CountAsync(e => e.TenantId == sub.TenantId && CountedStatuses.Contains(e.Status), ct);
            var entities = await db.LegalEntities.CountAsync(e => e.TenantId == sub.TenantId, ct);
            if (plan.MaxEmployees is { } maxE && headcount > maxE)
                throw new PlanLimitException($"The {plan.Name} plan covers up to {maxE} employees and you have {headcount}.");
            if (plan.MaxLegalEntities is { } maxL && entities > maxL)
                throw new PlanLimitException($"The {plan.Name} plan covers {maxL} {(maxL == 1 ? "company" : "companies")} and you have {entities}.");
        }
        finally { db.IgnoreTenantFilter = was; }
        sub.PlanId = plan.Id;
    }

    public async Task<Subscription> CancelAsync(string? reason, CancellationToken ct)
    {
        tenant.Require(Permissions.BillingManage);
        var sub = await RequireOwnAsync(ct);
        if (sub.IsReadOnly) throw new InvalidStateException("The account is already closed for changes.");
        if (sub.Status == SubscriptionStatus.Trialing)
        {
            // Nothing to bill: close now.
            sub.Status = SubscriptionStatus.Cancelled; sub.CancelledAt = DateTimeOffset.UtcNow;
        }
        else sub.CancelAtPeriodEnd = true;
        sub.CancellationReason = reason?.Trim();
        audit.Record("billing.cancelled", nameof(Subscription), sub.Id, new { reason, atPeriodEnd = sub.CancelAtPeriodEnd, sub.CurrentPeriodEnd });
        await db.SaveChangesAsync(ct);
        return sub;
    }

    public async Task<Subscription> ResumeAsync(CancellationToken ct)
    {
        tenant.Require(Permissions.BillingManage);
        var sub = await RequireOwnAsync(ct);
        if (!sub.CancelAtPeriodEnd) throw new InvalidStateException("The subscription is not set to cancel.");
        sub.CancelAtPeriodEnd = false; sub.CancellationReason = null;
        audit.Record("billing.resumed", nameof(Subscription), sub.Id);
        await db.SaveChangesAsync(ct);
        return sub;
    }

    /// <summary>What the next invoice would be if it were issued now.</summary>
    public async Task<object?> EstimateAsync(Subscription sub, CancellationToken ct)
    {
        if (sub.IsReadOnly) return null;
        var plan = await db.Plans.IgnoreQueryFilters().FirstAsync(p => p.Id == sub.PlanId, ct);
        var start = sub.CurrentPeriodStart ?? sub.TrialEndsOn?.AddDays(1) ?? BusinessClock.Today();
        var end = sub.CurrentPeriodEnd ?? start.AddMonths(1).AddDays(-1);
        var headcount = await HeadcountAsync(ct);
        var (lines, subtotal, vat, total) = InvoiceCalculator.Calculate(plan, headcount, _options.VatRate, start, end);
        return new { periodStart = start, periodEnd = end, invoiceOn = end.AddDays(1), headcount, lines, subtotal, vatRate = _options.VatRate, vat, total, currency = plan.Currency };
    }

}
