using System.Text;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Domain.Migration;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ecosys.Payroll.Infrastructure.Services.Migration;

/// <summary>What a file would do, before anything is saved. <see cref="CanImport"/> only when there is no problem at all.</summary>
public sealed record ImportPreview(ImportKind Kind, string FileName, int Rows, int ReadyRows, bool CanImport, IReadOnlyList<ImportProblem> Problems,
    IReadOnlyDictionary<string, int> Summary, IReadOnlyList<string> Notes, IReadOnlyList<string> Columns);

public sealed record ImportOutcome(ImportPreview Preview, ImportBatch? Batch);

/// <summary>
/// Loads data from a customer's previous payroll system. Every file is checked in full first (the dry run); a file is
/// imported only when every row is clean, all in one transaction, as a batch that can be undone (newest first) until
/// payroll has used it. Values are validated with the same rules as the screens; bank accounts arrive unverified.
/// </summary>
public sealed class MigrationImportService(PayrollDbContext db, ITenantContext tenant, AuditService audit,
    EmployeeImporter employees, OpeningBalanceImporter balances, YearToDateImporter yearToDate)
{
    public static IReadOnlyList<ImportColumn> ColumnsFor(ImportKind kind) => kind switch
    {
        ImportKind.Employees => EmployeeImporter.Columns,
        ImportKind.OpeningBalances => OpeningBalanceImporter.Columns,
        _ => YearToDateImporter.Columns,
    };

    private void Authorise(ImportKind kind, Guid legalEntityId)
    {
        tenant.RequireLegalEntity(legalEntityId);
        switch (kind)
        {
            case ImportKind.Employees: tenant.Require(Permissions.EmployeesWrite); break;
            case ImportKind.OpeningBalances: tenant.Require(Permissions.EmployeesWrite); break;
            default: tenant.Require(Permissions.SalaryWrite); break;
        }
    }

    public async Task<ImportPreview> PreviewAsync(ImportKind kind, Guid legalEntityId, string fileName, byte[] content, CancellationToken ct = default) =>
        (await RunAsync(kind, legalEntityId, fileName, content, apply: false, ct)).Preview;

    public Task<ImportOutcome> ApplyAsync(ImportKind kind, Guid legalEntityId, string fileName, byte[] content, CancellationToken ct = default) =>
        RunAsync(kind, legalEntityId, fileName, content, apply: true, ct);

    private async Task<ImportOutcome> RunAsync(ImportKind kind, Guid legalEntityId, string fileName, byte[] content, bool apply, CancellationToken ct)
    {
        Authorise(kind, legalEntityId);
        if (!await db.LegalEntities.AnyAsync(l => l.Id == legalEntityId, ct)) throw new NotFoundException("LegalEntity", legalEntityId);
        var sheet = TabularReader.Read(content, fileName);
        var problems = new List<ImportProblem>();
        await using var tx = apply ? await db.Database.BeginTransactionAsync(ct) : null;

        Dictionary<string, int> summary; List<string> notes; Action<ImportBatch> write;
        switch (kind)
        {
            case ImportKind.Employees:
            {
                var plan = await employees.PlanAsync(legalEntityId, sheet, problems, ct);
                if (plan.Rows.Any(r => r.Salary is not null) && !tenant.Has(Permissions.SalaryWrite))
                    problems.Add(new ImportProblem(0, "BasicSalary", "The file sets salaries, which needs permission to change pay. Remove the pay columns or ask a payroll administrator."));
                (summary, notes, write) = (plan.Summary, plan.Notes, b => employees.Apply(plan, b));
                break;
            }
            case ImportKind.OpeningBalances:
            {
                var plan = await balances.PlanAsync(legalEntityId, sheet, problems, ct);
                if (plan.Loans.Count > 0 && !tenant.Has(Permissions.SalaryWrite))
                    problems.Add(new ImportProblem(0, "LoanBalance", "The file sets loans, which needs permission to change pay. Remove the loan columns or ask a payroll administrator."));
                (summary, notes, write) = (plan.Summary, plan.Notes, b => balances.Apply(plan, b));
                break;
            }
            default:
            {
                var plan = await yearToDate.PlanAsync(legalEntityId, sheet, problems, ct);
                (summary, notes, write) = (plan.Summary, plan.Notes, b => yearToDate.Apply(plan, b));
                break;
            }
        }

        var badRows = problems.Where(p => p.Row > 1).Select(p => p.Row).Distinct().Count();
        var ordered = problems.OrderBy(p => p.Row).ThenBy(p => p.Column).ToList();
        var ready = problems.Any(p => p.Row is 0 or 1) ? 0 : sheet.Rows.Count - badRows;
        var columns = sheet.Headers.Where(h => h.Length > 0).ToList();
        var preview = new ImportPreview(kind, fileName, sheet.Rows.Count, ready, problems.Count == 0 && ready > 0, ordered, summary, notes, columns);
        if (!apply || !preview.CanImport) return new ImportOutcome(preview, null);

        var batch = new ImportBatch
        {
            TenantId = tenant.RequireTenant(), LegalEntityId = legalEntityId, Kind = kind, FileName = Path.GetFileName(fileName), RowCount = sheet.Rows.Count,
            AppliedByName = tenant.UserName, Summary = new Dictionary<string, int>(summary),
        };
        write(batch);
        db.ImportBatches.Add(batch);
        // Counts only: the audit trail never carries salaries, PINs or account numbers.
        audit.Record("migration.import.applied", nameof(ImportBatch), batch.Id, new { kind = kind.ToString(), batch.FileName, batch.RowCount, summary }, legalEntityId);
        await db.SaveChangesAsync(ct);
        await tx!.CommitAsync(ct);
        return new ImportOutcome(preview, batch);
    }

    public async Task<IReadOnlyList<ImportBatch>> ListAsync(Guid? legalEntityId, CancellationToken ct = default)
    {
        tenant.Require(Permissions.EmployeesRead);
        var q = db.ImportBatches.AsQueryable();
        if (legalEntityId is { } le) { tenant.RequireLegalEntity(le); q = q.Where(b => b.LegalEntityId == le); }
        if (tenant.LegalEntityIds.Count > 0) q = q.Where(b => tenant.LegalEntityIds.Contains(b.LegalEntityId));
        return await q.OrderByDescending(b => b.CreatedAt).Take(200).ToListAsync(ct);
    }

    public async Task<ImportBatch> UndoAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct) ?? throw new NotFoundException(nameof(ImportBatch), batchId);
        Authorise(batch.Kind, batch.LegalEntityId);
        if (batch.Summary.GetValueOrDefault("salaries") + batch.Summary.GetValueOrDefault("loans") > 0 && !tenant.Has(Permissions.SalaryWrite))
            throw new ForbiddenException("This import set pay or loans; undoing it needs permission to change pay.");
        if (batch.Status == ImportBatchStatus.Undone) throw new InvalidStateException("This import was already undone.");
        var later = await db.ImportBatches.Where(b => b.LegalEntityId == batch.LegalEntityId && b.Status == ImportBatchStatus.Applied && b.CreatedAt > batch.CreatedAt).OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(ct);
        if (later is not null) throw new InvalidStateException($"Undo the later import first ({later.FileName}, {BusinessClock.ToLocal(later.CreatedAt):dd MMM yyyy HH:mm}). Imports are undone newest first.");
        if (batch.Kind != ImportKind.YearToDate && await db.PayrollRuns.AnyAsync(r => r.LegalEntityId == batch.LegalEntityId && r.ApprovedAt != null && r.ApprovedAt > batch.CreatedAt, ct))
            throw new InvalidStateException("A payroll run has been approved since this import, so payroll may have used it. Correct individual records instead.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var r = batch.Records;
        switch (batch.Kind)
        {
            case ImportKind.Employees:
                await RemoveEmployeesAsync(r.Employees, ct);
                break;
            case ImportKind.OpeningBalances:
                var touched = await db.EmployeeLeaveBalances.Where(b => r.LeaveBalances.Contains(b.Id) || r.LeaveBalanceUpdates.Select(u => u.Id).Contains(b.Id)).Select(b => b.EmployeeId).Distinct().ToListAsync(ct);
                if (await db.LeaveRequests.AnyAsync(q => touched.Contains(q.EmployeeId) && q.Status == LeaveRequestStatus.Approved && q.UpdatedAt > batch.CreatedAt, ct))
                    throw new InvalidStateException("Leave has been approved since this import for some of these employees, so their balances have moved on. Correct the balances by hand instead.");
                await db.StaffLoans.Where(l => r.Loans.Contains(l.Id)).ExecuteDeleteAsync(ct);
                await db.EmployeeLeaveBalances.Where(b => r.LeaveBalances.Contains(b.Id)).ExecuteDeleteAsync(ct);
                foreach (var u in r.LeaveBalanceUpdates)
                    await db.EmployeeLeaveBalances.Where(b => b.Id == u.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Entitlement, u.Entitlement).SetProperty(b => b.Accrued, u.Accrued).SetProperty(b => b.Taken, u.Taken).SetProperty(b => b.CarriedOver, u.CarriedOver), ct);
                foreach (var n in r.NssfUpdates)
                {
                    if (n.StatutoryId is { } sid)
                        await db.EmployeeStatutoryDetails.Where(s => s.Id == sid).ExecuteUpdateAsync(s => s.SetProperty(x => x.NssfOpeningBalance, n.Previous), ct);
                    else
                    {
                        // The import created the record only to hold the NSSF figure; keep it if anything else has been entered since.
                        var created = await db.EmployeeStatutoryDetails.Where(s => s.EmployeeId == n.EmployeeId).ToListAsync(ct);
                        foreach (var s in created)
                            if (s.KraPin is null && s.NssfNumber is null && s.ShifNumber is null && !s.HasDisabilityExemption) db.EmployeeStatutoryDetails.Remove(s);
                            else s.NssfOpeningBalance = 0m;
                    }
                }
                break;
            default:
                await db.PayrollHistoryMonths.Where(h => r.HistoryMonths.Contains(h.Id)).ExecuteDeleteAsync(ct);
                break;
        }
        batch.Status = ImportBatchStatus.Undone;
        batch.UndoneAt = DateTimeOffset.UtcNow;
        batch.UndoneByName = tenant.UserName;
        audit.Record("migration.import.undone", nameof(ImportBatch), batch.Id, new { kind = batch.Kind.ToString(), batch.FileName, batch.Summary }, batch.LegalEntityId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch;
    }

    /// <summary>The tables an import fills for an employee; removed on undo, children first.</summary>
    private static readonly HashSet<string> OwnedByImport = ["EmployeeSalaryAssignment", "EmployeeSalaryAssignmentLine", "EmployeeBankAccount", "EmployeeStatutoryDetails", "EmploymentRecord", "Employee"];

    private async Task RemoveEmployeesAsync(List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        // Anything else that points at these employees (a payroll run, a loan, leave, a user account, a document, a
        // report to them…) means they are in use: undoing would orphan it, so refuse and say where.
        var inUse = new List<string>();
        foreach (var et in db.Model.GetEntityTypes().Where(t => !t.IsOwned() && t.GetTableName() is not null && !OwnedByImport.Contains(t.ClrType.Name)))
        {
            foreach (var prop in et.GetProperties().Where(p => p.Name is "EmployeeId" or "ReportingManagerId" && (p.ClrType == typeof(Guid) || p.ClrType == typeof(Guid?))))
            {
                var sql = $"SELECT count(*)::int AS \"Value\" FROM \"{et.GetTableName()}\" WHERE \"{prop.GetColumnName()}\" = ANY(@ids)";
                if (await db.Database.SqlQueryRaw<int>(sql, new NpgsqlParameter("ids", ids.ToArray())).SingleAsync(ct) > 0) inUse.Add(Words(et.ClrType.Name));
            }
        }
        var outsideReports = await db.Employees.IgnoreQueryFilters().CountAsync(e => e.ReportingManagerId != null && ids.Contains(e.ReportingManagerId.Value) && !ids.Contains(e.Id), ct);
        if (outsideReports > 0) inUse.Add("employees who report to them");
        if (inUse.Count > 0)
            throw new InvalidStateException($"Some of these employees are already in use ({string.Join(", ", inUse.Distinct())}). Undo is only possible before anything else is recorded against them.");

        var assignments = db.EmployeeSalaryAssignments.IgnoreQueryFilters().Where(a => ids.Contains(a.EmployeeId)).Select(a => a.Id);
        await db.EmployeeSalaryAssignmentLines.IgnoreQueryFilters().Where(l => assignments.Contains(l.EmployeeSalaryAssignmentId)).ExecuteDeleteAsync(ct);
        await db.EmployeeSalaryAssignments.IgnoreQueryFilters().Where(a => ids.Contains(a.EmployeeId)).ExecuteDeleteAsync(ct);
        await db.EmployeeBankAccounts.IgnoreQueryFilters().Where(a => ids.Contains(a.EmployeeId)).ExecuteDeleteAsync(ct);
        await db.EmployeeStatutoryDetails.IgnoreQueryFilters().Where(a => ids.Contains(a.EmployeeId)).ExecuteDeleteAsync(ct);
        await db.EmploymentRecords.IgnoreQueryFilters().Where(a => ids.Contains(a.EmployeeId)).ExecuteDeleteAsync(ct);
        // Managers inside the batch: clear the links first so the rows can go in any order.
        await db.Employees.IgnoreQueryFilters().Where(e => ids.Contains(e.Id)).ExecuteUpdateAsync(s => s.SetProperty(e => e.ReportingManagerId, (Guid?)null), ct);
        await db.Employees.IgnoreQueryFilters().Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct);
    }

    private static string Words(string typeName)
    {
        var sb = new StringBuilder();
        foreach (var ch in typeName) { if (char.IsUpper(ch) && sb.Length > 0) sb.Append(' '); sb.Append(char.ToLowerInvariant(ch)); }
        return sb.ToString() + "s";
    }

    /// <summary>A CSV with the import's headers and one example row. For employees, the company's fixed pay components are columns too.</summary>
    public async Task<string> TemplateAsync(ImportKind kind, Guid? legalEntityId, CancellationToken ct = default)
    {
        var cols = ColumnsFor(kind).Select(c => (c.Name, c.Example ?? "")).ToList();
        if (kind == ImportKind.Employees && legalEntityId is { } le)
        {
            tenant.RequireLegalEntity(le);
            var extra = await db.SalaryComponents.Where(c => c.LegalEntityId == le && c.IsActive && !c.IsBasic && c.Method == Domain.Compensation.CalculationMethod.FixedAmount)
                .OrderBy(c => c.SortOrder).Select(c => c.Code).ToListAsync(ct);
            cols.AddRange(extra.Select(c => (c, "")));
        }
        static string Q(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        return string.Join(",", cols.Select(c => Q(c.Item1))) + "\r\n" + string.Join(",", cols.Select(c => Q(c.Item2))) + "\r\n";
    }
}
