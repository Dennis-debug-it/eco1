using System.Text.Json;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Migration;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Migration;

/// <summary>
/// Stops money and filings moving while a company is still being migrated. Called before approving a run, making or
/// confirming a payment file, finalising, and exporting a statutory return as a file.
/// </summary>
public sealed class MigrationGuard(PayrollDbContext db, ITenantContext tenant)
{
    public async Task EnsureLiveAsync(string action, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } id) return;
        var inMigration = await db.Tenants.Where(t => t.Id == id).Select(t => t.MigrationStartedAt != null && t.GoLiveSignedAt == null).FirstOrDefaultAsync(ct);
        if (inMigration)
            throw new InvalidStateException($"This organisation is still moving from its previous payroll system, so you can't {action} yet. Runs can be calculated and reconciled; " +
                                            "paying, finalising and filing open once go-live is signed (Settings, Data migration).");
    }
}

public sealed record MigrationCheck(string Key, string Label, bool Done, string Detail, bool Required);

public sealed record MigrationStatus(bool InMigration, bool Live, DateTimeOffset? StartedAt, string? StartedBy, DateTimeOffset? GoLiveSignedAt, string? GoLiveSignedBy,
    DateOnly? GoLiveFrom, IReadOnlyList<MigrationCheck> Checklist, int ParallelRunsToDiscard);

