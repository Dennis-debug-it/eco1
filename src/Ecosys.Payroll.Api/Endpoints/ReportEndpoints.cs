using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Ecosys.Payroll.Infrastructure.Services.Migration;
using Ecosys.Payroll.Infrastructure.Services.Returns;
using Ecosys.Payroll.Infrastructure.Services.Accounting;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record RegisterRow(string EmployeeNumber, string EmployeeName, string? Department, decimal BasicPay, decimal GrossPay, decimal TaxablePay, decimal Paye, decimal Nssf, decimal Shif, decimal HousingLevy, decimal TotalOtherDeductions, decimal TotalEmployeeDeductions, decimal NetPay, decimal TotalEmployerContributions, decimal TotalEmployerCost);

/// <summary>Reports. Every tabular report accepts <c>format=csv|xlsx|pdf</c>; without it the JSON is returned.</summary>
public static class ReportEndpoints
{
    public static void MapReports(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async (Guid? legalEntityId, ReportingService svc, CancellationToken ct) => Results.Ok(await svc.DashboardAsync(legalEntityId, ct))).RequireAuthorization();

        var r = api.MapGroup("/reports").RequireAuthorization();

        r.MapGet("/statutory", async (Guid runId, string ruleCode, string? format, ReportingService svc, AuditService audit, PayrollDbContext db, MigrationGuard migration, CancellationToken ct) =>
        {
            var report = await svc.StatutoryAsync(runId, ruleCode, ct);
            if (TabularExport.Parse(format) is not { } f) return Results.Ok(report); // on screen: compare it with the old system's return
            await migration.EnsureLiveAsync("export a statutory return for filing", ct);
            var file = TabularExport.Build(f, report.Title, $"{report.RunReference} · {report.Period}", $"{report.RunReference}-{ruleCode}", report.Rows,
            [
                new("Employee No", x => x.EmployeeNumber), new("Employee Name", x => x.EmployeeName), new("PIN", x => x.Pin), new("Member No", x => x.MemberNumber),
                new("Gross Pay", x => x.Gross, true), new("Contribution Base", x => x.Base, true), new("Employee", x => x.Employee, true), new("Employer", x => x.Employer, true), new("Total", x => x.Total, true),
            ], ["Totals", null, null, null, report.Rows.Sum(x => x.Gross), null, report.TotalEmployee, report.TotalEmployer, report.TotalEmployee + report.TotalEmployer]);
            return await Export(file, audit, db, "report.statutory.exported", runId, ct);
        });

        // Journals as import files for QuickBooks or Sage: one run's journals, or every journal a company posted between two dates.
        var acc = api.MapGroup("/accounting/journals").RequireAuthorization();
        acc.MapGet("/check", async (string? format, Guid? runId, Guid? legalEntityId, DateOnly? from, DateOnly? to, JournalExportService svc, CancellationToken ct) =>
            Results.Ok(await svc.CheckAsync(JournalExportService.Parse(format), runId, legalEntityId, from, to, ct)));
        acc.MapGet("/export", async (string? format, Guid? runId, Guid? legalEntityId, DateOnly? from, DateOnly? to, JournalExportService svc, CancellationToken ct) =>
        {
            try
            {
                var file = await svc.ExportAsync(JournalExportService.Parse(format), runId, legalEntityId, from, to, ct);
                return Results.File(file.Content, file.ContentType, file.FileName);
            }
            catch (JournalExportNotReadyException ex)
            {
                return Results.Json(new { title = "The journals have problems", status = 422, code = ex.Code, detail = "Fix the items listed, then export again.", check = ex.Check }, statusCode: 422);
            }
        });

