using Ecosys.Payroll.Domain.Integration;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Persistence;

/// <summary>Connecting other systems: API keys, the events feed, webhooks and idempotent replays.</summary>
public partial class PayrollDbContext
{
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<IntegrationEvent> IntegrationEvents => Set<IntegrationEvent>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    private void ConfigureIntegration(Microsoft.EntityFrameworkCore.ModelBuilder b)
    {
        TenantEntity<ApiClient>(b, e =>
        {
            // Keys are looked up before the tenant is known, so the hash is unique across all organisations.
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.Property(x => x.Permissions).HasColumnType("jsonb");
            e.Property(x => x.LegalEntityIds).HasColumnType("jsonb");
        });
        TenantEntity<IntegrationEvent>(b, e =>
        {
            e.Property(x => x.Sequence).UseIdentityAlwaysColumn();
            e.HasIndex(x => x.Sequence).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Sequence });
            e.HasIndex(x => x.FannedOut).HasFilter("fanned_out = false");
        });
        TenantEntity<WebhookEndpoint>(b, e =>
        {
            e.Property(x => x.Events).HasColumnType("jsonb");
            e.Property(x => x.Secret).HasConversion(encryptor.RequiredConverter());
        });
        TenantEntity<WebhookDelivery>(b, e =>
        {
            e.HasIndex(x => new { x.WebhookEndpointId, x.CreatedAt });
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(x => x.WebhookEndpointId).OnDelete(DeleteBehavior.Restrict);
        });
        TenantEntity<IdempotencyRecord>(b, e =>
        {
            e.HasIndex(x => new { x.ApiClientId, x.Key }).IsUnique();
            e.HasIndex(x => x.ExpiresAt);
        });
    }
}
