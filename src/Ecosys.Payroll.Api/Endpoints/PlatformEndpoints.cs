using System.Text.RegularExpressions;
using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Domain.Billing;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Seed;
using Ecosys.Payroll.Infrastructure.Services.Saas;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record PlatformProvisionRequest(string OrganisationName, string Slug, string? LegalEntityName, string KraPin, string AdminEmail, string AdminName, string? PlanCode, bool Trial = true);
public sealed record PlanCodeBody(string PlanCode);
public sealed record ExtendTrialBody(DateOnly Until);
public sealed record ReasonBody(string Reason);
public sealed record RecordPaymentBody(decimal Amount, BillingPaymentMethod Method, string Reference, DateTimeOffset? ReceivedAt, string? Notes);
public sealed record PlanBody(string Code, string Name, string? Description, decimal MonthlyBaseFee, int IncludedEmployees, decimal PricePerAdditionalEmployee,
    int? MaxEmployees, int? MaxLegalEntities, List<string>? Features, bool IsPublic, bool IsActive, int SortOrder, string? Currency);

/// <summary>
/// The operator console: customers, subscriptions, invoices, payments and the price list. Platform administrators only.
/// Shows account and billing data, never payroll or employee records.
/// </summary>
public static partial class PlatformEndpoints
{
    public static void MapPlatform(this WebApplication app)
    {
        var g = app.MapGroup("/api/platform").RequireAuthorization().AddEndpointFilter(async (c, next) =>
        {
            var ctx = c.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!ctx.IsPlatformAdministrator) throw new ForbiddenException("Platform administrators only.");
            c.HttpContext.RequestServices.GetRequiredService<PayrollDbContext>().IgnoreTenantFilter = true;
            return await next(c);
        });

