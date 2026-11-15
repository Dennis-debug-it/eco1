using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public static class ReportEndpoints
{
    public static void MapReports(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async (Guid? legalEntityId, ReportingService svc, CancellationToken ct) => Results.Ok(await svc.DashboardAsync(legalEntityId, ct))).RequireAuthorization();

        var r = api.MapGroup("/reports").RequireAuthorization();

        r.MapGet("/statutory", async (Guid runId, string ruleCode, string? format, ReportingService svc, CancellationToken ct) =>
        {
            var report = await svc.StatutoryAsync(runId, ruleCode, ct);
            if (format == "csv")
            {
                var csv = ReportingService.ToCsv(report.Rows,
                    ("Employee No", x => x.EmployeeNumber), ("Employee Name", x => x.EmployeeName), ("PIN", x => x.Pin), ("Member No", x => x.MemberNumber),
                    ("Gross Pay", x => x.Gross), ("Contribution Base", x => x.Base), ("Employee", x => x.Employee), ("Employer", x => x.Employer), ("Total", x => x.Total));
                return Results.File(csv, "text/csv", $"{report.RunReference}-{ruleCode}.csv");
            }
            return Results.Ok(report);
        });

        r.MapGet("/payroll-register", async (Guid runId, string? format, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.ReportsRead); ctx.Require(Permissions.SalaryRead);
            var run = await db.PayrollRuns.Include(x => x.Employees.OrderBy(e => e.EmployeeNumber)).FirstOrDefaultAsync(x => x.Id == runId, ct) ?? throw new NotFoundException(nameof(PayrollRun), runId);
            ctx.RequireLegalEntity(run.LegalEntityId);
            var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name, ct);
            var rows = run.Employees.Select(e => new { e.EmployeeNumber, e.EmployeeName, Department = e.DepartmentId is { } d ? depts.GetValueOrDefault(d) : null, e.BasicPay, e.GrossPay, e.TaxablePay, e.Paye, e.Nssf, e.Shif, e.HousingLevy, e.TotalOtherDeductions, e.TotalEmployeeDeductions, e.NetPay, e.TotalEmployerContributions, e.TotalEmployerCost }).ToList();
            if (format == "csv")
                return Results.File(ReportingService.ToCsv(rows, ("Employee No", x => x.EmployeeNumber), ("Name", x => x.EmployeeName), ("Department", x => x.Department), ("Basic", x => x.BasicPay), ("Gross", x => x.GrossPay),
                    ("Taxable Pay", x => x.TaxablePay), ("PAYE", x => x.Paye), ("NSSF", x => x.Nssf), ("SHIF", x => x.Shif), ("Housing Levy", x => x.HousingLevy), ("Other Deductions", x => x.TotalOtherDeductions),
                    ("Total Deductions", x => x.TotalEmployeeDeductions), ("Net Pay", x => x.NetPay), ("Employer Contributions", x => x.TotalEmployerContributions), ("Employer Cost", x => x.TotalEmployerCost)), "text/csv", $"{run.Reference}-register.csv");
            return Results.Ok(new { run.Reference, run.Status, rows, totals = new { run.TotalGross, run.TotalPaye, run.TotalNssfEmployee, run.TotalShif, run.TotalHousingLevyEmployee, run.TotalOtherDeductions, run.TotalNet, run.TotalEmployerContributions, run.TotalEmployerCost } });
        });

        r.MapGet("/variance", async (Guid runId, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.ReportsRead); ctx.Require(Permissions.SalaryRead);
            var run = await db.PayrollRuns.Include(x => x.Employees).FirstOrDefaultAsync(x => x.Id == runId, ct) ?? throw new NotFoundException(nameof(PayrollRun), runId);
            var previous = await db.PayrollRuns.Where(x => x.PayrollGroupId == run.PayrollGroupId && x.Status == PayrollRunStatus.Finalised && x.Id != runId && x.CreatedAt < run.CreatedAt)
                .OrderByDescending(x => x.CreatedAt).Include(x => x.Employees).FirstOrDefaultAsync(ct);
            var prev = previous?.Employees.ToDictionary(e => e.EmployeeId) ?? new Dictionary<Guid, PayrollRunEmployee>();
            var rows = run.Employees.Select(e =>
            {
                var p = prev.GetValueOrDefault(e.EmployeeId);
                return new { e.EmployeeNumber, e.EmployeeName, currentGross = e.GrossPay, previousGross = p?.GrossPay, currentNet = e.NetPay, previousNet = p?.NetPay, netChange = p is null ? (decimal?)null : e.NetPay - p.NetPay,
                    netChangePercent = p is null || p.NetPay == 0 ? (decimal?)null : Math.Round((e.NetPay - p.NetPay) / p.NetPay * 100, 1), status = p is null ? "New" : "Existing" };
            }).ToList();
            var leavers = prev.Values.Where(p => run.Employees.All(e => e.EmployeeId != p.EmployeeId)).Select(p => new { p.EmployeeNumber, p.EmployeeName, previousNet = p.NetPay }).ToList();
            return Results.Ok(new { current = run.Reference, previous = previous?.Reference, rows, leavers, totals = new { currentNet = run.TotalNet, previousNet = previous?.TotalNet, currentGross = run.TotalGross, previousGross = previous?.TotalGross } });
        });

        r.MapGet("/audit", async (string? entityType, Guid? entityId, string? action, int? offset, int? limit, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.AuditRead);
            var (off, lim) = Paging.Normalise(offset, limit);
            var q = db.PayrollAuditLogs.AsQueryable();
            if (entityType is not null) q = q.Where(a => a.EntityType == entityType);
            if (entityId is { } id) q = q.Where(a => a.EntityId == id);
            if (action is not null) q = q.Where(a => a.Action.StartsWith(action));
            var total = await q.CountAsync(ct);
            var items = await q.OrderByDescending(a => a.OccurredAt).Skip(off).Take(lim).ToListAsync(ct);
            return Results.Ok(new Page<PayrollAuditLog>(items, total, off, lim));
        });
    }
}
