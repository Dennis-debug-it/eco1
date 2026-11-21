using Ecosys.Payroll.Domain.Billing;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecosys.Payroll.Infrastructure.Services.Saas;

public sealed record BillingCycleResult(int TrialsConverted, int TrialsExpired, int InvoicesIssued, int MarkedPastDue, int Suspended, int Cancelled, int RemindersQueued);

/// <summary>
/// Platform-side billing across all tenants: starting trials, the billing cycle (trial ends, monthly invoices in
/// arrears, dunning), payments and operator actions. Runs with the tenant filter off, so every method either
/// belongs to the platform operator (checked by the caller) or to a background job.
/// </summary>
public sealed class BillingService(PayrollDbContext db, ITenantContext tenant, SubscriptionService subscriptions, IConfiguration config)
{
    private readonly SaasOptions _options = SaasOptions.From(config);
    private string PortalUrl => config["Payroll:PortalUrl"] ?? "https://payroll.ecosys.example";

    /// <summary>Starts a trial for a newly created tenant. The caller saves.</summary>
    public async Task<Subscription> StartTrialAsync(Guid tenantId, string? planCode, string billingName, string billingEmail, DateOnly today, CancellationToken ct)
    {
        db.IgnoreTenantFilter = true;
        var code = string.IsNullOrWhiteSpace(planCode) ? _options.DefaultPlan : planCode.Trim().ToLowerInvariant();
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Code == code && p.IsActive && p.IsPublic, ct) ?? throw new DomainException($"There is no plan '{planCode}'.", "VALIDATION");
        var sub = new Subscription
        {
            TenantId = tenantId, PlanId = plan.Id, Status = SubscriptionStatus.Trialing, TrialEndsOn = today.AddDays(_options.TrialDays),
            BillingName = billingName, BillingEmail = billingEmail,
        };
        db.Subscriptions.Add(sub);
        return sub;
    }

    // ------------------------------------------------------------------ Billing cycle

    /// <summary>Idempotent: safe to run many times a day and to catch up after downtime.</summary>
    public async Task<BillingCycleResult> RunCycleAsync(DateOnly today, CancellationToken ct = default)
    {
        db.IgnoreTenantFilter = true;
        int converted = 0, expired = 0, issued = 0, pastDue = 0, suspended = 0, cancelled = 0, reminders = 0;
        var plans = await db.Plans.IgnoreQueryFilters().ToDictionaryAsync(p => p.Id, ct);
        var subs = await db.Subscriptions.Where(s => s.Status != SubscriptionStatus.Cancelled).ToListAsync(ct);

        foreach (var sub in subs)
        {
            var plan = plans[sub.PlanId];
            if (sub.Status == SubscriptionStatus.Trialing && sub.TrialEndsOn is { } trialEnd)
            {
                if (trialEnd < today)
                {
                    if (sub.ActivatedAt is not null)
                    {
                        sub.Status = SubscriptionStatus.Active;
                        sub.CurrentPeriodStart = trialEnd.AddDays(1);
                        sub.CurrentPeriodEnd = trialEnd.AddDays(1).AddMonths(1).AddDays(-1);
                        Audit(sub.TenantId, "billing.trial.converted", sub.Id, new { plan = plan.Code });
                        await NotifyAsync(sub, "billing.trial.converted", "Your Ecosys Payroll subscription has started",
                            $"Your free trial has ended and your {plan.Name} subscription is now active. Your first invoice will be issued on {sub.CurrentPeriodEnd.Value.AddDays(1):d MMM yyyy}.", ct);
                        converted++;
                    }
                    else
                    {
                        Suspend(sub, Subscription.TrialEndedReason);
                        await NotifyAsync(sub, "billing.trial.ended", "Your Ecosys Payroll trial has ended",
                            $"Your free trial has ended. Your data is safe and you can still sign in to view and export it. To keep running payroll, choose a plan and add billing details at {PortalUrl}/billing.", ct);
                        expired++;
                    }
                }
                else if (sub.ActivatedAt is null && trialEnd <= today.AddDays(3) && !await AlreadyNotifiedAsync(sub, "billing.trial.ending", TrialEndingSubject(trialEnd), ct))
                {
                    await NotifyAsync(sub, "billing.trial.ending", TrialEndingSubject(trialEnd),
                        $"Your free trial ends on {trialEnd:d MMM yyyy}. To carry on without interruption, add billing details at {PortalUrl}/billing. If you do nothing, your account becomes read-only; nothing is deleted.", ct);
                    reminders++;
                }
            }

            // Monthly billing in arrears: invoice each period that has ended, then move to the next one.
            while (sub.Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue && sub.CurrentPeriodEnd is { } end && end < today)
            {
                var start = sub.CurrentPeriodStart ?? end.AddMonths(-1).AddDays(1);
                if (!await db.SubscriptionInvoices.AnyAsync(i => i.SubscriptionId == sub.Id && i.PeriodStart == start, ct))
                {
                    await IssueInvoiceAsync(sub, plan, start, end, issuedOn: today, ct);
                    issued++;
                }
                if (sub.CancelAtPeriodEnd)
                {
                    sub.Status = SubscriptionStatus.Cancelled; sub.CancelledAt = DateTimeOffset.UtcNow; sub.CancelAtPeriodEnd = false;
                    Audit(sub.TenantId, "billing.cancellation.effective", sub.Id, new { periodEnd = end });
                    await NotifyAsync(sub, "billing.cancelled", "Your Ecosys Payroll subscription has ended",
                        $"Your subscription ended on {end:d MMM yyyy} as requested. You can still sign in to view and export your records, and reactivate at any time from {PortalUrl}/billing.", ct);
                    cancelled++;
                    break;
                }
                sub.CurrentPeriodStart = end.AddDays(1);
                sub.CurrentPeriodEnd = end.AddDays(1).AddMonths(1).AddDays(-1);
            }
        }
        await db.SaveChangesAsync(ct);

        // Dunning: overdue → past due; overdue beyond the grace period → read-only.
        var overdue = await db.SubscriptionInvoices.Where(i => i.Status == InvoiceStatus.Open && i.DueOn < today).ToListAsync(ct);
        foreach (var group in overdue.GroupBy(i => i.SubscriptionId))
        {
            var sub = subs.FirstOrDefault(s => s.Id == group.Key) ?? await db.Subscriptions.FirstAsync(s => s.Id == group.Key, ct);
            var oldest = group.OrderBy(i => i.DueOn).First();
            if (sub.Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue && oldest.DueOn.AddDays(_options.GraceDays) < today)
            {
                Suspend(sub, $"Invoice {oldest.Number} overdue");
                await NotifyAsync(sub, "billing.suspended", "Your Ecosys Payroll account is read-only",
                    $"Invoice {oldest.Number} was due on {oldest.DueOn:d MMM yyyy} and is still unpaid, so your account is now read-only. You can still sign in and export your data. It reopens as soon as payment is received. {_options.PaymentInstructions}", ct);
                suspended++;
            }
            else if (sub.Status == SubscriptionStatus.Active)
            {
                sub.Status = SubscriptionStatus.PastDue;
                Audit(sub.TenantId, "billing.past_due", sub.Id, new { invoice = oldest.Number });
                await NotifyAsync(sub, "billing.past_due", $"Invoice {oldest.Number} is overdue",
                    $"Invoice {oldest.Number} ({oldest.Currency} {oldest.Balance:N2}) was due on {oldest.DueOn:d MMM yyyy}. Please pay by {oldest.DueOn.AddDays(_options.GraceDays):d MMM yyyy} to avoid your account becoming read-only. {_options.PaymentInstructions}", ct);
                pastDue++;
            }
        }
        await db.SaveChangesAsync(ct);
        return new BillingCycleResult(converted, expired, issued, pastDue, suspended, cancelled, reminders);
    }

    private async Task IssueInvoiceAsync(Subscription sub, Plan plan, DateOnly start, DateOnly end, DateOnly issuedOn, CancellationToken ct)
    {
        var headcount = await db.Employees.CountAsync(e => e.TenantId == sub.TenantId && SubscriptionService.CountedStatuses.Contains(e.Status), ct);
        var (lines, subtotal, vat, total) = InvoiceCalculator.Calculate(plan, headcount, _options.VatRate, start, end);
        var inv = new SubscriptionInvoice
        {
            TenantId = sub.TenantId, SubscriptionId = sub.Id, Number = await db.NextInvoiceNumberAsync(ct), PlanCode = plan.Code, PlanName = plan.Name,
            PeriodStart = start, PeriodEnd = end, IssuedOn = issuedOn, DueOn = issuedOn.AddDays(_options.InvoiceDueDays), Currency = plan.Currency,
            Lines = lines, BillableEmployees = headcount, Subtotal = subtotal, VatRate = _options.VatRate, Vat = vat, Total = total,
            BillingName = sub.BillingName, BillingTaxPin = sub.BillingTaxPin,
        };
        if (total == 0) { inv.Status = InvoiceStatus.Paid; inv.PaidAt = DateTimeOffset.UtcNow; }
        db.SubscriptionInvoices.Add(inv);
        Audit(sub.TenantId, "billing.invoice.issued", inv.Id, new { inv.Number, inv.Total, headcount });
        if (total > 0)
            await NotifyAsync(sub, "billing.invoice", $"Ecosys Payroll invoice {inv.Number}",
                $"Invoice {inv.Number} for {start:d MMM} – {end:d MMM yyyy} is ready: {inv.Currency} {total:N2} including VAT, due {inv.DueOn:d MMM yyyy}. View or download it at {PortalUrl}/billing. {_options.PaymentInstructions}", ct);
    }

    // ------------------------------------------------------------------ Payments

    public async Task<SubscriptionInvoice> RecordPaymentAsync(Guid invoiceId, decimal amount, BillingPaymentMethod method, string reference, DateTimeOffset? receivedAt, string? notes, DateOnly today, CancellationToken ct)
    {
        db.IgnoreTenantFilter = true;
        var inv = await db.SubscriptionInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new NotFoundException(nameof(SubscriptionInvoice), invoiceId);
        if (inv.Status != InvoiceStatus.Open) throw new InvalidStateException($"Invoice {inv.Number} is {inv.Status.ToString().ToLowerInvariant()}.");
        if (amount <= 0) throw new DomainException("Enter the amount received.", "VALIDATION");
        if (amount > inv.Balance) throw new DomainException($"That is more than the {inv.Currency} {inv.Balance:N2} outstanding on {inv.Number}.", "VALIDATION");
        if (string.IsNullOrWhiteSpace(reference)) throw new DomainException("Enter the payment reference (M-Pesa code, bank reference).", "VALIDATION");
        if (await db.SubscriptionPayments.AnyAsync(p => p.Reference == reference.Trim() && p.Method == method, ct))
            throw new DomainException($"A {method} payment with reference {reference.Trim()} is already recorded.", "DUPLICATE");

        db.SubscriptionPayments.Add(new SubscriptionPayment
        {
            TenantId = inv.TenantId, InvoiceId = inv.Id, Amount = amount, Method = method, Reference = reference.Trim(), ReceivedAt = receivedAt ?? DateTimeOffset.UtcNow,
            RecordedByName = tenant.UserName, Notes = notes,
        });
        inv.AmountPaid += amount;
        if (inv.AmountPaid >= inv.Total) { inv.Status = InvoiceStatus.Paid; inv.PaidAt = DateTimeOffset.UtcNow; }
        Audit(inv.TenantId, "billing.payment.recorded", inv.Id, new { inv.Number, amount, method = method.ToString(), reference = reference.Trim() });
        await db.SaveChangesAsync(ct);
        await ReconcileStandingAsync(inv.SubscriptionId, today, ct);
        return inv;
    }

    public async Task<SubscriptionInvoice> VoidInvoiceAsync(Guid invoiceId, string reason, DateOnly today, CancellationToken ct)
    {
        db.IgnoreTenantFilter = true;
        var inv = await db.SubscriptionInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new NotFoundException(nameof(SubscriptionInvoice), invoiceId);
        if (inv.Status != InvoiceStatus.Open) throw new InvalidStateException($"Only open invoices can be voided; {inv.Number} is {inv.Status}.");
        if (inv.AmountPaid > 0) throw new InvalidStateException("Payments have been recorded against this invoice. Refund them before voiding.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Give a reason for voiding the invoice.", "VALIDATION");
        inv.Status = InvoiceStatus.Void; inv.VoidedAt = DateTimeOffset.UtcNow; inv.VoidReason = reason.Trim();
        Audit(inv.TenantId, "billing.invoice.voided", inv.Id, new { inv.Number, reason });
        await db.SaveChangesAsync(ct);
        await ReconcileStandingAsync(inv.SubscriptionId, today, ct);
        return inv;
    }

    /// <summary>After money comes in: an account held back only for non-payment returns to good standing once nothing is overdue.</summary>
    private async Task ReconcileStandingAsync(Guid subscriptionId, DateOnly today, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstAsync(s => s.Id == subscriptionId, ct);
        var stillOverdue = await db.SubscriptionInvoices.AnyAsync(i => i.SubscriptionId == sub.Id && i.Status == InvoiceStatus.Open && i.DueOn < today, ct);
        if (stillOverdue) return;
        var heldForNonPayment = sub.Status == SubscriptionStatus.Suspended && sub.SuspensionReason?.Contains("overdue", StringComparison.OrdinalIgnoreCase) == true;
        if (sub.Status == SubscriptionStatus.PastDue || heldForNonPayment)
        {
            sub.Status = SubscriptionStatus.Active; sub.SuspendedAt = null; sub.SuspensionReason = null;
            if (sub.CurrentPeriodEnd is { } end && end < today) { sub.CurrentPeriodStart = today; sub.CurrentPeriodEnd = today.AddMonths(1).AddDays(-1); }
            Audit(sub.TenantId, "billing.reinstated", sub.Id);
            await NotifyAsync(sub, "billing.reinstated", "Thank you, your payment is recorded",
                "Your payment has been recorded and your Ecosys Payroll account is in good standing.", ct);
            await db.SaveChangesAsync(ct);
        }
    }

    // ------------------------------------------------------------------ Operator actions

    public async Task<Subscription> ExtendTrialAsync(Guid tenantId, DateOnly until, DateOnly today, CancellationToken ct)
    {
        var sub = await RequireAsync(tenantId, ct);
        if (until <= today) throw new DomainException("Choose a date after today.", "VALIDATION");
        if (sub.Status is not (SubscriptionStatus.Trialing or SubscriptionStatus.Suspended) || sub.Status == SubscriptionStatus.Suspended && sub.SuspensionReason != Subscription.TrialEndedReason)
            throw new InvalidStateException("Only a trial, or an account suspended because its trial ended, can be extended.");
        var before = sub.TrialEndsOn;
        sub.Status = SubscriptionStatus.Trialing; sub.TrialEndsOn = until; sub.SuspendedAt = null; sub.SuspensionReason = null;
        Audit(tenantId, "billing.trial.extended", sub.Id, new { before, until });
        await db.SaveChangesAsync(ct);
        return sub;
    }

    public async Task<Subscription> SuspendAsync(Guid tenantId, string reason, CancellationToken ct)
    {
        var sub = await RequireAsync(tenantId, ct);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Give a reason for suspending the account.", "VALIDATION");
        if (sub.IsReadOnly) throw new InvalidStateException("The account is already read-only.");
        Suspend(sub, reason.Trim());
        await NotifyAsync(sub, "billing.suspended", "Your Ecosys Payroll account is read-only",
            $"Your account has been made read-only: {reason.Trim()}. You can still sign in and export your data. Contact {_options.SupportEmail ?? "support"} if you have questions.", ct);
        await db.SaveChangesAsync(ct);
        return sub;
    }

    public async Task<Subscription> ReactivateAsync(Guid tenantId, DateOnly today, CancellationToken ct)
    {
        var sub = await RequireAsync(tenantId, ct);
        if (!sub.IsReadOnly && sub.Status != SubscriptionStatus.PastDue) throw new InvalidStateException("The account is already active.");
        var before = sub.Status;
        sub.Status = SubscriptionStatus.Active; sub.SuspendedAt = null; sub.SuspensionReason = null; sub.CancelledAt = null; sub.CancelAtPeriodEnd = false;
        sub.ActivatedAt ??= DateTimeOffset.UtcNow;
        if (sub.CurrentPeriodEnd is null || sub.CurrentPeriodEnd < today) { sub.CurrentPeriodStart = today; sub.CurrentPeriodEnd = today.AddMonths(1).AddDays(-1); }
        Audit(tenantId, "billing.reactivated", sub.Id, new { before = before.ToString() });
        await db.SaveChangesAsync(ct);
        return sub;
    }

    public async Task<Subscription> AssignPlanAsync(Guid tenantId, string planCode, CancellationToken ct)
    {
        var sub = await RequireAsync(tenantId, ct);
        var from = await db.Plans.IgnoreQueryFilters().Where(p => p.Id == sub.PlanId).Select(p => p.Code).FirstAsync(ct);
        await subscriptions.ApplyPlanAsync(sub, planCode, operatorAssigned: true, ct);
        Audit(tenantId, "billing.plan.assigned", sub.Id, new { from, to = planCode });
        await db.SaveChangesAsync(ct);
        return sub;
    }

    /// <summary>Puts an operator-managed tenant (no subscription yet) onto a plan, active from today.</summary>
    public async Task<Subscription> StartSubscriptionAsync(Guid tenantId, string planCode, DateOnly today, CancellationToken ct)
    {
        db.IgnoreTenantFilter = true;
        if (await db.Subscriptions.AnyAsync(s => s.TenantId == tenantId, ct)) throw new InvalidStateException("The organisation already has a subscription.");
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct)) throw new NotFoundException("Tenant", tenantId);
        var sub = new Subscription { TenantId = tenantId, Status = SubscriptionStatus.Active, ActivatedAt = DateTimeOffset.UtcNow, CurrentPeriodStart = today, CurrentPeriodEnd = today.AddMonths(1).AddDays(-1) };
        await subscriptions.ApplyPlanAsync(sub, planCode, operatorAssigned: true, ct);
        db.Subscriptions.Add(sub);
        Audit(tenantId, "billing.subscription.started", sub.Id, new { plan = planCode });
        await db.SaveChangesAsync(ct);
        return sub;
    }

    private async Task<Subscription> RequireAsync(Guid tenantId, CancellationToken ct)
    {
        db.IgnoreTenantFilter = true;
        return await db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct)
               ?? throw new DomainException("This organisation is operator-managed and has no subscription. Start one first.", "NOT_FOUND");
    }

    private void Suspend(Subscription sub, string reason)
    {
        sub.Status = SubscriptionStatus.Suspended; sub.SuspendedAt = DateTimeOffset.UtcNow; sub.SuspensionReason = reason;
        Audit(sub.TenantId, "billing.suspended", sub.Id, new { reason });
    }

    /// <summary>Operator and system actions are written to the customer's own audit log, so they can see what was done to their account.</summary>
    private void Audit(Guid tenantId, string action, Guid entityId, object? details = null) =>
        db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            TenantId = tenantId, ActorId = tenant.UserId, ActorName = tenant.UserName ?? "billing", Action = action, EntityType = nameof(Subscription), EntityId = entityId,
            Details = details is null ? null : System.Text.Json.JsonSerializer.Serialize(details, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            IpAddress = tenant.IpAddress,
        });

    private static string TrialEndingSubject(DateOnly trialEnd) => $"Your Ecosys Payroll trial ends on {trialEnd:d MMMM}";

    /// <summary>Reminders carry their date in the subject, so each is sent once, and an extended trial gets a fresh one.</summary>
    private Task<bool> AlreadyNotifiedAsync(Subscription sub, string template, string subject, CancellationToken ct) =>
        db.NotificationMessages.AnyAsync(m => m.TenantId == sub.TenantId && m.Template == template && m.EntityId == sub.Id && m.Subject == subject, ct);

    /// <summary>Billing mail goes to the billing contact, or to the tenant administrators when none is set. Never contains payroll figures.</summary>
    private async Task NotifyAsync(Subscription sub, string template, string subject, string body, CancellationToken ct)
    {
        var recipients = new List<(string Email, string? Name)>();
        if (!string.IsNullOrWhiteSpace(sub.BillingEmail)) recipients.Add((sub.BillingEmail, sub.BillingName));
        else
            recipients.AddRange((await db.Users.Where(u => u.TenantId == sub.TenantId && u.IsActive).ToListAsync(ct))
                .Where(u => u.Roles.Contains(Roles.TenantAdministrator)).Select(u => (u.Email, (string?)u.DisplayName)));
        foreach (var (email, name) in recipients)
            db.NotificationMessages.Add(new NotificationMessage
            {
                TenantId = sub.TenantId, Channel = NotificationChannel.Email, Recipient = email, RecipientName = name, Template = template, Subject = subject, Body = body,
                EntityType = nameof(Subscription), EntityId = sub.Id, NextAttemptAt = DateTimeOffset.UtcNow,
            });
    }
}

/// <summary>Runs the billing cycle every few hours when SaaS mode is on.</summary>
public sealed class BillingJob(IServiceScopeFactory scopes, ILogger<BillingJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<TenantContext>();
                ctx.UserName = "billing"; ctx.IsPlatformAdministrator = true;
                var r = await scope.ServiceProvider.GetRequiredService<BillingService>().RunCycleAsync(BusinessClock.Today(), stoppingToken);
                if (r != new BillingCycleResult(0, 0, 0, 0, 0, 0, 0)) log.LogInformation("Billing cycle: {Result}", r);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Billing cycle failed"); }
            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
