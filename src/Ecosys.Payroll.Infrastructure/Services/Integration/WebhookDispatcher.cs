using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Integration;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecosys.Payroll.Infrastructure.Services.Integration;

public sealed record WebhookResponse(int? StatusCode, string? Error, int DurationMs)
{
    public bool Succeeded => StatusCode is >= 200 and < 300;
}

public interface IWebhookSender
{
    Task<WebhookResponse> SendAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
}

/// <summary>
/// Posts webhooks over HTTPS with a 10-second timeout and no redirects. Every connection is checked against
/// <see cref="WebhookAddress.IsBlocked"/> after DNS, so a public name that resolves to an internal address is refused.
/// </summary>
public sealed class HttpWebhookSender : IWebhookSender, IDisposable
{
    private readonly HttpClient _http;

    public HttpWebhookSender(IConfiguration config)
    {
        var allowPrivate = config.GetValue("Integrations:AllowPrivateWebhookTargets", false);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = config.GetValue("Integrations:WebhookUseProxy", false),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var usable = allowPrivate ? addresses : addresses.Where(a => !WebhookAddress.IsBlocked(a)).ToArray();
                if (usable.Length == 0) throw new HttpRequestException($"{context.DnsEndPoint.Host} is not a public address.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(usable, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            },
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ecosys-Payroll-Webhooks/1.0");
    }

    public async Task<WebhookResponse> SendAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return new WebhookResponse((int)res.StatusCode, res.IsSuccessStatusCode ? null : $"The receiver answered {(int)res.StatusCode} {res.ReasonPhrase}", (int)watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException && !ct.IsCancellationRequested)
        {
            var message = ex is TaskCanceledException ? "No answer within 10 seconds" : ex.InnerException?.Message ?? ex.Message;
            return new WebhookResponse(null, message.Length > 300 ? message[..300] : message, (int)watch.ElapsedMilliseconds);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Turns saved events into deliveries for every webhook listening, then sends what is due. A failed delivery is
/// retried over about eleven hours; an endpoint whose deliveries keep failing is switched off and administrators are told.
/// </summary>
public sealed class WebhookDispatcher(IServiceScopeFactory scopes, ILogger<WebhookDispatcher> log, IConfiguration config) : BackgroundService
{
    /// <summary>Wait before each retry: 30 s, 2 min, 10 min, 30 min, 1 h, 3 h, 6 h. Eight attempts in all.</summary>
    public static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6)];
    public static int MaxAttempts => Backoff.Length + 1;
    /// <summary>Deliveries in a row that use up every retry before the endpoint is switched off.</summary>
    public const int DisableAfter = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Integrations:PollSeconds", 5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Webhook dispatch failed"); }
            try { await Task.Delay(interval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One pass: fan out new events, send due deliveries, clear expired idempotency records. Returns deliveries attempted.</summary>
    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PayrollDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IWebhookSender>();
        db.IgnoreTenantFilter = true; // a system worker serving every organisation
        await FanOutAsync(db, ct);
        var sent = await SendDueAsync(db, sender, ct);
        await db.IdempotencyRecords.IgnoreQueryFilters().Where(r => r.ExpiresAt < DateTimeOffset.UtcNow).ExecuteDeleteAsync(ct);
        return sent;
    }

    private async Task FanOutAsync(PayrollDbContext db, CancellationToken ct)
    {
        var events = await db.IntegrationEvents.Where(e => !e.FannedOut).OrderBy(e => e.Sequence).Take(200).ToListAsync(ct);
        if (events.Count == 0) return;
        var tenants = events.Select(e => e.TenantId).Distinct().ToList();
        var endpoints = (await db.WebhookEndpoints.Where(w => w.IsActive && tenants.Contains(w.TenantId)).ToListAsync(ct)).ToLookup(w => w.TenantId);
        var baseUrl = config["Payroll:PortalUrl"];
        foreach (var e in events)
        {
            foreach (var w in endpoints[e.TenantId].Where(w => w.Wants(e.Type)))
                db.WebhookDeliveries.Add(new WebhookDelivery
                {
                    TenantId = e.TenantId, WebhookEndpointId = w.Id, IntegrationEventId = e.Id, EventType = e.Type,
                    Payload = EventPayload.Serialize(e, baseUrl), NextAttemptAt = DateTimeOffset.UtcNow,
                });
            e.FannedOut = true;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<int> SendDueAsync(PayrollDbContext db, IWebhookSender sender, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var due = await db.WebhookDeliveries
            .Where(d => d.Status == WebhookDeliveryStatus.Pending && (d.NextAttemptAt == null || d.NextAttemptAt <= now)
                        && db.WebhookEndpoints.Any(w => w.Id == d.WebhookEndpointId && w.IsActive))
            .OrderBy(d => d.CreatedAt).Take(50).ToListAsync(ct);
        if (due.Count == 0) return 0;
        var ids = due.Select(d => d.WebhookEndpointId).Distinct().ToList();
        var endpoints = await db.WebhookEndpoints.Where(w => ids.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var disabled = new List<WebhookEndpoint>();

        foreach (var d in due)
        {
            var w = endpoints[d.WebhookEndpointId];
            if (!w.IsActive) continue; // switched off earlier in this pass
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var headers = new Dictionary<string, string>
            {
                [WebhookSignature.Header] = WebhookSignature.Sign(w.Secret, d.Payload, ts),
                ["Ecosys-Event"] = d.EventType,
                ["Ecosys-Delivery"] = d.Id.ToString(),
            };
            var res = await sender.SendAsync(w.Url, d.Payload, headers, ct);
            d.Attempts++;
            d.LastStatusCode = res.StatusCode;
            d.DurationMs = res.DurationMs;
            if (res.Succeeded)
            {
                d.Status = WebhookDeliveryStatus.Delivered; d.DeliveredAt = DateTimeOffset.UtcNow; d.NextAttemptAt = null; d.LastError = null;
                w.ConsecutiveFailures = 0; w.LastSuccessAt = d.DeliveredAt;
                continue;
            }
            d.LastError = res.Error;
            w.LastFailureAt = DateTimeOffset.UtcNow;
            if (d.Attempts < MaxAttempts) { d.NextAttemptAt = DateTimeOffset.UtcNow + Backoff[d.Attempts - 1]; continue; }
            d.Status = WebhookDeliveryStatus.Failed; d.NextAttemptAt = null;
            if (++w.ConsecutiveFailures >= DisableAfter)
            {
                w.IsActive = false;
                w.DisabledReason = $"Switched off automatically after {DisableAfter} deliveries in a row failed. Last error: {res.Error}";
                disabled.Add(w);
            }
        }
        foreach (var w in disabled) await TellAdministratorsAsync(db, w, ct);
        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    private async Task TellAdministratorsAsync(PayrollDbContext db, WebhookEndpoint w, CancellationToken ct)
    {
        log.LogWarning("Webhook {Webhook} for tenant {Tenant} switched off after repeated failures", w.Id, w.TenantId);
        var admins = (await db.Users.Where(u => u.TenantId == w.TenantId && u.IsActive).ToListAsync(ct)).Where(u => u.Roles.Contains(Roles.TenantAdministrator));
        var portal = config["Payroll:PortalUrl"] ?? "https://payroll.ecosys.example";
        foreach (var u in admins)
            db.NotificationMessages.Add(new NotificationMessage
            {
                TenantId = w.TenantId, Channel = NotificationChannel.Email, Recipient = u.Email, RecipientName = u.DisplayName, Template = "integration.webhook.disabled",
                Subject = $"Webhook \"{w.Name}\" was switched off",
                Body = $"Ecosys Payroll could not deliver events to {w.Url} after repeated attempts, so the webhook \"{w.Name}\" has been switched off. "
                     + $"Check the receiving system, then switch the webhook back on under Settings, Integrations at {portal}/integrations. Events waiting to be sent will go out then.",
                EntityType = nameof(WebhookEndpoint), EntityId = w.Id, NextAttemptAt = DateTimeOffset.UtcNow,
            });
    }
}