        // KRA P10 (monthly PAYE return) for one company and month, across every approved run that month.
        r.MapGet("/p10/check", async (Guid legalEntityId, int year, int month, PayeReturnService svc, CancellationToken ct) => Results.Ok(await svc.CheckAsync(legalEntityId, year, month, ct)));
        r.MapGet("/p10", async (Guid legalEntityId, int year, int month, PayeReturnService svc, CancellationToken ct) =>
        {
            try
            {
                var file = await svc.ExportAsync(legalEntityId, year, month, ct);
                return Results.File(file.Content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.FileName);
            }
            catch (PayeReturnNotReadyException ex)
            {
                return Results.Json(new { title = "The return has problems", status = 422, code = ex.Code, detail = "Fix the employees listed, then export again.", check = ex.Check }, statusCode: 422);
            }
        });

        // NSSF upload files, laid out exactly as NSSF specifies. The check lists anything to fix; the file is only made when there is nothing.
        r.MapGet("/nssf-file/check", async (Guid runId, NssfReturnService svc, CancellationToken ct) => Results.Ok(await svc.CheckAsync(runId, ct)));
        r.MapGet("/nssf-file", async (Guid runId, string? layout, NssfReturnService svc, CancellationToken ct) =>
        {
            var l = (layout ?? "portal").ToLowerInvariant() switch
            {
                "portal" => NssfFileLayout.PortalTemplate,
                "returns" => NssfFileLayout.Returns2014,
                _ => throw new DomainException("Layout is 'portal' (the employer portal's payroll template) or 'returns' (the returns file).", "VALIDATION"),
            };
            try
            {
                var file = await svc.ExportAsync(runId, l, ct);
                return Results.File(file.Content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.FileName);
            }
            catch (ReturnNotReadyException ex)
            {
                return Results.Json(new { title = "The return has problems", status = 422, code = ex.Code, detail = "Fix the members listed, then export again.", check = ex.Check }, statusCode: 422);
            }
        });