/// <summary>
/// Migration mode for one organisation: switched on while its data is loaded and parallel runs are reconciled against
/// the old system; switched off for good when someone with organisation rights signs go-live. Signing discards the
/// parallel runs (the old system paid those months) and releases any pay items they had claimed.
/// </summary>
public sealed class MigrationModeService(PayrollDbContext db, ITenantContext tenant, AuditService audit)
{
    private async Task<Tenant> CurrentAsync(CancellationToken ct) =>
        await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenant.RequireTenant(), ct) ?? throw new NotFoundException(nameof(Tenant), tenant.RequireTenant());

    public async Task<MigrationStatus> StatusAsync(CancellationToken ct = default)
    {
        tenant.Require(Permissions.EmployeesRead);
        var t = await CurrentAsync(ct);
        var checklist = await ChecklistAsync(t, ct);
        var discard = t.InMigration ? await ParallelRuns(null).CountAsync(ct) : 0;
        return new MigrationStatus(t.InMigration, t.GoLiveSignedAt is not null, t.MigrationStartedAt, t.MigrationStartedBy, t.GoLiveSignedAt, t.GoLiveSignedBy, t.GoLiveFrom, checklist, discard);
    }

    public async Task<MigrationStatus> StartAsync(CancellationToken ct = default)
    {
        tenant.Require(Permissions.OrganisationManage);
        var t = await CurrentAsync(ct);
        if (t.GoLiveSignedAt is not null) throw new InvalidStateException("This organisation has already gone live on Ecosys.");
        if (t.MigrationStartedAt is not null) return await StatusAsync(ct);
        if (await db.PayrollRuns.AnyAsync(r => r.ApprovedAt != null || r.Status == PayrollRunStatus.Approved || r.Status == PayrollRunStatus.PaymentProcessing || r.Status == PayrollRunStatus.Paid || r.Status == PayrollRunStatus.Finalised || r.Status == PayrollRunStatus.Reversed, ct))
            throw new InvalidStateException("Ecosys has already approved payroll for this organisation, so it isn't migrating any more. Migration mode is for an organisation not yet paid through Ecosys.");
        t.MigrationStartedAt = DateTimeOffset.UtcNow;
        t.MigrationStartedBy = tenant.UserName;
        Audit(t, "migration.started", new { });
        await db.SaveChangesAsync(ct);
        return await StatusAsync(ct);
    }

    /// <param name="firstPeriodStart">First day of the first pay period Ecosys pays. Unapproved runs for earlier periods are the parallel runs.</param>
    /// <param name="acceptOpenItems">The signer has seen the required checklist items that are still open and goes ahead anyway.</param>
    public async Task<MigrationStatus> GoLiveAsync(DateOnly firstPeriodStart, bool acceptOpenItems, CancellationToken ct = default)
    {
        tenant.Require(Permissions.OrganisationManage);
        var t = await CurrentAsync(ct);
        if (!t.InMigration) throw new InvalidStateException("This organisation isn't in migration mode.");
        var open = (await ChecklistAsync(t, ct)).Where(c => c.Required && !c.Done).ToList();
        if (open.Count > 0 && !acceptOpenItems)
            throw new DomainException($"Still open: {string.Join("; ", open.Select(c => c.Label))}. Confirm you want to go live anyway.", "CHECKLIST_OPEN");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var parallel = await ParallelRuns(firstPeriodStart).ToListAsync(ct);
        var ids = parallel.Select(r => r.Id).ToList();
        // The old system paid these months: the runs were only for comparison. Pay items they claimed go back to the queue.
        await db.ScheduledPayItems.Where(i => i.ProcessedInRunId != null && ids.Contains(i.ProcessedInRunId.Value) && i.Status == PayItemStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ProcessedInRunId, (Guid?)null), ct);
        foreach (var r in parallel) { r.IsDeleted = true; r.DeletedAt = DateTimeOffset.UtcNow; }

        t.GoLiveSignedAt = DateTimeOffset.UtcNow;
        t.GoLiveSignedBy = tenant.UserName;
        t.GoLiveFrom = firstPeriodStart;
        Audit(t, "migration.golive.signed", new { firstPeriodStart, discardedParallelRuns = parallel.Select(r => r.Reference).ToList(), openItemsAccepted = open.Select(c => c.Key).ToList() });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await StatusAsync(ct);
    }

    /// <summary>Leaves migration mode without going live (a migration abandoned or started by mistake). Nothing is discarded.</summary>
    public async Task<MigrationStatus> CancelAsync(CancellationToken ct = default)
    {
        tenant.Require(Permissions.OrganisationManage);
        var t = await CurrentAsync(ct);
        if (!t.InMigration) throw new InvalidStateException("This organisation isn't in migration mode.");
        t.MigrationStartedAt = null; t.MigrationStartedBy = null;
        Audit(t, "migration.cancelled", new { });
        await db.SaveChangesAsync(ct);
        return await StatusAsync(ct);
    }

    /// <summary>Runs never approved: in migration mode these can only be parallel runs. Before go-live, every one of them.</summary>
    private IQueryable<PayrollRun> ParallelRuns(DateOnly? before) =>
        db.PayrollRuns.Where(r => r.ApprovedAt == null && r.Status != PayrollRunStatus.Reversed)
            .Where(r => before == null || db.PayrollPeriods.Any(p => p.Id == r.PayrollPeriodId && p.PeriodEnd < before));

    private async Task<List<MigrationCheck>> ChecklistAsync(Tenant t, CancellationToken ct)
    {
        var active = SubscriptionCounted();
        var employees = await db.Employees.CountAsync(e => active.Contains(e.Status), ct);
        var unpaid = await db.Employees.CountAsync(e => active.Contains(e.Status) && !db.EmployeeBankAccounts.Any(b => b.EmployeeId == e.Id && b.IsVerified), ct);
        var batches = await db.ImportBatches.Where(b => b.Status == ImportBatchStatus.Applied).Select(b => b.Kind).ToListAsync(ct);
        var reconciled = (await db.PayrollAuditLogs.Where(a => a.Action == "payroll.run.reconciled").Select(a => new { a.EntityId, a.Details }).ToListAsync(ct))
            .Where(a => FullyReconciled(a.Details)).Select(a => a.EntityId).Distinct().Count();
        var january = BusinessClock.Today().Month == 1;
        return
        [
            new("employees", "Employees loaded", employees > 0, employees == 0 ? "No active employees yet. Import them from the old system." : $"{employees} active employees.", true),
            new("bank", "Bank accounts verified", employees > 0 && unpaid == 0, unpaid == 0 ? "Every active employee has a verified account." : $"{unpaid} active employee(s) have no verified bank or M-Pesa account. Payment files skip them.", true),
            new("balances", "Opening balances loaded", batches.Contains(ImportKind.OpeningBalances), "Leave balances, loans and NSSF to date. Skip if the old system had none.", false),
            new("ytd", "This year's pay to date loaded", batches.Contains(ImportKind.YearToDate), january ? "Not needed for a January go-live." : "Needed for whole P9s when switching mid-year.", false),
            new("parallel", "Two parallel runs fully reconciled", reconciled >= 2, $"{reconciled} run(s) matched the old system's register with no differences.", true),
        ];
    }

    private static Domain.Employees.EmploymentStatus[] SubscriptionCounted() => Saas.SubscriptionService.CountedStatuses;

    private static bool FullyReconciled(string? details)
    {
        if (details is null) return false;
        try
        {
            var d = JsonDocument.Parse(details).RootElement;
            int Get(string name) => d.TryGetProperty(name, out var v) || d.TryGetProperty(char.ToUpperInvariant(name[0]) + name[1..], out v) ? v.GetInt32() : -1;
            return Get("employees") > 0 && Get("different") == 0 && Get("onlyInEcosys") == 0 && Get("onlyInLegacy") == 0;
        }
        catch (JsonException) { return false; }
    }

    private void Audit(Tenant t, string action, object details) => audit.Record(action, nameof(Tenant), t.Id, details);
}
