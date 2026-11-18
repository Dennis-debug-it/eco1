using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record Comment(string? Text);
public sealed record Reason(string Text);
public sealed record VariableInputRequest(Guid EmployeeId, Guid SalaryComponentId, decimal Amount, string? Note);
public sealed record PaymentFileRequest(PaymentFileFormat Format = PaymentFileFormat.GenericCsv, DateOnly? ValueDate = null);
public sealed record ConfirmPaymentsRequest(Guid BatchId, string? BankReference, List<Guid>? FailedPaymentIds, string? FailureReason);
public sealed record AdjustmentRequest(Guid EmployeeId, Guid OriginalRunId, Guid SalaryComponentId, AdjustmentType Type, decimal Amount, string Reason);

public sealed record RunListItem(Guid Id, string Reference, PayrollRunStatus Status, PayrollRunType RunType, string Period, string Group, int EmployeeCount, decimal TotalGross, decimal TotalNet, decimal TotalEmployerCost, int ErrorCount, int WarningCount, DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt);
public sealed record RunSummary(PayrollRun Run, string PeriodName, DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly PayDate, string GroupName, string? PreparedByName, string? ApprovedByName, IReadOnlyList<PayrollApproval> Approvals, IReadOnlyList<PaymentBatch> Batches, bool HasJournal);
public sealed record RunEmployeeRow(Guid Id, Guid EmployeeId, string EmployeeNumber, string EmployeeName, string? Department, decimal BasicPay, decimal GrossPay, decimal TaxablePay, decimal Paye, decimal Nssf, decimal Shif, decimal HousingLevy, decimal TotalOtherDeductions, decimal NetPay, decimal TotalEmployerCost, decimal? PreviousNetPay, bool HasErrors, IReadOnlyList<Ecosys.Payroll.Engine.Model.PayrollValidationIssue> Issues);

public sealed record AttendanceRow(string EmployeeNumber, decimal DaysWorked, decimal AbsentDays, decimal OvertimeHours, decimal RestDayOvertimeHours, string? Note);

public static class PayrollEndpoints
{
    private static async Task<object> SaveAttendanceAsync(Guid runId, List<AttendanceRow> rows, string source, ITenantContext ctx, PayrollRunService svc, PayrollDbContext db, AuditService audit, CancellationToken ct)
    {
        ctx.Require(Permissions.PayrollPrepare);
        var run = await svc.GetAsync(runId, includeEmployees: false, ct);
        run.EnsureEditable();
        var numbers = rows.Select(r => r.EmployeeNumber.Trim()).ToList();
        var emps = await db.Employees.Where(e => e.LegalEntityId == run.LegalEntityId && e.PayrollGroupId == run.PayrollGroupId && numbers.Contains(e.EmployeeNumber)).ToDictionaryAsync(e => e.EmployeeNumber, ct);
        var unknown = numbers.Where(n => !emps.ContainsKey(n)).Distinct().ToList();
        if (unknown.Count > 0) throw new DomainException($"Unknown employee number(s) for this payroll group: {string.Join(", ", unknown.Take(10))}.", "VALIDATION");
        var dupes = numbers.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0) throw new DomainException($"Duplicate rows for: {string.Join(", ", dupes)}.", "VALIDATION");
        foreach (var r in rows)
            if (r.AbsentDays < 0 || r.OvertimeHours < 0 || r.RestDayOvertimeHours < 0 || r.DaysWorked < 0 || r.OvertimeHours + r.RestDayOvertimeHours > 300 || r.AbsentDays > 31)
                throw new DomainException($"Implausible attendance values for {r.EmployeeNumber}.", "VALIDATION");

