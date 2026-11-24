using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Integration;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Ecosys.Payroll.Infrastructure.Services.Integration;

public sealed record WebhookRequest(string Name, string Url, List<string>? Events, bool? IsActive);

public sealed record WebhookView(Guid Id, string Name, string Url, IReadOnlyList<string> Events, bool IsActive, string? DisabledReason,
    int ConsecutiveFailures, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, DateTimeOffset CreatedAt)
{
    public static WebhookView From(WebhookEndpoint w) => new(w.Id, w.Name, w.Url, w.Events, w.IsActive, w.DisabledReason, w.ConsecutiveFailures, w.LastSuccessAt, w.LastFailureAt, w.CreatedAt);
}

public sealed record WebhookWithSecret(WebhookView Webhook, string Secret);

public sealed record DeliveryView(Guid Id, Guid WebhookEndpointId, string EventType, Guid? EventId, WebhookDeliveryStatus Status, int Attempts, int? LastStatusCode,
    string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt, DateTimeOffset? NextAttemptAt, int? DurationMs, string Payload);

/// <summary>Event payloads: the same JSON in webhooks and in the events feed. Ids and links only, never pay figures.</summary>
public static class EventPayload
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static object Shape(IntegrationEvent e, string? baseUrl) => new
    {
        id = e.Id, type = e.Type, sequence = e.Sequence, occurredAt = e.CreatedAt, companyId = e.LegalEntityId,
        data = new
        {
            @object = e.EntityType, id = e.EntityId,
            url = IntegrationEventTypes.Resources.Url(e.EntityType, e.EntityId) is { } path ? $"{baseUrl?.TrimEnd('/')}{path}" : null,
        },
    };

    public static string Serialize(IntegrationEvent e, string? baseUrl) => JsonSerializer.Serialize(Shape(e, baseUrl), Json);
}

/// <summary>
/// Signs a webhook body: <c>Ecosys-Signature: t={unix seconds},v1={hex HMAC-SHA256 of "{t}.{body}"}</c>. The receiver
/// recomputes it with the endpoint's secret and rejects old timestamps, which stops forged and replayed calls.
/// </summary>
public static class WebhookSignature
{
    public const string Header = "Ecosys-Signature";

    public static string Sign(string secret, string body, long timestamp) => $"t={timestamp},v1={Mac(secret, body, timestamp)}";

    private static string Mac(string secret, string body, long timestamp) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    /// <summary>What a receiver does: true when the header matches the body and is no older than <paramref name="tolerance"/>.</summary>
    public static bool Verify(string secret, string body, string header, DateTimeOffset now, TimeSpan tolerance)
    {
        var parts = header.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        if (!parts.TryGetValue("t", out var t) || !long.TryParse(t, out var ts) || !parts.TryGetValue("v1", out var given)) return false;
        if (Math.Abs(now.ToUnixTimeSeconds() - ts) > tolerance.TotalSeconds) return false;
        var expected = Mac(secret, body, ts);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(given));
    }
}

/// <summary>
/// Webhook addresses are typed in by customers and called from our servers, so they must not reach our own network:
/// only public addresses, over HTTPS. Checked when saved and again on every connection (DNS can change).
/// </summary>
public static class WebhookAddress
{
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 unique local
        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => true,
            100 => b[1] is >= 64 and <= 127,                    // carrier-grade NAT
            169 => b[1] == 254,                                // link-local, cloud metadata
            172 => b[1] is >= 16 and <= 31,
            192 => (b[1] == 168) || (b[1] == 0 && b[2] is 0 or 2) || (b[1] == 88 && b[2] == 99),
            198 => b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100),
            203 => b[1] == 0 && b[2] == 113,
            >= 224 => true,                                    // multicast and reserved
            _ => false,
        };
    }

    /// <summary>Returns a problem with the address, or null if it can be used.</summary>
    public static string? Problem(string? url, bool allowPrivate)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)) return "Enter the full address, starting with https://.";
        if (u.Scheme != Uri.UriSchemeHttps && !(allowPrivate && u.Scheme == Uri.UriSchemeHttp)) return "The address must start with https://.";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "Don't put a user name or password in the address. Check the signature header instead.";
        if (u.AbsoluteUri.Length > 500) return "The address is too long.";
        if (allowPrivate) return null;
        if (u.IsLoopback || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || !u.Host.Contains('.'))
            return "The address must be on the public internet.";
        if (IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && IsBlocked(ip)) return "The address must be on the public internet.";
        return null;
    }
}

