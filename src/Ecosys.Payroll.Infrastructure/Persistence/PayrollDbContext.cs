using System.Text.Json;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Compliance;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Finance;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Engine.Rules;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ecosys.Payroll.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL context. Tenant isolation is enforced with global query filters bound to the
/// request's <see cref="ITenantContext"/>; every tenant entity is also stamped on insert so a
/// service cannot accidentally write into another tenant.
/// </summary>
public class PayrollDbContext(DbContextOptions<PayrollDbContext> options, ITenantContext tenant, FieldEncryptor encryptor) : DbContext(options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<LegalEntity> LegalEntities => Set<LegalEntity>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<CostCentre> CostCentres => Set<CostCentre>();
    public DbSet<PayrollGroup> PayrollGroups => Set<PayrollGroup>();
    public DbSet<PayrollPeriod> PayrollPeriods => Set<PayrollPeriod>();

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmploymentRecord> EmploymentRecords => Set<EmploymentRecord>();
    public DbSet<EmployeeBankAccount> EmployeeBankAccounts => Set<EmployeeBankAccount>();
    public DbSet<EmployeeStatutoryDetails> EmployeeStatutoryDetails => Set<EmployeeStatutoryDetails>();

    public DbSet<SalaryComponent> SalaryComponents => Set<SalaryComponent>();
    public DbSet<SalaryStructure> SalaryStructures => Set<SalaryStructure>();
    public DbSet<SalaryStructureLine> SalaryStructureLines => Set<SalaryStructureLine>();
    public DbSet<EmployeeSalaryAssignment> EmployeeSalaryAssignments => Set<EmployeeSalaryAssignment>();
    public DbSet<EmployeeSalaryAssignmentLine> EmployeeSalaryAssignmentLines => Set<EmployeeSalaryAssignmentLine>();
    public DbSet<PayrollVariableInput> PayrollVariableInputs => Set<PayrollVariableInput>();
    public DbSet<StaffLoan> StaffLoans => Set<StaffLoan>();

    public DbSet<StatutoryRuleVersionRecord> StatutoryRuleVersions => Set<StatutoryRuleVersionRecord>();

    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<PayrollRunEmployee> PayrollRunEmployees => Set<PayrollRunEmployee>();
    public DbSet<PayrollResultLineRecord> PayrollResultLines => Set<PayrollResultLineRecord>();
    public DbSet<PayrollApproval> PayrollApprovals => Set<PayrollApproval>();
    public DbSet<PayrollPayment> PayrollPayments => Set<PayrollPayment>();
    public DbSet<PaymentBatch> PaymentBatches => Set<PaymentBatch>();
    public DbSet<Payslip> Payslips => Set<Payslip>();
    public DbSet<PayrollAdjustment> PayrollAdjustments => Set<PayrollAdjustment>();
    public DbSet<PayrollAuditLog> PayrollAuditLogs => Set<PayrollAuditLog>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<NotificationMessage> NotificationMessages => Set<NotificationMessage>();

    public DbSet<PayrollJournal> PayrollJournals => Set<PayrollJournal>();
    public DbSet<PayrollJournalLine> PayrollJournalLines => Set<PayrollJournalLine>();
    public DbSet<GlAccountMapping> GlAccountMappings => Set<GlAccountMapping>();

    public DbSet<LeaveType> LeaveTypes => Set<LeaveType>();
    public DbSet<EmployeeLeaveBalance> EmployeeLeaveBalances => Set<EmployeeLeaveBalance>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();

    public DbSet<User> Users => Set<User>();

    /// <summary>Tenant filter is bypassed only for platform administration (never for payroll data access).</summary>
    public bool IgnoreTenantFilter { get; set; }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("pgcrypto");

        // ---- Shared conventions ------------------------------------------------------------
        foreach (var et in b.Model.GetEntityTypes())
        {
            // Keys are generated client-side (Guid.NewGuid in Entity); tell EF so graph-discovered entities are inserted, not updated.
            foreach (var p in et.FindPrimaryKey()?.Properties.Where(p => p.ClrType == typeof(Guid)) ?? [])
                p.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            foreach (var p in et.GetProperties().Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
                p.SetColumnType("numeric(18,4)");
            foreach (var p in et.GetProperties().Where(p => p.ClrType.IsEnum || Nullable.GetUnderlyingType(p.ClrType)?.IsEnum == true))
                p.SetProviderClrType(typeof(string));
        }

        b.Entity<Tenant>(e => { e.HasIndex(x => x.Slug).IsUnique(); e.HasQueryFilter(x => !x.IsDeleted); });

        // ---- Organisation -----------------------------------------------------------------
        TenantEntity<LegalEntity>(b, e =>
        {
            e.HasIndex(x => new { x.TenantId, x.TaxPin }).IsUnique();
            e.OwnsOne(x => x.Settings, s => s.ToJson());
        });
        Scoped<Branch>(b, e => e.HasIndex(x => new { x.LegalEntityId, x.Code }).IsUnique());
        Scoped<Department>(b, e => e.HasIndex(x => new { x.LegalEntityId, x.Code }).IsUnique());
        Scoped<CostCentre>(b, e => e.HasIndex(x => new { x.LegalEntityId, x.Code }).IsUnique());
        Scoped<PayrollGroup>(b, e => e.HasIndex(x => new { x.LegalEntityId, x.Code }).IsUnique());
        Scoped<PayrollPeriod>(b, e =>
        {
            e.HasIndex(x => new { x.PayrollGroupId, x.PeriodStart, x.PeriodEnd }).IsUnique();
            e.HasOne<PayrollGroup>().WithMany().HasForeignKey(x => x.PayrollGroupId);
        });

        // ---- Employees --------------------------------------------------------------------
        Scoped<Employee>(b, e =>
        {
            e.HasIndex(x => new { x.LegalEntityId, x.EmployeeNumber }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => x.PayrollGroupId);
            e.Property(x => x.NationalId).HasConversion(encryptor.Converter());
            e.Property(x => x.PassportNumber).HasConversion(encryptor.Converter());
            e.HasOne<PayrollGroup>().WithMany().HasForeignKey(x => x.PayrollGroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<CostCentre>().WithMany().HasForeignKey(x => x.CostCentreId).OnDelete(DeleteBehavior.SetNull);
            e.Ignore(x => x.FullName);
        });
        Scoped<EmploymentRecord>(b, e => { e.HasIndex(x => new { x.EmployeeId, x.EffectiveFrom }); e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId); });
        Scoped<EmployeeBankAccount>(b, e =>
        {
            e.HasIndex(x => x.EmployeeId);
            e.Property(x => x.AccountNumber).HasConversion(encryptor.RequiredConverter());
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId);
        });
        Scoped<EmployeeStatutoryDetails>(b, e =>
        {
            e.HasIndex(x => x.EmployeeId).IsUnique();
            e.Property(x => x.KraPin).HasConversion(encryptor.Converter());
            e.Property(x => x.StatutoryExemptions).HasColumnType("jsonb");
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId);
        });

        // ---- Compensation -----------------------------------------------------------------
        Scoped<SalaryComponent>(b, e => e.HasIndex(x => new { x.LegalEntityId, x.Code }).IsUnique());
        Scoped<SalaryStructure>(b, e =>
        {
            e.HasIndex(x => new { x.LegalEntityId, x.Code }).IsUnique();
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SalaryStructureId).OnDelete(DeleteBehavior.Cascade);
        });
        TenantEntity<SalaryStructureLine>(b, e => e.HasOne<SalaryComponent>().WithMany().HasForeignKey(x => x.SalaryComponentId));
        Scoped<EmployeeSalaryAssignment>(b, e =>
        {
            e.HasIndex(x => new { x.EmployeeId, x.EffectiveFrom });
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId);
            e.HasOne<SalaryStructure>().WithMany().HasForeignKey(x => x.SalaryStructureId);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.EmployeeSalaryAssignmentId).OnDelete(DeleteBehavior.Cascade);
        });
        TenantEntity<EmployeeSalaryAssignmentLine>(b, e => e.HasOne<SalaryComponent>().WithMany().HasForeignKey(x => x.SalaryComponentId));
        Scoped<PayrollVariableInput>(b, e =>
        {
            e.HasIndex(x => new { x.PayrollRunId, x.EmployeeId });
            e.HasOne<PayrollRun>().WithMany().HasForeignKey(x => x.PayrollRunId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<SalaryComponent>().WithMany().HasForeignKey(x => x.SalaryComponentId);
        });
        Scoped<StaffLoan>(b, e => { e.HasIndex(x => new { x.EmployeeId, x.Status }); e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId); });

        // ---- Compliance -------------------------------------------------------------------
        b.Entity<StatutoryRuleVersionRecord>(e =>
        {
            e.ToTable("statutory_rule_versions");
            e.HasIndex(x => new { x.Jurisdiction, x.RuleCode, x.Version, x.TenantId }).IsUnique();
            e.HasIndex(x => new { x.Jurisdiction, x.EffectiveFrom, x.EffectiveTo });
            e.Property(x => x.Parameters).HasColumnType("jsonb").HasConversion(JsonConverter(() => new StatutoryRuleParameters()));
            e.Property(x => x.Rounding).HasColumnType("jsonb").HasConversion(JsonConverter(() => RoundingRule.Default));
            e.Property(x => x.ApplicableCategories).HasColumnType("jsonb");
            e.HasQueryFilter(x => !x.IsDeleted && (IgnoreTenantFilter || x.TenantId == null || x.TenantId == tenant.TenantId));
        });

        // ---- Payroll ----------------------------------------------------------------------
        Scoped<PayrollRun>(b, e =>
        {
            e.HasIndex(x => new { x.LegalEntityId, x.Reference }).IsUnique();
            e.HasIndex(x => new { x.PayrollGroupId, x.PayrollPeriodId, x.Status });
            e.HasOne<PayrollGroup>().WithMany().HasForeignKey(x => x.PayrollGroupId);
            e.HasOne<PayrollPeriod>().WithMany().HasForeignKey(x => x.PayrollPeriodId);
            e.HasMany(x => x.Employees).WithOne().HasForeignKey(x => x.PayrollRunId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.IsLocked);
        });
        Scoped<PayrollRunEmployee>(b, e =>
        {
            e.HasIndex(x => new { x.PayrollRunId, x.EmployeeId }).IsUnique();
            e.HasIndex(x => x.EmployeeId);
            e.Property(x => x.Issues).HasColumnType("jsonb").HasConversion(JsonConverter(() => new List<PayrollValidationIssue>()));
            e.Property(x => x.StatutoryRuleVersionIds).HasColumnType("jsonb");
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PayrollRunEmployeeId).OnDelete(DeleteBehavior.Cascade);
        });
        TenantEntity<PayrollResultLineRecord>(b, e => e.HasIndex(x => new { x.PayrollRunEmployeeId, x.Sequence }));
        Scoped<PayrollApproval>(b, e => { e.HasIndex(x => x.PayrollRunId); e.HasOne<PayrollRun>().WithMany().HasForeignKey(x => x.PayrollRunId); });
        Scoped<PaymentBatch>(b, e => { e.HasIndex(x => x.PayrollRunId); e.HasOne<PayrollRun>().WithMany().HasForeignKey(x => x.PayrollRunId); });
        Scoped<PayrollPayment>(b, e =>
        {
            e.HasIndex(x => new { x.PayrollRunId, x.Status });
            e.Property(x => x.AccountNumber).HasConversion(encryptor.Converter());
            e.HasOne<PayrollRun>().WithMany().HasForeignKey(x => x.PayrollRunId);
            e.HasOne<PaymentBatch>().WithMany().HasForeignKey(x => x.PaymentBatchId);
        });
        Scoped<Payslip>(b, e =>
        {
            e.HasIndex(x => new { x.EmployeeId, x.PeriodEnd });
            e.HasIndex(x => x.PayrollRunEmployeeId).IsUnique();
            e.HasOne<PayrollRun>().WithMany().HasForeignKey(x => x.PayrollRunId);
        });
        Scoped<PayrollAdjustment>(b, e => e.HasIndex(x => new { x.EmployeeId, x.Status }));
        TenantEntity<PayrollAuditLog>(b, e => { e.HasIndex(x => new { x.TenantId, x.OccurredAt }); e.HasIndex(x => new { x.EntityType, x.EntityId }); });
        Scoped<AttendanceRecord>(b, e =>
        {
            e.HasIndex(x => new { x.PayrollRunId, x.EmployeeId }).IsUnique();
            e.HasOne<PayrollRun>().WithMany().HasForeignKey(x => x.PayrollRunId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId);
        });
        TenantEntity<NotificationMessage>(b, e =>
        {
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.Property(x => x.Recipient).HasConversion(encryptor.RequiredConverter());
        });

        // ---- Finance ----------------------------------------------------------------------
        Scoped<PayrollJournal>(b, e =>
        {
            e.HasIndex(x => x.PayrollRunId);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PayrollJournalId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.IsBalanced);
        });
        TenantEntity<PayrollJournalLine>(b, _ => { });
        Scoped<GlAccountMapping>(b, e => e.HasIndex(x => new { x.LegalEntityId, x.SourceCode }).IsUnique());

        // ---- Leave ------------------------------------------------------------------------
        Scoped<LeaveType>(b, e => e.HasIndex(x => new { x.LegalEntityId, x.Code }).IsUnique());
        Scoped<EmployeeLeaveBalance>(b, e => { e.HasIndex(x => new { x.EmployeeId, x.LeaveTypeId, x.Year }).IsUnique(); e.Ignore(x => x.Available); });
        Scoped<LeaveRequest>(b, e => e.HasIndex(x => new { x.EmployeeId, x.Status }));

        // ---- Identity ---------------------------------------------------------------------
        TenantEntity<User>(b, e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();
            e.Property(x => x.Roles).HasColumnType("jsonb");
            e.Property(x => x.LegalEntityIds).HasColumnType("jsonb");
            e.Property(x => x.MfaSecret).HasConversion(encryptor.Converter());
        });
    }

    private void TenantEntity<T>(ModelBuilder b, Action<EntityTypeBuilder<T>> configure) where T : TenantEntity
    {
        b.Entity<T>(e =>
        {
            e.HasIndex(x => x.TenantId);
            e.HasQueryFilter(x => !x.IsDeleted && (IgnoreTenantFilter || x.TenantId == tenant.TenantId));
            configure(e);
        });
    }

    private void Scoped<T>(ModelBuilder b, Action<EntityTypeBuilder<T>> configure) where T : LegalEntityScopedEntity
    {
        TenantEntity<T>(b, e =>
        {
            e.HasIndex(x => x.LegalEntityId);
            e.HasOne<LegalEntity>().WithMany().HasForeignKey(x => x.LegalEntityId).OnDelete(DeleteBehavior.Restrict);
            configure(e);
        });
    }

    private static ValueConverter<T, string> JsonConverter<T>(Func<T> fallback) where T : class =>
        new(v => JsonSerializer.Serialize(v, Json), v => JsonSerializer.Deserialize<T>(v, Json) ?? fallback());

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.CreatedBy ??= tenant.UserId;
                    if (entry.Entity is TenantEntity te)
                    {
                        if (te.TenantId == Guid.Empty) te.TenantId = tenant.RequireTenant();
                        else if (!IgnoreTenantFilter && tenant.TenantId is { } tid && te.TenantId != tid)
                            throw new ForbiddenException("Cross-tenant write rejected.");
                    }
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = tenant.UserId;
                    entry.Property(x => x.CreatedAt).IsModified = false;
                    entry.Property(x => x.CreatedBy).IsModified = false;
                    break;
                case EntityState.Deleted:
                    // Soft delete for historical retention (PRD §9.2)
                    entry.State = EntityState.Modified;
                    entry.Entity.IsDeleted = true;
                    entry.Entity.DeletedAt = now;
                    entry.Entity.UpdatedBy = tenant.UserId;
                    break;
            }
        }
        return SaveWithReferenceGuardAsync(ct);
    }

    /// <summary>
    /// Foreign keys are not tenant-aware, so a client-supplied LegalEntityId from another tenant would satisfy
    /// the FK. Every written legal-entity-scoped row must reference a legal entity visible to this tenant.
    /// </summary>
    private async Task<int> SaveWithReferenceGuardAsync(CancellationToken ct)
    {
        if (!IgnoreTenantFilter)
        {
            var referenced = ChangeTracker.Entries<LegalEntityScopedEntity>()
                .Where(e => e.State is EntityState.Added || (e.State == EntityState.Modified && e.Property(x => x.LegalEntityId).IsModified))
                .Select(e => e.Entity.LegalEntityId).Distinct().ToList();
            if (referenced.Count > 0)
            {
                var pending = ChangeTracker.Entries<LegalEntity>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.Id).ToHashSet();
                var unresolved = referenced.Where(id => !pending.Contains(id)).ToList();
                var visible = unresolved.Count == 0 ? [] : await LegalEntities.Where(l => unresolved.Contains(l.Id)).Select(l => l.Id).ToListAsync(ct);
                if (unresolved.Except(visible).Any()) throw new ForbiddenException("Referenced legal entity does not belong to this tenant.");
            }
        }
        return await base.SaveChangesAsync(ct);
    }
}