        var existing = await db.AttendanceRecords.Where(a => a.PayrollRunId == runId).ToDictionaryAsync(a => a.EmployeeId, ct);
        foreach (var r in rows)
        {
            var emp = emps[r.EmployeeNumber.Trim()];
            if (!existing.TryGetValue(emp.Id, out var rec))
            {
                rec = new AttendanceRecord { LegalEntityId = run.LegalEntityId, PayrollRunId = runId, EmployeeId = emp.Id };
                db.AttendanceRecords.Add(rec);
            }
            rec.DaysWorked = r.DaysWorked; rec.AbsentDays = r.AbsentDays; rec.OvertimeHours = r.OvertimeHours; rec.RestDayOvertimeHours = r.RestDayOvertimeHours; rec.Note = r.Note; rec.Source = source;
        }
        if (run.Status == PayrollRunStatus.Calculated) run.Transition(PayrollRunStatus.Prepared); // results are stale
        audit.Record("payroll.run.attendance", nameof(PayrollRun), runId, new { rows = rows.Count, source, overtimeHours = rows.Sum(r => r.OvertimeHours + r.RestDayOvertimeHours), absentDays = rows.Sum(r => r.AbsentDays) }, run.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return new { saved = rows.Count };
    }

    public static void MapPayrollRuns(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/runs").RequireAuthorization();

        g.MapGet("", async (Guid? legalEntityId, PayrollRunStatus? status, int? offset, int? limit, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.SalaryRead);
            var (off, lim) = Paging.Normalise(offset, limit);
            var q = db.PayrollRuns.AsQueryable();
            if (legalEntityId is { } le) q = q.Where(r => r.LegalEntityId == le);
            if (ctx.LegalEntityIds.Count > 0) q = q.Where(r => ctx.LegalEntityIds.Contains(r.LegalEntityId));
            if (status is { } s) q = q.Where(r => r.Status == s);
            var total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(r => r.CreatedAt).Skip(off).Take(lim)
                .Join(db.PayrollPeriods, r => r.PayrollPeriodId, p => p.Id, (r, p) => new { r, p })
                .Join(db.PayrollGroups, x => x.r.PayrollGroupId, gr => gr.Id, (x, gr) => new RunListItem(x.r.Id, x.r.Reference, x.r.Status, x.r.RunType, x.p.Name, gr.Name, x.r.EmployeeCount, x.r.TotalGross, x.r.TotalNet, x.r.TotalEmployerCost, x.r.ErrorCount, x.r.WarningCount, x.r.CreatedAt, x.r.ApprovedAt))
                .ToListAsync(ct);
            return Results.Ok(new Page<RunListItem>(rows, total, off, lim));
        });

        g.MapPost("", async (CreateRunRequest req, PayrollRunService svc, CancellationToken ct) =>
        {
            var run = await svc.CreateAsync(req, ct);
            return Results.Created($"/api/payroll/runs/{run.Id}", run);
        });