/// <summary>Webhook endpoints for an organisation: add, change, switch off, rotate the secret, test and resend.</summary>
public sealed class WebhookService(PayrollDbContext db, ITenantContext ctx, AuditService audit, IConfiguration config)
{
    public const int MaxEndpoints = 10;
    private bool AllowPrivate => config.GetValue("Integrations:AllowPrivateWebhookTargets", false);
    private string? BaseUrl => config["Payroll:PortalUrl"];

    public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<IReadOnlyList<WebhookView>> ListAsync(CancellationToken ct)
    {
        ctx.Require(Permissions.IntegrationsManage);
        return (await db.WebhookEndpoints.OrderBy(w => w.CreatedAt).ToListAsync(ct)).Select(WebhookView.From).ToList();
    }

    private void Validate(WebhookRequest req, out string name, out string url, out List<string> events)
    {
        name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > 80) throw new DomainException("Give the webhook a name of up to 80 characters.", "VALIDATION");
        url = req.Url?.Trim() ?? "";
        if (WebhookAddress.Problem(url, AllowPrivate) is { } problem) throw new DomainException(problem, "VALIDATION");
        events = (req.Events ?? []).Distinct().ToList();
        var unknown = events.Except(IntegrationEventTypes.All).ToList();
        if (unknown.Count > 0) throw new DomainException($"Unknown event type: {string.Join(", ", unknown)}.", "VALIDATION");
    }

    public async Task<WebhookWithSecret> CreateAsync(WebhookRequest req, CancellationToken ct)
    {
        ctx.Require(Permissions.IntegrationsManage);
        Validate(req, out var name, out var url, out var events);
        if (await db.WebhookEndpoints.CountAsync(ct) >= MaxEndpoints) throw new DomainException($"An organisation can have up to {MaxEndpoints} webhooks.", "LIMIT");
        var w = new WebhookEndpoint { Name = name, Url = url, Events = events, Secret = NewSecret() };
        db.WebhookEndpoints.Add(w);
        audit.Record("integration.webhook.created", nameof(WebhookEndpoint), w.Id, new { w.Name, w.Url, w.Events });
        await db.SaveChangesAsync(ct);
        return new WebhookWithSecret(WebhookView.From(w), w.Secret);
    }

    public async Task<WebhookView> UpdateAsync(Guid id, WebhookRequest req, CancellationToken ct)
    {
        ctx.Require(Permissions.IntegrationsManage);
        var w = await LoadAsync(id, ct);
        Validate(req, out var name, out var url, out var events);
        w.Name = name; w.Url = url; w.Events = events;
        if (req.IsActive is { } active && active != w.IsActive)
        {
            w.IsActive = active;
            // Switching back on starts afresh; deliveries that were waiting are sent.
            if (active) { w.DisabledReason = null; w.ConsecutiveFailures = 0; }
            else w.DisabledReason = $"Switched off by {ctx.UserName}";
        }
        audit.Record("integration.webhook.updated", nameof(WebhookEndpoint), w.Id, new { w.Name, w.Url, w.Events, w.IsActive });
        await db.SaveChangesAsync(ct);
        return WebhookView.From(w);
    }

    public async Task<WebhookWithSecret> RotateSecretAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.IntegrationsManage);
        var w = await LoadAsync(id, ct);
        w.Secret = NewSecret();
        audit.Record("integration.webhook.secret_rotated", nameof(WebhookEndpoint), w.Id, new { w.Name });
        await db.SaveChangesAsync(ct);
        return new WebhookWithSecret(WebhookView.From(w), w.Secret);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.IntegrationsManage);
        var w = await LoadAsync(id, ct);
        db.WebhookEndpoints.Remove(w);
        // Nothing more goes to a removed endpoint.
        foreach (var d in await db.WebhookDeliveries.Where(d => d.WebhookEndpointId == id && d.Status == WebhookDeliveryStatus.Pending).ToListAsync(ct))
        { d.Status = WebhookDeliveryStatus.Failed; d.LastError = "Webhook removed"; d.NextAttemptAt = null; }
        audit.Record("integration.webhook.deleted", nameof(WebhookEndpoint), w.Id, new { w.Name, w.Url });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Queues a <c>ping</c> event to the endpoint, so the receiver can be checked before real events flow.</summary>
    public async Task<DeliveryView> SendTestAsync(Guid id, CancellationToken ct)
    {
        ctx.Require(Permissions.IntegrationsManage);
        var w = await LoadAsync(id, ct);
        var payload = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid(), type = IntegrationEventTypes.Ping, sequence = (long?)null, occurredAt = DateTimeOffset.UtcNow, companyId = (Guid?)null,
            data = new { @object = "webhook", id = w.Id, url = (string?)null, message = $"Test event from Ecosys Payroll, sent by {ctx.UserName}." },
        }, EventPayload.Json);
        var d = new WebhookDelivery { WebhookEndpointId = w.Id, EventType = IntegrationEventTypes.Ping, Payload = payload, NextAttemptAt = DateTimeOffset.UtcNow };
        db.WebhookDeliveries.Add(d);
        await db.SaveChangesAsync(ct);
        return View(d);
    }

    /// <summary>Sends a past delivery again, as a new delivery with the same body (and therefore the same event id).</summary>
    public async Task<DeliveryView> RedeliverAsync(Guid deliveryId, CancellationToken ct)
    {
        ctx.Require(Permissions.IntegrationsManage);
        var old = await db.WebhookDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct) ?? throw new NotFoundException(nameof(WebhookDelivery), deliveryId);
        var w = await LoadAsync(old.WebhookEndpointId, ct);
        if (!w.IsActive) throw new InvalidStateException("Switch the webhook back on before sending events to it.");
        var d = new WebhookDelivery { WebhookEndpointId = w.Id, IntegrationEventId = old.IntegrationEventId, EventType = old.EventType, Payload = old.Payload, NextAttemptAt = DateTimeOffset.UtcNow };
        db.WebhookDeliveries.Add(d);
        audit.Record("integration.webhook.redelivered", nameof(WebhookDelivery), d.Id, new { from = old.Id, old.EventType });
        await db.SaveChangesAsync(ct);
        return View(d);
    }

    public async Task<IReadOnlyList<DeliveryView>> DeliveriesAsync(Guid id, int limit, CancellationToken ct)
    {
        ctx.Require(Permissions.IntegrationsManage);
        await LoadAsync(id, ct);
        return (await db.WebhookDeliveries.Where(d => d.WebhookEndpointId == id).OrderByDescending(d => d.CreatedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct))
            .Select(View).ToList();
    }

    private async Task<WebhookEndpoint> LoadAsync(Guid id, CancellationToken ct) =>
        await db.WebhookEndpoints.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException(nameof(WebhookEndpoint), id);

    private static DeliveryView View(WebhookDelivery d) => new(d.Id, d.WebhookEndpointId, d.EventType, d.IntegrationEventId, d.Status, d.Attempts, d.LastStatusCode,
        d.LastError, d.CreatedAt, d.DeliveredAt, d.NextAttemptAt, d.DurationMs, d.Payload);
}