        g.MapGet("/overview", async (PayrollDbContext db, SaasOptions options, CancellationToken ct) =>
        {
            var today = BusinessClock.Today();
            var subs = await db.Subscriptions.ToListAsync(ct);
            var plans = await db.Plans.IgnoreQueryFilters().ToDictionaryAsync(p => p.Id, ct);
            var headcounts = await Headcounts(db, ct);
            var paying = subs.Where(s => s.Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue).ToList();
            var mrr = paying.Sum(s => InvoiceCalculator.Calculate(plans[s.PlanId], headcounts.GetValueOrDefault(s.TenantId), 0m, today, today).Subtotal);
            var open = await db.SubscriptionInvoices.Where(i => i.Status == InvoiceStatus.Open).Select(i => new { i.Total, i.AmountPaid, i.DueOn }).ToListAsync(ct);
            return Results.Ok(new
            {
                saas = options.Enabled,
                tenants = await db.Tenants.CountAsync(t => t.Slug != PlatformAdminProvisioner.PlatformSlug, ct),
                managed = await db.Tenants.CountAsync(t => t.Slug != PlatformAdminProvisioner.PlatformSlug && !db.Subscriptions.Any(s => s.TenantId == t.Id), ct),
                byStatus = subs.GroupBy(s => s.Status).ToDictionary(x => x.Key.ToString(), x => x.Count()),
                mrrExVat = mrr,
                currency = "KES",
                openBalance = open.Sum(i => i.Total - i.AmountPaid),
                overdueBalance = open.Where(i => i.DueOn < today).Sum(i => i.Total - i.AmountPaid),
                trialsEndingThisWeek = subs.Count(s => s.Status == SubscriptionStatus.Trialing && s.TrialEndsOn <= today.AddDays(7)),
                signupsLast30Days = subs.Count(s => s.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-30)),
                employeesUnderManagement = headcounts.Values.Sum(),
            });
        });

        g.MapGet("/tenants", async (string? status, string? q, PayrollDbContext db, CancellationToken ct) =>
        {
            // The operator's own organisation is not a customer.
            var tenants = await db.Tenants.Where(t => t.Slug != PlatformAdminProvisioner.PlatformSlug).OrderBy(t => t.Name).ToListAsync(ct);
            var subs = await db.Subscriptions.ToDictionaryAsync(s => s.TenantId, ct);
            var plans = await db.Plans.IgnoreQueryFilters().ToDictionaryAsync(p => p.Id, ct);
            var headcounts = await Headcounts(db, ct);
            var balances = (await db.SubscriptionInvoices.Where(i => i.Status == InvoiceStatus.Open).Select(i => new { i.TenantId, i.Total, i.AmountPaid }).ToListAsync(ct))
                .GroupBy(i => i.TenantId).ToDictionary(x => x.Key, x => x.Sum(i => i.Total - i.AmountPaid));
            var lastLogin = (await db.Users.Where(u => u.LastLoginAt != null).Select(u => new { u.TenantId, u.LastLoginAt }).ToListAsync(ct))
                .GroupBy(u => u.TenantId).ToDictionary(x => x.Key, x => x.Max(u => u.LastLoginAt));
            var rows = tenants.Select(t =>
            {
                var s = subs.GetValueOrDefault(t.Id);
                var p = s is null ? null : plans[s.PlanId];
                return new
                {
                    t.Id, t.Name, t.Slug, t.IsActive, t.CreatedAt, status = s?.Status.ToString() ?? "Managed", plan = p?.Name, planCode = p?.Code, trialEndsOn = s?.TrialEndsOn,
                    employees = headcounts.GetValueOrDefault(t.Id), balance = balances.GetValueOrDefault(t.Id), lastLoginAt = lastLogin.GetValueOrDefault(t.Id),
                    suspensionReason = s?.SuspensionReason, cancelAtPeriodEnd = s?.CancelAtPeriodEnd ?? false,
                };
            });
            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(r => r.status.Equals(status, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(q)) rows = rows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Slug.Contains(q, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(rows.ToList());
        });

        g.MapGet("/tenants/{id:guid}", async (Guid id, PayrollDbContext db, CancellationToken ct) =>
        {
            var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Tenant", id);
            var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == id, ct);
            var plan = sub is null ? null : await db.Plans.IgnoreQueryFilters().FirstAsync(p => p.Id == sub.PlanId, ct);
            var users = (await db.Users.Where(u => u.TenantId == id).ToListAsync(ct));
            return Results.Ok(new
            {
                tenant = new { t.Id, t.Name, t.Slug, t.IsActive, t.CreatedAt },
                // Where the customer is in moving off their previous payroll system. State only: no employee or payroll figures.
                migration = new { inMigration = t.InMigration, t.MigrationStartedAt, t.GoLiveSignedAt, t.GoLiveSignedBy, t.GoLiveFrom },
                subscription = sub, plan = plan is null ? null : BillingEndpoints.PlanView(plan),
                usage = new
                {
                    employees = await db.Employees.CountAsync(e => e.TenantId == id && SubscriptionService.CountedStatuses.Contains(e.Status), ct),
                    legalEntities = await db.LegalEntities.CountAsync(l => l.TenantId == id, ct),
                    users = users.Count(u => u.IsActive),
                    payrollRuns = await db.PayrollRuns.CountAsync(r => r.TenantId == id, ct),
                },
                // Contacts only: who to talk to about the account. No employee or payroll data.
                administrators = users.Where(u => u.IsActive && u.Roles.Contains(Roles.TenantAdministrator)).Select(u => new { u.Email, u.DisplayName, u.LastLoginAt, u.MfaEnabled, emailVerified = u.EmailVerificationTokenHash == null }),
                invoices = await db.SubscriptionInvoices.Where(i => i.TenantId == id).OrderByDescending(i => i.PeriodStart).ToListAsync(ct),
                payments = await db.SubscriptionPayments.Where(p => p.TenantId == id).OrderByDescending(p => p.ReceivedAt).ToListAsync(ct),
                history = await db.PayrollAuditLogs.Where(a => a.TenantId == id && (a.Action.StartsWith("billing.") || a.Action.StartsWith("tenant.")))
                    .OrderByDescending(a => a.OccurredAt).Take(50).Select(a => new { a.Action, a.ActorName, a.OccurredAt, a.Details }).ToListAsync(ct),
            });
        });

        g.MapPost("/tenants", async (PlatformProvisionRequest req, TenantProvisioner provisioner, BillingService billing, PayrollDbContext db, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var r = await provisioner.ProvisionAsync(new TenantProvisionRequest(req.OrganisationName, req.Slug, string.IsNullOrWhiteSpace(req.LegalEntityName) ? req.OrganisationName : req.LegalEntityName,
                req.KraPin, req.AdminEmail, req.AdminName), ct);
            db.IgnoreTenantFilter = true;
            if (!string.IsNullOrWhiteSpace(req.PlanCode))
            {
                if (req.Trial) { await billing.StartTrialAsync(r.TenantId, req.PlanCode, req.OrganisationName.Trim(), r.AdminEmail, BusinessClock.Today(), ct); await db.SaveChangesAsync(ct); }
                else await billing.StartSubscriptionAsync(r.TenantId, req.PlanCode, BusinessClock.Today(), ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new { tenantId = r.TenantId, slug = r.Slug, adminEmail = r.AdminEmail, temporaryPassword = r.TemporaryPassword });
        });

        g.MapPost("/tenants/{id:guid}/subscription", async (Guid id, PlanCodeBody b, BillingService billing, EntitlementCache cache, CancellationToken ct) =>
            Done(cache, id, await billing.StartSubscriptionAsync(id, b.PlanCode, BusinessClock.Today(), ct)));
        g.MapPost("/tenants/{id:guid}/plan", async (Guid id, PlanCodeBody b, BillingService billing, EntitlementCache cache, CancellationToken ct) =>
            Done(cache, id, await billing.AssignPlanAsync(id, b.PlanCode, ct)));
        g.MapPost("/tenants/{id:guid}/extend-trial", async (Guid id, ExtendTrialBody b, BillingService billing, EntitlementCache cache, CancellationToken ct) =>
            Done(cache, id, await billing.ExtendTrialAsync(id, b.Until, BusinessClock.Today(), ct)));
        g.MapPost("/tenants/{id:guid}/suspend", async (Guid id, ReasonBody b, BillingService billing, EntitlementCache cache, CancellationToken ct) =>
            Done(cache, id, await billing.SuspendAsync(id, b.Reason, ct)));
        g.MapPost("/tenants/{id:guid}/reactivate", async (Guid id, BillingService billing, EntitlementCache cache, CancellationToken ct) =>
            Done(cache, id, await billing.ReactivateAsync(id, BusinessClock.Today(), ct)));

        // Blocks every sign-in to the organisation (abuse, legal hold). Billing suspension is the usual tool; this is the hard stop.
        g.MapPost("/tenants/{id:guid}/access", async (Guid id, AccessBody b, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Tenant", id);
            if (t.Id == ctx.TenantId && !b.Enabled) throw new DomainException("You can't disable sign-in to your own organisation.", "VALIDATION");
            if (!b.Enabled && string.IsNullOrWhiteSpace(b.Reason)) throw new DomainException("Give a reason for blocking sign-in.", "VALIDATION");
            t.IsActive = b.Enabled;
            db.PayrollAuditLogs.Add(new PayrollAuditLog { TenantId = id, ActorId = ctx.UserId, ActorName = ctx.UserName, Action = b.Enabled ? "tenant.access.enabled" : "tenant.access.disabled", EntityType = "Tenant", EntityId = id, Details = System.Text.Json.JsonSerializer.Serialize(new { reason = b.Reason }), IpAddress = ctx.IpAddress });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapGet("/invoices", async (string? status, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.SubscriptionInvoices.AsQueryable();
            if (Enum.TryParse<InvoiceStatus>(status, true, out var s)) q = q.Where(i => i.Status == s);
            var today = BusinessClock.Today();
            var rows = await q.OrderByDescending(i => i.IssuedOn).ThenByDescending(i => i.Number).Take(500)
                .Join(db.Tenants, i => i.TenantId, t => t.Id, (i, t) => new { i, tenant = t.Name, t.Slug }).ToListAsync(ct);
            return Results.Ok(rows.Select(r => new { r.i.Id, r.i.TenantId, r.tenant, r.Slug, r.i.Number, r.i.PlanName, r.i.PeriodStart, r.i.PeriodEnd, r.i.IssuedOn, r.i.DueOn, r.i.Currency, r.i.Total, r.i.AmountPaid, r.i.Balance, r.i.Status, overdue = r.i.Status == InvoiceStatus.Open && r.i.DueOn < today }));
        });

        g.MapPost("/invoices/{id:guid}/payments", async (Guid id, RecordPaymentBody b, BillingService billing, EntitlementCache cache, CancellationToken ct) =>
        {
            var inv = await billing.RecordPaymentAsync(id, b.Amount, b.Method, b.Reference, b.ReceivedAt, b.Notes, BusinessClock.Today(), ct);
            cache.Invalidate(inv.TenantId);
            return Results.Ok(new { inv.Id, inv.Number, inv.Status, inv.AmountPaid, inv.Balance });
        });

        g.MapPost("/invoices/{id:guid}/void", async (Guid id, ReasonBody b, BillingService billing, EntitlementCache cache, CancellationToken ct) =>
        {
            var inv = await billing.VoidInvoiceAsync(id, b.Reason, BusinessClock.Today(), ct);
            cache.Invalidate(inv.TenantId);
            return Results.Ok(new { inv.Id, inv.Number, inv.Status });
        });

        g.MapGet("/invoices/{id:guid}/pdf", async (Guid id, PayrollDbContext db, SaasOptions options, CancellationToken ct) =>
        {
            var inv = await db.SubscriptionInvoices.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException(nameof(SubscriptionInvoice), id);
            var name = await db.Tenants.Where(t => t.Id == inv.TenantId).Select(t => t.Name).FirstAsync(ct);
            return Results.File(InvoiceRenderer.Render(inv, name, options), "application/pdf", $"{inv.Number}.pdf");
        });

        // Runs the billing cycle now (it also runs every few hours in the background).
        g.MapPost("/billing/run", async (BillingService billing, CancellationToken ct) => Results.Ok(await billing.RunCycleAsync(BusinessClock.Today(), ct)));

        g.MapGet("/plans", async (PayrollDbContext db, CancellationToken ct) =>
        {
            var counts = await db.Subscriptions.GroupBy(s => s.PlanId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
            return Results.Ok((await db.Plans.OrderBy(p => p.SortOrder).ToListAsync(ct)).Select(p => new { plan = BillingEndpoints.PlanView(p), subscribers = counts.GetValueOrDefault(p.Id) }));
        });

        g.MapPost("/plans", async (PlanBody b, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var code = (b.Code ?? "").Trim().ToLowerInvariant();
            if (!CodeRx().IsMatch(code)) throw new DomainException("Plan codes are 2–30 lowercase letters, digits, hyphens or underscores.", "VALIDATION");
            if (await db.Plans.IgnoreQueryFilters().AnyAsync(p => p.Code == code, ct)) throw new DomainException($"A plan with code '{code}' already exists.", "DUPLICATE");
            var p = new Plan { Code = code, Name = "" };
            Apply(p, b);
            db.Plans.Add(p);
            PlatformAudit(db, ctx, "platform.plan.created", p.Id, new { p.Code, p.MonthlyBaseFee, p.PricePerAdditionalEmployee });
            await db.SaveChangesAsync(ct);
            return Results.Ok(BillingEndpoints.PlanView(p));
        });

        g.MapPut("/plans/{id:guid}", async (Guid id, PlanBody b, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var p = await db.Plans.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Plan), id);
            var before = BillingEndpoints.PlanView(p);
            Apply(p, b);
            PlatformAudit(db, ctx, "platform.plan.updated", p.Id, new { before, after = BillingEndpoints.PlanView(p) });
            await db.SaveChangesAsync(ct);
            return Results.Ok(BillingEndpoints.PlanView(p));
        });
    }

    public sealed record AccessBody(bool Enabled, string? Reason);

    private static IResult Done(EntitlementCache cache, Guid tenantId, Subscription sub)
    {
        cache.Invalidate(tenantId);
        return Results.Ok(new { sub.Id, sub.Status, sub.TrialEndsOn, sub.CurrentPeriodStart, sub.CurrentPeriodEnd, sub.SuspensionReason });
    }

    private static void Apply(Plan p, PlanBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name)) throw new DomainException("Enter a plan name.", "VALIDATION");
        if (b.MonthlyBaseFee < 0 || b.PricePerAdditionalEmployee < 0 || b.IncludedEmployees < 0) throw new DomainException("Prices and included employees cannot be negative.", "VALIDATION");
        if (b.MaxEmployees is < 1 || b.MaxLegalEntities is < 1) throw new DomainException("Limits must be at least 1, or empty for no limit.", "VALIDATION");
        if (b.MaxEmployees is { } max && max < b.IncludedEmployees) throw new DomainException("The employee limit cannot be lower than the included employees.", "VALIDATION");
        var unknown = (b.Features ?? []).Except(PlanFeatures.All).ToList();
        if (unknown.Count > 0) throw new DomainException($"Unknown features: {string.Join(", ", unknown)}.", "VALIDATION");
        p.Name = b.Name.Trim(); p.Description = b.Description?.Trim(); p.MonthlyBaseFee = b.MonthlyBaseFee; p.IncludedEmployees = b.IncludedEmployees;
        p.PricePerAdditionalEmployee = b.PricePerAdditionalEmployee; p.MaxEmployees = b.MaxEmployees; p.MaxLegalEntities = b.MaxLegalEntities;
        p.Features = (b.Features ?? []).Distinct().ToList(); p.IsPublic = b.IsPublic; p.IsActive = b.IsActive; p.SortOrder = b.SortOrder;
        if (!string.IsNullOrWhiteSpace(b.Currency)) p.Currency = b.Currency.Trim().ToUpperInvariant();
    }

    private static void PlatformAudit(PayrollDbContext db, ITenantContext ctx, string action, Guid id, object details) =>
        db.PayrollAuditLogs.Add(new PayrollAuditLog { TenantId = Guid.Empty, ActorId = ctx.UserId, ActorName = ctx.UserName, Action = action, EntityType = nameof(Plan), EntityId = id, Details = System.Text.Json.JsonSerializer.Serialize(details, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), IpAddress = ctx.IpAddress });

    private static async Task<Dictionary<Guid, int>> Headcounts(PayrollDbContext db, CancellationToken ct) =>
        await db.Employees.Where(e => SubscriptionService.CountedStatuses.Contains(e.Status)).GroupBy(e => e.TenantId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);

    [GeneratedRegex("^[a-z0-9_-]{2,30}$")] private static partial Regex CodeRx();
}