        r.MapGet("/payroll-register", async (Guid runId, string? format, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.ReportsRead); ctx.Require(Permissions.SalaryRead);
            var run = await db.PayrollRuns.Include(x => x.Employees.OrderBy(e => e.EmployeeNumber)).FirstOrDefaultAsync(x => x.Id == runId, ct) ?? throw new NotFoundException(nameof(PayrollRun), runId);
            ctx.RequireLegalEntity(run.LegalEntityId);
            var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PayrollPeriodId, ct);
            var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name, ct);
            var rows = run.Employees.Select(e => new RegisterRow(e.EmployeeNumber, e.EmployeeName, e.DepartmentId is { } d ? depts.GetValueOrDefault(d) : null, e.BasicPay, e.GrossPay, e.TaxablePay, e.Paye, e.Nssf, e.Shif, e.HousingLevy, e.TotalOtherDeductions, e.TotalEmployeeDeductions, e.NetPay, e.TotalEmployerContributions, e.TotalEmployerCost)).ToList();
            var totals = new { run.TotalGross, run.TotalPaye, run.TotalNssfEmployee, run.TotalShif, run.TotalHousingLevyEmployee, run.TotalOtherDeductions, run.TotalNet, run.TotalEmployerContributions, run.TotalEmployerCost };
            if (TabularExport.Parse(format) is not { } f) return Results.Ok(new { run.Reference, run.Status, rows, totals });
            var file = TabularExport.Build(f, "Payroll register (gross to net)", $"{run.Reference} · {period.Name} · {run.Status}", $"{run.Reference}-register", rows,
            [
                new("Employee No", x => x.EmployeeNumber), new("Name", x => x.EmployeeName), new("Department", x => x.Department), new("Basic", x => x.BasicPay, true), new("Gross", x => x.GrossPay, true),
                new("Taxable Pay", x => x.TaxablePay, true), new("PAYE", x => x.Paye, true), new("NSSF", x => x.Nssf, true), new("SHIF", x => x.Shif, true), new("Housing Levy", x => x.HousingLevy, true),
                new("Other Deductions", x => x.TotalOtherDeductions, true), new("Total Deductions", x => x.TotalEmployeeDeductions, true), new("Net Pay", x => x.NetPay, true),
                new("Employer Contributions", x => x.TotalEmployerContributions, true), new("Employer Cost", x => x.TotalEmployerCost, true),
            ], ["Totals", null, null, rows.Sum(x => x.BasicPay), run.TotalGross, run.TotalTaxable, run.TotalPaye, run.TotalNssfEmployee, run.TotalShif, run.TotalHousingLevyEmployee, run.TotalOtherDeductions, run.TotalEmployeeDeductions, run.TotalNet, run.TotalEmployerContributions, run.TotalEmployerCost]);
            return await Export(file, audit, db, "report.register.exported", runId, ct);
        });

        r.MapGet("/annual", async (Guid legalEntityId, int year, Guid? employeeId, string? format, ReportingService svc, AuditService audit, PayrollDbContext db, CancellationToken ct) =>
        {
            var report = await svc.AnnualAsync(legalEntityId, year, employeeId, ct);
            if (TabularExport.Parse(format) is not { } f) return Results.Ok(report);
            // One row per employee-month (P9 layout) followed by each employee's total.
            var rows = report.Employees.SelectMany(e => e.Months.Where(m => m.Gross != 0).Select(m => (e, m)).Append((e, m: e.Totals))).ToList();
            var file = TabularExport.Build(f, employeeId is null ? $"Annual payroll summary {year}" : $"Tax deduction card (P9) {year}",
                $"{report.LegalEntity} · Employer PIN {report.EmployerPin}", employeeId is null ? $"annual-summary-{year}" : $"P9-{year}-{report.Employees.FirstOrDefault()?.EmployeeNumber}", rows,
            [
                new("Employee No", x => x.e.EmployeeNumber), new("Name", x => x.e.EmployeeName), new("KRA PIN", x => x.e.KraPin), new("Month", x => x.m.Period),
                new("Gross Pay", x => x.m.Gross, true), new("Allowable Deductions", x => x.m.AllowableDeductions, true), new("Taxable Pay", x => x.m.TaxablePay, true),
                new("Tax Charged", x => x.m.GrossTax, true), new("Reliefs", x => x.m.Reliefs, true), new("PAYE", x => x.m.Paye, true),
                new("NSSF", x => x.m.Nssf, true), new("SHIF", x => x.m.Shif, true), new("Housing Levy", x => x.m.HousingLevy, true), new("Net Pay", x => x.m.NetPay, true),
            ]);
            return await Export(file, audit, db, "report.annual.exported", legalEntityId, ct);
        });

        r.MapGet("/variance", async (Guid runId, string? format, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.ReportsRead); ctx.Require(Permissions.SalaryRead);
            var run = await db.PayrollRuns.Include(x => x.Employees).FirstOrDefaultAsync(x => x.Id == runId, ct) ?? throw new NotFoundException(nameof(PayrollRun), runId);
            ctx.RequireLegalEntity(run.LegalEntityId);
            var previous = await db.PayrollRuns.Where(x => x.PayrollGroupId == run.PayrollGroupId && x.Status == PayrollRunStatus.Finalised && x.Id != runId && x.CreatedAt < run.CreatedAt)
                .OrderByDescending(x => x.CreatedAt).Include(x => x.Employees).FirstOrDefaultAsync(ct);
            var prev = previous?.Employees.ToDictionary(e => e.EmployeeId) ?? new Dictionary<Guid, PayrollRunEmployee>();
            var rows = run.Employees.OrderBy(e => e.EmployeeNumber).Select(e =>
            {
                var p = prev.GetValueOrDefault(e.EmployeeId);
                return new VarianceRow(e.EmployeeNumber, e.EmployeeName, e.GrossPay, p?.GrossPay, e.NetPay, p?.NetPay, p is null ? null : e.NetPay - p.NetPay,
                    p is null || p.NetPay == 0 ? null : Math.Round((e.NetPay - p.NetPay) / p.NetPay * 100, 1), p is null ? "New" : "Existing");
            }).ToList();
            var leavers = prev.Values.Where(p => run.Employees.All(e => e.EmployeeId != p.EmployeeId)).Select(p => new { p.EmployeeNumber, p.EmployeeName, previousNet = p.NetPay }).ToList();
            if (TabularExport.Parse(format) is { } f)
            {
                var all = rows.Concat(leavers.Select(l => new VarianceRow(l.EmployeeNumber, l.EmployeeName, 0, null, 0, l.previousNet, -l.previousNet, -100, "Left"))).ToList();
                var file = TabularExport.Build(f, "Payroll variance", $"{run.Reference} vs {previous?.Reference ?? "—"}", $"{run.Reference}-variance", all,
                [
                    new("Employee No", x => x.EmployeeNumber), new("Name", x => x.EmployeeName), new("Previous Gross", x => x.PreviousGross, true), new("Gross", x => x.CurrentGross, true),
                    new("Previous Net", x => x.PreviousNet, true), new("Net", x => x.CurrentNet, true), new("Net Change", x => x.NetChange, true), new("Change %", x => x.NetChangePercent, true), new("Status", x => x.Status),
                ]);
                return await Export(file, audit, db, "report.variance.exported", runId, ct);
            }
            return Results.Ok(new { current = run.Reference, previous = previous?.Reference, rows, leavers, totals = new { currentNet = run.TotalNet, previousNet = previous?.TotalNet, currentGross = run.TotalGross, previousGross = previous?.TotalGross } });
        });

        r.MapGet("/audit", async (string? entityType, Guid? entityId, string? action, string? format, int? offset, int? limit, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.AuditRead);
            var (off, lim) = Paging.Normalise(offset, limit);
            var q = db.PayrollAuditLogs.AsQueryable();
            if (entityType is not null) q = q.Where(a => a.EntityType == entityType);
            if (entityId is { } id) q = q.Where(a => a.EntityId == id);
            if (action is not null) q = q.Where(a => a.Action.StartsWith(action));
            if (TabularExport.Parse(format) is { } f)
            {
                var all = await q.OrderByDescending(a => a.OccurredAt).Take(50_000).ToListAsync(ct);
                var file = TabularExport.Build(f, "Audit trail", $"{all.Count} entries", "audit-trail", all,
                    [new("When", x => x.OccurredAt), new("Actor", x => x.ActorName), new("Action", x => x.Action), new("Entity", x => x.EntityType), new("Entity Id", x => x.EntityId), new("Details", x => x.Details), new("IP", x => x.IpAddress)]);
                return Results.File(file.Content, file.ContentType, file.FileName);
            }
            var total = await q.CountAsync(ct);
            var items = await q.OrderByDescending(a => a.OccurredAt).Skip(off).Take(lim).ToListAsync(ct);
            return Results.Ok(new Page<PayrollAuditLog>(items, total, off, lim));
        });

        r.MapGet("/notifications", async (NotificationStatus? status, int? offset, int? limit, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var (off, lim) = Paging.Normalise(offset, limit);
            var q = db.NotificationMessages.AsQueryable();
            if (status is { } s) q = q.Where(m => m.Status == s);
            var total = await q.CountAsync(ct);
            var items = (await q.OrderByDescending(m => m.CreatedAt).Skip(off).Take(lim).ToListAsync(ct))
                .Select(m => new { m.Id, m.Channel, recipient = FieldEncryptor.Mask(m.Recipient), m.RecipientName, m.Template, m.Subject, m.Status, m.Attempts, m.SentAt, m.LastError, m.CreatedAt }).ToList();
            return Results.Ok(new { items, total, offset = off, limit = lim });
        });
    }

    private static async Task<IResult> Export(ExportFile file, AuditService audit, PayrollDbContext db, string action, Guid entityId, CancellationToken ct)
    {
        audit.Record(action, "Report", entityId, new { file.FileName });
        await db.SaveChangesAsync(ct);
        return Results.File(file.Content, file.ContentType, file.FileName);
    }
}

public sealed record VarianceRow(string EmployeeNumber, string EmployeeName, decimal CurrentGross, decimal? PreviousGross, decimal CurrentNet, decimal? PreviousNet, decimal? NetChange, decimal? NetChangePercent, string Status);
