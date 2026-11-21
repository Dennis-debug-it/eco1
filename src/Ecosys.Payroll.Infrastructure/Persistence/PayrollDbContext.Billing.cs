using Ecosys.Payroll.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Persistence;

public partial class PayrollDbContext
{
    public const string InvoiceNumberSequence = "subscription_invoice_number";

    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SubscriptionInvoice> SubscriptionInvoices => Set<SubscriptionInvoice>();
    public DbSet<SubscriptionPayment> SubscriptionPayments => Set<SubscriptionPayment>();

    private void ConfigureBilling(ModelBuilder b)
    {
        b.HasSequence<long>(InvoiceNumberSequence).StartsAt(1001);

        // Platform price list: readable by every tenant (sign-up, plan changes), written only by the operator.
        b.Entity<Plan>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Features).HasColumnType("jsonb");
            e.HasQueryFilter(x => !x.IsDeleted);
        });
        TenantEntity<Subscription>(b, e =>
        {
            e.HasIndex(x => x.TenantId).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.IsReadOnly);
        });
        TenantEntity<SubscriptionInvoice>(b, e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.SubscriptionId, x.PeriodStart }).IsUnique();
            e.HasIndex(x => new { x.Status, x.DueOn });
            e.Property(x => x.Lines).HasColumnType("jsonb").HasConversion(JsonConverter(() => new List<InvoiceLine>()), JsonComparer<List<InvoiceLine>>());
            e.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.Balance);
        });
        TenantEntity<SubscriptionPayment>(b, e =>
        {
            e.HasIndex(x => x.InvoiceId);
            e.HasIndex(x => x.Reference);
            e.HasOne<SubscriptionInvoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>Next platform-wide invoice number, e.g. INV-001001. Gap-free numbering is not guaranteed (a rolled-back issue skips one).</summary>
    public async Task<string> NextInvoiceNumberAsync(CancellationToken ct) =>
        $"INV-{await Database.SqlQueryRaw<long>($"SELECT nextval('{InvoiceNumberSequence}') AS \"Value\"").SingleAsync(ct):000000}";
}
