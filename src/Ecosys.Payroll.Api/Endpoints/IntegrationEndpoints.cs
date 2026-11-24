using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Domain.Integration;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Services.Integration;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

/// <summary>Settings, Integrations: API keys and webhooks, for tenant administrators.</summary>
public static class IntegrationEndpoints
{
    public static void MapIntegrations(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/integrations").RequireAuthorization();

        g.MapGet("/options", () => Results.Ok(new
        {
            permissions = ApiScopes.Grantable.Select(p => new { code = p, description = ApiScopes.Descriptions[p] }),
            events = IntegrationEventTypes.All.Select(e => new { code = e, description = IntegrationEventTypes.Descriptions[e] }),
            maxWebhooks = WebhookService.MaxEndpoints,
            retries = WebhookDispatcher.Backoff.Select(b => b.ToString()),
        }));

        g.MapGet("/keys", async (ApiKeyService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));
        g.MapPost("/keys", async (ApiKeyRequest req, ApiKeyService svc, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(req, ct);
            return Results.Created($"/api/payroll/integrations/keys/{created.Client.Id}", created);
        });
        g.MapPost("/keys/{id:guid}/revoke", async (Guid id, ApiKeyService svc, PayrollDbContext db, ApiKeyCache cache, CancellationToken ct) =>
        {
            var view = await svc.RevokeAsync(id, ct);
            cache.Invalidate(await db.ApiClients.Where(c => c.Id == id).Select(c => c.KeyHash).FirstAsync(ct));
            return Results.Ok(view);
        });

        g.MapGet("/webhooks", async (WebhookService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));
        g.MapPost("/webhooks", async (WebhookRequest req, WebhookService svc, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(req, ct);
            return Results.Created($"/api/payroll/integrations/webhooks/{created.Webhook.Id}", created);
        });
        g.MapPut("/webhooks/{id:guid}", async (Guid id, WebhookRequest req, WebhookService svc, CancellationToken ct) => Results.Ok(await svc.UpdateAsync(id, req, ct)));
        g.MapDelete("/webhooks/{id:guid}", async (Guid id, WebhookService svc, CancellationToken ct) => { await svc.DeleteAsync(id, ct); return Results.NoContent(); });
        g.MapPost("/webhooks/{id:guid}/rotate-secret", async (Guid id, WebhookService svc, CancellationToken ct) => Results.Ok(await svc.RotateSecretAsync(id, ct)));
        g.MapPost("/webhooks/{id:guid}/test", async (Guid id, WebhookService svc, CancellationToken ct) => Results.Ok(await svc.SendTestAsync(id, ct)));
        g.MapGet("/webhooks/{id:guid}/deliveries", async (Guid id, int? limit, WebhookService svc, CancellationToken ct) => Results.Ok(await svc.DeliveriesAsync(id, limit ?? 50, ct)));
        g.MapPost("/deliveries/{id:guid}/redeliver", async (Guid id, WebhookService svc, CancellationToken ct) => Results.Ok(await svc.RedeliverAsync(id, ct)));
    }
}