        g.MapGet("/{id:guid}", async (Guid id, PayrollRunService svc, PayrollDbContext db, CancellationToken ct) =>
        {
            var run = await svc.GetAsync(id, includeEmployees: false, ct);
            var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PayrollPeriodId, ct);
            var group = await db.PayrollGroups.FirstAsync(p => p.Id == run.PayrollGroupId, ct);
            var userIds = new[] { run.PreparedBy, run.ApprovedBy }.Where(u => u != null).Select(u => u!.Value).ToList();
            var users = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
            var approvals = await db.PayrollApprovals.Where(a => a.PayrollRunId == id).OrderBy(a => a.CreatedAt).ToListAsync(ct);
            var batches = await db.PaymentBatches.Where(b => b.PayrollRunId == id).OrderBy(b => b.CreatedAt).ToListAsync(ct);
            var hasJournal = await db.PayrollJournals.AnyAsync(j => j.PayrollRunId == id, ct);
            return Results.Ok(new RunSummary(run, period.Name, period.PeriodStart, period.PeriodEnd, period.PayDate, group.Name,
                run.PreparedBy is { } pb ? users.GetValueOrDefault(pb) : null, run.ApprovedBy is { } ab ? users.GetValueOrDefault(ab) : null, approvals, batches, hasJournal));
        });

        g.MapGet("/{id:guid}/employees", async (Guid id, bool? issuesOnly, ITenantContext ctx, PayrollRunService svc, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.SalaryRead);
            var run = await svc.GetAsync(id, includeEmployees: true, ct);
            var depts = await db.Departments.Where(d => d.LegalEntityId == run.LegalEntityId).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
            var rows = run.Employees.Where(e => issuesOnly != true || e.Issues.Count > 0)
                .Select(e => new RunEmployeeRow(e.Id, e.EmployeeId, e.EmployeeNumber, e.EmployeeName, e.DepartmentId is { } d ? depts.GetValueOrDefault(d) : null,
                    e.BasicPay, e.GrossPay, e.TaxablePay, e.Paye, e.Nssf, e.Shif, e.HousingLevy, e.TotalOtherDeductions, e.NetPay, e.TotalEmployerCost, e.PreviousNetPay, e.HasErrors, e.Issues));
            return Results.Ok(rows);
        });

        g.MapGet("/{id:guid}/employees/{employeeId:guid}", async (Guid id, Guid employeeId, PayrollRunService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetEmployeeResultAsync(id, employeeId, ct)));

        g.MapGet("/{id:guid}/variable-inputs", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.SalaryRead);
            return Results.Ok(await db.PayrollVariableInputs.Where(v => v.PayrollRunId == id).OrderBy(v => v.CreatedAt).ToListAsync(ct));
        });
        g.MapPost("/{id:guid}/variable-inputs", async (Guid id, List<VariableInputRequest> items, ITenantContext ctx, PayrollRunService svc, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.PayrollPrepare);
            var run = await svc.GetAsync(id, includeEmployees: false, ct);
            run.EnsureEditable();
            var components = await db.SalaryComponents.Where(c => c.LegalEntityId == run.LegalEntityId).ToDictionaryAsync(c => c.Id, ct);
            foreach (var i in items)
            {
                if (!components.TryGetValue(i.SalaryComponentId, out var c)) throw new NotFoundException(nameof(SalaryComponent), i.SalaryComponentId);
                if (i.Amount <= 0) throw new DomainException($"Amount for {c.Code} must be positive.", "VALIDATION");
                db.PayrollVariableInputs.Add(new PayrollVariableInput { LegalEntityId = run.LegalEntityId, PayrollRunId = id, EmployeeId = i.EmployeeId, SalaryComponentId = i.SalaryComponentId, Amount = i.Amount, Note = i.Note });
            }
            if (run.Status == PayrollRunStatus.Calculated) run.Transition(PayrollRunStatus.Prepared); // results are stale
            audit.Record("payroll.run.variable_inputs", nameof(PayrollRun), id, new { count = items.Count, total = items.Sum(i => i.Amount) }, run.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { added = items.Count });
        });
        g.MapDelete("/{id:guid}/variable-inputs/{inputId:guid}", async (Guid id, Guid inputId, ITenantContext ctx, PayrollRunService svc, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.PayrollPrepare);
            var run = await svc.GetAsync(id, includeEmployees: false, ct);
            run.EnsureEditable();
            var v = await db.PayrollVariableInputs.FirstOrDefaultAsync(x => x.Id == inputId && x.PayrollRunId == id, ct) ?? throw new NotFoundException(nameof(PayrollVariableInput), inputId);
            db.PayrollVariableInputs.Remove(v);
            if (run.Status == PayrollRunStatus.Calculated) run.Transition(PayrollRunStatus.Prepared);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- Attendance (time & attendance / biometric import, or manual capture) ----
        g.MapGet("/{id:guid}/attendance", async (Guid id, ITenantContext ctx, PayrollRunService svc, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.SalaryRead);
            var run = await svc.GetAsync(id, includeEmployees: false, ct);
            var rows = await db.AttendanceRecords.Where(a => a.PayrollRunId == id)
                .Join(db.Employees, a => a.EmployeeId, e => e.Id, (a, e) => new { a.Id, a.EmployeeId, e.EmployeeNumber, employeeName = e.FirstName + " " + e.LastName, a.DaysWorked, a.AbsentDays, a.OvertimeHours, a.RestDayOvertimeHours, a.Source, a.Note })
                .OrderBy(x => x.EmployeeNumber).ToListAsync(ct);
            return Results.Ok(rows);
        });
        g.MapPut("/{id:guid}/attendance", async (Guid id, List<AttendanceRow> rows, string? source, ITenantContext ctx, PayrollRunService svc, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
            Results.Ok(await SaveAttendanceAsync(id, rows, source ?? "manual", ctx, svc, db, audit, ct)));
        g.MapPost("/{id:guid}/attendance/import", async (Guid id, HttpRequest http, ITenantContext ctx, PayrollRunService svc, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            // CSV: EmployeeNumber,DaysWorked,AbsentDays,OvertimeHours,RestDayOvertimeHours[,Note] — header row required.
            using var reader = new StreamReader(http.Body);
            var text = await reader.ReadToEndAsync(ct);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2) throw new DomainException("The file has no data rows.", "VALIDATION");
            var header = lines[0].Split(',').Select(h => h.Trim().Trim('"').ToLowerInvariant().Replace(" ", "")).ToList();
            int Col(string name) => header.IndexOf(name);
            if (Col("employeenumber") < 0) throw new DomainException("Missing EmployeeNumber column.", "VALIDATION");
            var parsed = new List<AttendanceRow>();
            for (var i = 1; i < lines.Length; i++)
            {
                var c = lines[i].Split(',').Select(x => x.Trim().Trim('"')).ToArray();
                decimal D(string name) => Col(name) is var k && k >= 0 && k < c.Length && decimal.TryParse(c[k], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v
                    : Col(name) >= 0 && Col(name) < c.Length && c[Col(name)].Length > 0 ? throw new DomainException($"Line {i + 1}: '{c[Col(name)]}' is not a number ({name}).", "VALIDATION") : 0m;
                parsed.Add(new AttendanceRow(c[Col("employeenumber")], D("daysworked"), D("absentdays"), D("overtimehours"), D("restdayovertimehours"), Col("note") is var n && n >= 0 && n < c.Length ? c[n] : null));
            }
            return Results.Ok(await SaveAttendanceAsync(id, parsed, "import", ctx, svc, db, audit, ct));
        }).Accepts<string>("text/csv");

        g.MapPost("/{id:guid}/prepare", async (Guid id, PayrollRunService svc, CancellationToken ct) => Results.Ok(await svc.PrepareAsync(id, ct)));
        g.MapPost("/{id:guid}/calculate", async (Guid id, PayrollRunService svc, CancellationToken ct) => Results.Ok(await svc.CalculateAsync(id, ct)));
        g.MapPost("/{id:guid}/validate", async (Guid id, ITenantContext ctx, PayrollRunService svc, CancellationToken ct) =>
        {
            ctx.Require(Permissions.PayrollReview);
            var run = await svc.GetAsync(id, includeEmployees: true, ct);
            var issues = run.Employees.SelectMany(e => e.Issues.Select(i => new { e.EmployeeNumber, e.EmployeeName, i.Severity, i.Code, i.Message })).OrderByDescending(i => i.Severity).ThenBy(i => i.EmployeeNumber).ToList();
            return Results.Ok(new { run.Status, run.ErrorCount, run.WarningCount, canSubmit = run.Status == PayrollRunStatus.Calculated && run.ErrorCount == 0, issues });
        });
        g.MapPost("/{id:guid}/submit", async (Guid id, Comment? c, PayrollRunService svc, CancellationToken ct) => Results.Ok(await svc.SubmitAsync(id, c?.Text, ct)));
        g.MapPost("/{id:guid}/approve", async (Guid id, Comment? c, PayrollRunService svc, CancellationToken ct) => Results.Ok(await svc.ApproveAsync(id, c?.Text, ct)));
        g.MapPost("/{id:guid}/reject", async (Guid id, Reason r, PayrollRunService svc, CancellationToken ct) => Results.Ok(await svc.RejectAsync(id, r.Text, ct)));
        g.MapPost("/{id:guid}/finalise", async (Guid id, PayrollRunService svc, CancellationToken ct) => Results.Ok(await svc.FinaliseAsync(id, ct)));
        g.MapPost("/{id:guid}/reverse", async (Guid id, Reason r, PayrollRunService svc, CancellationToken ct) => Results.Ok(await svc.ReverseAsync(id, r.Text, ct)));

        g.MapPost("/{id:guid}/payment-file", async (Guid id, PaymentFileRequest? req, PayrollRunService svc, CancellationToken ct) =>
        {
            var (batch, content) = await svc.GeneratePaymentFileAsync(id, req?.Format ?? PaymentFileFormat.GenericCsv, req?.ValueDate, ct);
            return Results.File(content, "text/csv", batch.FileName);
        });
        g.MapGet("/{id:guid}/payments", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.PaymentsManage);
            var payments = await db.PayrollPayments.Where(p => p.PayrollRunId == id).OrderBy(p => p.Reference).ToListAsync(ct);
            foreach (var p in payments) p.AccountNumber = FieldEncryptor.Mask(p.AccountNumber);
            db.ChangeTracker.Clear();
            return Results.Ok(payments);
        });
        g.MapPost("/{id:guid}/payments/confirm", async (Guid id, ConfirmPaymentsRequest req, PayrollRunService svc, CancellationToken ct) =>
            Results.Ok(await svc.ConfirmPaymentsAsync(id, req.BatchId, req.BankReference, req.FailedPaymentIds, req.FailureReason, ct)));

        g.MapGet("/{id:guid}/journal", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.JournalsRead);
            var journals = await db.PayrollJournals.Include(j => j.Lines.OrderBy(l => l.Sequence)).Where(j => j.PayrollRunId == id).OrderBy(j => j.CreatedAt).ToListAsync(ct);
            var ccs = await db.CostCentres.ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            return Results.Ok(journals.Select(j => new { j.Id, j.Reference, j.PostingDate, j.Status, j.TotalDebit, j.TotalCredit, j.ReversalOfJournalId, j.CurrencyCode,
                lines = j.Lines.Select(l => new { l.Sequence, l.AccountCode, l.Description, l.Debit, l.Credit, costCentre = l.CostCentreId is { } c ? ccs.GetValueOrDefault(c) : null }) }));
        });

        // Adjustments (controlled corrections to approved runs)
        var adj = api.MapGroup("/adjustments").RequireAuthorization();
        adj.MapGet("", async (Guid? employeeId, AdjustmentStatus? status, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.SalaryRead);
            var q = db.PayrollAdjustments.AsQueryable();
            if (employeeId is { } e) q = q.Where(a => a.EmployeeId == e);
            if (status is { } s) q = q.Where(a => a.Status == s);
            return Results.Ok(await q.OrderByDescending(a => a.CreatedAt).ToListAsync(ct));
        });
        adj.MapPost("", async (AdjustmentRequest req, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.PayrollPrepare);
            var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == req.OriginalRunId, ct) ?? throw new NotFoundException(nameof(PayrollRun), req.OriginalRunId);
            if (!run.IsLocked) throw new InvalidStateException("Adjustments apply to approved or finalised runs; edit the run directly instead.");
            if (string.IsNullOrWhiteSpace(req.Reason)) throw new DomainException("Reason is required.", "VALIDATION");
            var a = new PayrollAdjustment { LegalEntityId = run.LegalEntityId, EmployeeId = req.EmployeeId, OriginalRunId = req.OriginalRunId, SalaryComponentId = req.SalaryComponentId, Type = req.Type, Amount = req.Amount, Reason = req.Reason, RequestedBy = ctx.RequireUser() };
            db.PayrollAdjustments.Add(a);
            audit.Record("payroll.adjustment.requested", nameof(PayrollAdjustment), a.Id, new { req.EmployeeId, req.Type, req.Amount, req.Reason }, run.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/adjustments/{a.Id}", a);
        });
        adj.MapPost("/{id:guid}/approve", async (Guid id, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.PayrollApprove);
            var a = await db.PayrollAdjustments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(PayrollAdjustment), id);
            if (a.RequestedBy == ctx.UserId && !ctx.IsPlatformAdministrator) throw new ForbiddenException("The requester cannot approve their own adjustment.");
            a.ApprovedBy = ctx.RequireUser();
            audit.Record("payroll.adjustment.approved", nameof(PayrollAdjustment), id, null, a.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(a);
        });
    }
}
