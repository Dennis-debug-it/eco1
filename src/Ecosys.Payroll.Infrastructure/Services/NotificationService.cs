using System.Net.Http.Json;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecosys.Payroll.Infrastructure.Services;

/// <summary>
/// Queues notifications in the outbox as part of the caller's unit of work (the caller saves).
/// Message bodies deliberately carry no salary figures — recipients sign in to see details.
/// </summary>
public sealed class NotificationService(PayrollDbContext db, ITenantContext tenant, IConfiguration config)
{
    private string PortalUrl => config["Payroll:PortalUrl"] ?? "https://payroll.ecosys.example";

    public void Enqueue(NotificationChannel channel, string recipient, string? name, string template, string subject, string body, string? entityType = null, Guid? entityId = null, Guid? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(recipient)) return;
        db.NotificationMessages.Add(new NotificationMessage
        {
            TenantId = tenantId ?? tenant.RequireTenant(), Channel = channel, Recipient = recipient.Trim(), RecipientName = name, Template = template,
            Subject = subject, Body = body, EntityType = entityType, EntityId = entityId, NextAttemptAt = DateTimeOffset.UtcNow,
        });
    }

    private void ToUser(User u, string template, string subject, string body, string entityType, Guid entityId)
    {
        Enqueue(NotificationChannel.Email, u.Email, u.DisplayName, template, subject, body, entityType, entityId);
        if (!string.IsNullOrWhiteSpace(u.Phone)) Enqueue(NotificationChannel.WhatsApp, u.Phone, u.DisplayName, template, subject, body, entityType, entityId);
    }

    /// <summary>Tells the next people in the chain: reviewers when a review is required and not yet done, otherwise approvers.</summary>
    public async Task PayrollSubmittedAsync(PayrollRun run, CancellationToken ct, bool forReview = false)
    {
        var role = forReview ? Roles.PayrollReviewer : Roles.PayrollApprover;
        var recipients = (await db.Users.Where(u => u.IsActive).ToListAsync(ct))
            .Where(u => u.Roles.Contains(role) && u.Id != run.PreparedBy && u.Id != run.SubmittedBy && u.Id != run.ReviewedBy
                        && (u.LegalEntityIds.Count == 0 || u.LegalEntityIds.Contains(run.LegalEntityId)));
        foreach (var u in recipients)
            ToUser(u, forReview ? "payroll.review" : "payroll.submitted", $"Payroll {run.Reference} awaits your {(forReview ? "review" : "approval")}",
                $"Payroll run {run.Reference} ({run.EmployeeCount} employees) {(forReview ? "has been submitted for review" : run.ReviewedBy is null ? "has been submitted for approval" : "has been reviewed and awaits approval")}. Open it at {PortalUrl}/payroll/{run.Id}.", nameof(PayrollRun), run.Id);
    }

    public async Task PayrollDecidedAsync(PayrollRun run, bool approved, string? comment, CancellationToken ct)
    {
        var ids = new[] { run.PreparedBy, run.SubmittedBy }.Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        foreach (var u in await db.Users.Where(u => ids.Contains(u.Id) && u.IsActive).ToListAsync(ct))
            ToUser(u, approved ? "payroll.approved" : "payroll.rejected", $"Payroll {run.Reference} {(approved ? "approved" : "returned")}",
                approved ? $"Payroll run {run.Reference} was approved. Payment files can now be generated."
                         : $"Payroll run {run.Reference} was returned for correction: {comment}", nameof(PayrollRun), run.Id);
    }

    public async Task PayslipsIssuedAsync(PayrollRun run, string periodName, CancellationToken ct)
    {
        var empIds = run.Employees.Select(e => e.EmployeeId).ToList();
        var employees = await db.Employees.Where(e => empIds.Contains(e.Id)).Select(e => new { e.Id, e.FirstName, e.Email, e.Phone }).ToListAsync(ct);
        foreach (var e in employees)
        {
            var body = $"Hello {e.FirstName}, your payslip for {periodName} is now available. Sign in to {PortalUrl}/self-service to view or download it.";
            if (!string.IsNullOrWhiteSpace(e.Email)) Enqueue(NotificationChannel.Email, e.Email, e.FirstName, "payslip.issued", $"Your {periodName} payslip is ready", body, nameof(PayrollRun), run.Id);
            if (!string.IsNullOrWhiteSpace(e.Phone)) Enqueue(NotificationChannel.WhatsApp, e.Phone, e.FirstName, "payslip.issued", $"Your {periodName} payslip is ready", body, nameof(PayrollRun), run.Id);
        }
    }

    /// <summary>
    /// Tells the employee, through the contact details already on file, that their pay account changed — the main way
    /// a diverted salary is caught before payday. Carries only the bank and the last digits, never amounts.
    /// </summary>
    public void BankDetailsChanged(Domain.Employees.Employee e, Domain.Employees.EmployeeBankAccount account, bool verified)
    {
        var where = $"{account.BankName ?? account.Method.ToString()} account {FieldEncryptor.Mask(account.AccountNumber)}";
        var body = verified
            ? $"Hello {e.FirstName}, {where} has been verified on your payroll record and your pay will now go there. If you didn't ask for this, contact HR immediately."
            : $"Hello {e.FirstName}, {where} was added to your payroll record. It will be used once HR verifies it. If you didn't ask for this, contact HR immediately.";
        const string subject = "Your pay account details changed";
        if (!string.IsNullOrWhiteSpace(e.Email)) Enqueue(NotificationChannel.Email, e.Email, e.FirstName, "employee.bank.changed", subject, body, nameof(Domain.Employees.EmployeeBankAccount), account.Id, e.TenantId);
        if (!string.IsNullOrWhiteSpace(e.Phone)) Enqueue(NotificationChannel.WhatsApp, e.Phone, e.FirstName, "employee.bank.changed", subject, body, nameof(Domain.Employees.EmployeeBankAccount), account.Id, e.TenantId);
    }

    public async Task LeaveDecidedAsync(Domain.Leave.LeaveRequest r, bool approved, CancellationToken ct)
    {
        var e = await db.Employees.Where(x => x.Id == r.EmployeeId).Select(x => new { x.FirstName, x.Email, x.Phone }).FirstOrDefaultAsync(ct);
        if (e is null) return;
        var body = $"Hello {e.FirstName}, your leave request for {r.StartDate:dd MMM} – {r.EndDate:dd MMM yyyy} ({r.Days:0.#} days) was {(approved ? "approved" : "declined")}."
                   + (string.IsNullOrWhiteSpace(r.ApproverComment) ? "" : $" Comment: {r.ApproverComment}");
        var subject = $"Leave request {(approved ? "approved" : "declined")}";
        if (!string.IsNullOrWhiteSpace(e.Email)) Enqueue(NotificationChannel.Email, e.Email, e.FirstName, "leave.decided", subject, body, nameof(Domain.Leave.LeaveRequest), r.Id);
        if (!string.IsNullOrWhiteSpace(e.Phone)) Enqueue(NotificationChannel.WhatsApp, e.Phone, e.FirstName, "leave.decided", subject, body, nameof(Domain.Leave.LeaveRequest), r.Id);
    }
}

