using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Migration;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Persistence;

/// <summary>Moving a customer in from another payroll system: import batches and the pay history carried across.</summary>
public partial class PayrollDbContext
{
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<PayrollHistoryMonth> PayrollHistoryMonths => Set<PayrollHistoryMonth>();

    private void ConfigureMigration(ModelBuilder b)
    {
        Scoped<ImportBatch>(b, e =>
        {
            e.HasIndex(x => new { x.LegalEntityId, x.CreatedAt });
            e.Property(x => x.Summary).HasColumnType("jsonb").HasConversion(JsonConverter(() => new Dictionary<string, int>()), JsonComparer<Dictionary<string, int>>());
            e.Property(x => x.Records).HasColumnType("jsonb").HasConversion(JsonConverter(() => new ImportRecords()), JsonComparer<ImportRecords>());
        });
        Scoped<PayrollHistoryMonth>(b, e =>
        {
            e.HasIndex(x => new { x.EmployeeId, x.Year, x.Month }).IsUnique();
            e.HasIndex(x => new { x.LegalEntityId, x.Year });
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.ImportBatchId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
