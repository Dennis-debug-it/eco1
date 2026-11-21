using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Domain.Billing;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services.Saas;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record ActivateRequest(string BillingName, string BillingEmail, string? BillingTaxPin, string? BillingAddress, string? PlanCode);
public sealed record PlanChangeRequest(string PlanCode);
public sealed record CancelRequest(string? Reason);

/// <summary>The customer's view of their own subscription. Status is visible to every user; details and actions need billing.manage.</summary>
public static class BillingEndpoints
{
    public static object PlanView(Plan p) => new
    {
        p.Id, p.Code, p.Name, p.Description, p.Currency, p.MonthlyBaseFee, p.IncludedEmployees, p.PricePerAdditionalEmployee, p.MaxEmployees, p.MaxLegalEntities, p.Features, p.IsPublic, p.IsActive, p.SortOrder,
    };

    public static void MapBilling(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/billing").RequireAuthorization();

        // Drives the banner and which modules the app shows. No amounts.
        g.MapGet("/status", async (ITenantContext ctx, SaasOptions options, SubscriptionService subs, PayrollDbContext db, CancellationToken ct) =>
        {
            var ent = await subs.GetEntitlementsAsync(ctx.RequireTenant(), ct);
            object? usage = null;
            if (!ent.Managed && ctx.Has(Permissions.BillingManage))
                usage = new { employees = await subs.HeadcountAsync(ct), ent.MaxEmployees, legalEntities = await db.LegalEntities.CountAsync(ct), ent.MaxLegalEntities };
            var today = BusinessClock.Today();
            return Results.Ok(new
            {
                saas = options.Enabled, managed = ent.Managed, status = ent.Status, planCode = ent.PlanCode, planName = ent.PlanName, features = ent.Features,
                readOnly = ent.ReadOnly, trialEndsOn = ent.TrialEndsOn, trialDaysLeft = ent.TrialEndsOn is { } end ? Math.Max(0, end.DayNumber - today.DayNumber) : (int?)null,
                activated = ent.Activated, cancelAtPeriodEnd = ent.CancelAtPeriodEnd, suspensionReason = ent.SuspensionReason, usage,
            });
        });

        g.MapGet("", async (ITenantContext ctx, SaasOptions options, SubscriptionService subs, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.BillingManage);
            var sub = await subs.RequireOwnAsync(ct);
            var plan = await db.Plans.IgnoreQueryFilters().FirstAsync(p => p.Id == sub.PlanId, ct);
            var invoices = await db.SubscriptionInvoices.OrderByDescending(i => i.PeriodStart).Take(36).ToListAsync(ct);
            var payments = await db.SubscriptionPayments.OrderByDescending(p => p.ReceivedAt).Take(50).ToListAsync(ct);
            var plans = await db.Plans.Where(p => p.IsActive && (p.IsPublic || p.Id == sub.PlanId)).OrderBy(p => p.SortOrder).ToListAsync(ct);
            return Results.Ok(new
            {
                subscription = new
                {
                    sub.Id, sub.Status, sub.TrialEndsOn, sub.ActivatedAt, sub.CurrentPeriodStart, sub.CurrentPeriodEnd, sub.CancelAtPeriodEnd, sub.CancelledAt, sub.SuspendedAt, sub.SuspensionReason,
                    sub.BillingName, sub.BillingEmail, sub.BillingTaxPin, sub.BillingAddress, readOnly = sub.IsReadOnly,
                },
                plan = PlanView(plan),
                estimate = await subs.EstimateAsync(sub, ct),
                usage = new { employees = await subs.HeadcountAsync(ct), legalEntities = await db.LegalEntities.CountAsync(ct) },
                invoices = invoices.Select(i => new { i.Id, i.Number, i.PeriodStart, i.PeriodEnd, i.IssuedOn, i.DueOn, i.Currency, i.BillableEmployees, i.Subtotal, i.Vat, i.Total, i.AmountPaid, i.Balance, i.Status, i.PaidAt, overdue = i.Status == InvoiceStatus.Open && i.DueOn < BusinessClock.Today() }),
                payments = payments.Select(p => new { p.Id, p.InvoiceId, p.Amount, p.Method, p.Reference, p.ReceivedAt }),
                plans = plans.Select(PlanView),
                paymentInstructions = options.PaymentInstructions, vatRate = options.VatRate, supportEmail = options.SupportEmail,
            });
        });

        g.MapPost("/activate", async (ActivateRequest req, ITenantContext ctx, SubscriptionService subs, EntitlementCache cache, CancellationToken ct) =>
        {
            var sub = await subs.ActivateAsync(req.BillingName, req.BillingEmail, req.BillingTaxPin, req.BillingAddress, req.PlanCode, BusinessClock.Today(), ct);
            cache.Invalidate(sub.TenantId);
            return Results.Ok(new { sub.Status, sub.ActivatedAt, sub.CurrentPeriodStart, sub.CurrentPeriodEnd });
        });

        g.MapPost("/plan", async (PlanChangeRequest req, SubscriptionService subs, EntitlementCache cache, CancellationToken ct) =>
        {
            var sub = await subs.ChangePlanAsync(req.PlanCode, ct);
            cache.Invalidate(sub.TenantId);
            return Results.NoContent();
        });

        g.MapPost("/cancel", async (CancelRequest req, SubscriptionService subs, EntitlementCache cache, CancellationToken ct) =>
        {
            var sub = await subs.CancelAsync(req.Reason, ct);
            cache.Invalidate(sub.TenantId);
            return Results.Ok(new { sub.Status, sub.CancelAtPeriodEnd, sub.CurrentPeriodEnd });
        });

        g.MapPost("/resume", async (SubscriptionService subs, EntitlementCache cache, CancellationToken ct) =>
        {
            var sub = await subs.ResumeAsync(ct);
            cache.Invalidate(sub.TenantId);
            return Results.NoContent();
        });

        g.MapGet("/invoices/{id:guid}/pdf", async (Guid id, ITenantContext ctx, SaasOptions options, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.BillingManage);
            var inv = await db.SubscriptionInvoices.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException(nameof(SubscriptionInvoice), id);
            var name = await db.Tenants.Where(t => t.Id == inv.TenantId).Select(t => t.Name).FirstAsync(ct);
            return Results.File(InvoiceRenderer.Render(inv, name, options), "application/pdf", $"{inv.Number}.pdf");
        });
    }
}