public interface INotificationSender
{
    /// <summary>Returns a provider reference on success; throws on failure.</summary>
    Task<string?> SendAsync(NotificationMessage message, CancellationToken ct);
}

/// <summary>Development/default provider: records delivery in the application log only.</summary>
public sealed class LogNotificationSender(ILogger<LogNotificationSender> log) : INotificationSender
{
    public Task<string?> SendAsync(NotificationMessage m, CancellationToken ct)
    {
        log.LogInformation("Notification [{Channel}] to {Recipient}: {Subject}", m.Channel, FieldEncryptor.Mask(m.Recipient), m.Subject);
        return Task.FromResult<string?>("log");
    }
}

/// <summary>
/// Delivers through the shared Ecosys Notifications service (email and WhatsApp), configured by
/// <c>Notifications:Ecosys:BaseUrl</c> and <c>Notifications:Ecosys:ApiKey</c>.
/// </summary>
public sealed class EcosysNotificationSender(HttpClient http, IConfiguration config) : INotificationSender
{
    public async Task<string?> SendAsync(NotificationMessage m, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = JsonContent.Create(new
            {
                channel = m.Channel.ToString().ToLowerInvariant(), to = m.Recipient, toName = m.RecipientName, template = m.Template,
                subject = m.Subject, body = m.Body, tenantId = m.TenantId, idempotencyKey = m.Id, source = "ecosys-payroll",
            }),
        };
        if (config["Notifications:Ecosys:ApiKey"] is { Length: > 0 } key) req.Headers.Authorization = new("Bearer", key);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var payload = await res.Content.ReadFromJsonAsync<Dictionary<string, object?>>(ct);
        return payload?.GetValueOrDefault("id")?.ToString();
    }
}

/// <summary>Background dispatcher for the notification outbox with exponential back-off (max 6 attempts).</summary>
public sealed class NotificationDispatcher(IServiceScopeFactory scopes, ILogger<NotificationDispatcher> log, IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Notifications:PollSeconds", 10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Notification dispatch failed"); }
            try { await Task.Delay(interval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PayrollDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
        db.IgnoreTenantFilter = true; // system worker processes all tenants' outboxes
        var now = DateTimeOffset.UtcNow;
        var due = await db.NotificationMessages.Where(m => m.Status == NotificationStatus.Pending && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
            .OrderBy(m => m.CreatedAt).Take(50).ToListAsync(ct);
        foreach (var m in due)
        {
            m.Attempts++;
            try
            {
                m.ProviderReference = await sender.SendAsync(m, ct);
                m.Status = NotificationStatus.Sent;
                m.SentAt = DateTimeOffset.UtcNow;
                m.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                m.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                if (m.Attempts >= 6) m.Status = NotificationStatus.Failed;
                else m.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, m.Attempts) * 15);
            }
        }
        await db.SaveChangesAsync(ct);
        return due.Count;
    }
}
