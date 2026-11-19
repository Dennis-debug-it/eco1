using System.Security.Cryptography;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;


public static class SelfServiceEndpoints
{
    public static void MapSelfService(this RouteGroupBuilder api)
    {
        // ---- Payslips --------------------------------------------------------------------
        api.MapGet("/employees/{id:guid}/payslips", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            RequireSelfOrSalaryRead(ctx, id);
            var slips = await db.Payslips.Where(p => p.EmployeeId == id).OrderByDescending(p => p.PeriodEnd)
                .Join(db.PayrollRuns, p => p.PayrollRunId, r => r.Id, (p, r) => new { p.Id, p.PayslipNumber, p.PeriodStart, p.PeriodEnd, p.GrossPay, p.NetPay, p.IssuedAt, runReference = r.Reference, r.CurrencyCode, runStatus = r.Status.ToString(), reversed = r.Status == PayrollRunStatus.Reversed })
                .ToListAsync(ct);
            return Results.Ok(slips);
        }).RequireAuthorization();

        api.MapGet("/payslips/{payslipId:guid}", async (Guid payslipId, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var slip = await db.Payslips.FirstOrDefaultAsync(p => p.Id == payslipId, ct) ?? throw new NotFoundException(nameof(Payslip), payslipId);
            RequireSelfOrSalaryRead(ctx, slip.EmployeeId);
            var result = await db.PayrollRunEmployees.Include(e => e.Lines.OrderBy(l => l.Sequence)).FirstAsync(e => e.Id == slip.PayrollRunEmployeeId, ct);
            return Results.Ok(new { slip, result });
        }).RequireAuthorization();

        api.MapGet("/payslips/{payslipId:guid}/pdf", async (Guid payslipId, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var slip = await db.Payslips.FirstOrDefaultAsync(p => p.Id == payslipId, ct) ?? throw new NotFoundException(nameof(Payslip), payslipId);
            RequireSelfOrSalaryRead(ctx, slip.EmployeeId);
            var run = await db.PayrollRuns.FirstAsync(r => r.Id == slip.PayrollRunId, ct);
            var result = await db.PayrollRunEmployees.Include(e => e.Lines).FirstAsync(e => e.Id == slip.PayrollRunEmployeeId, ct);
            var employee = await db.Employees.FirstAsync(e => e.Id == slip.EmployeeId, ct);
            var entity = await db.LegalEntities.FirstAsync(e => e.Id == run.LegalEntityId, ct);
            var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PayrollPeriodId, ct);
            var bank = await db.EmployeeBankAccounts.Where(b => b.EmployeeId == slip.EmployeeId).OrderByDescending(b => b.IsPrimary).FirstOrDefaultAsync(ct);
            var dept = employee.DepartmentId is { } d ? await db.Departments.Where(x => x.Id == d).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
            var stat = await db.EmployeeStatutoryDetails.FirstOrDefaultAsync(s => s.EmployeeId == slip.EmployeeId, ct);

            var pdf = PayslipRenderer.Render(entity, employee, result, run, period, slip, bank, dept, FieldEncryptor.Mask(stat?.KraPin));
            var hash = Convert.ToHexStringLower(SHA256.HashData(pdf));
            slip.ContentHash ??= hash;
            if (ctx.EmployeeId == slip.EmployeeId) slip.FirstViewedAt ??= DateTimeOffset.UtcNow;
            audit.Record("payslip.downloaded", nameof(Payslip), slip.Id, new { slip.PayslipNumber, self = ctx.EmployeeId == slip.EmployeeId }, slip.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.File(pdf, "application/pdf", $"{slip.PayslipNumber}.pdf");
        }).RequireAuthorization();

    }

    private static void RequireSelfOrSalaryRead(ITenantContext ctx, Guid employeeId)
    {
        if (ctx.EmployeeId == employeeId) return;
        ctx.Require(Permissions.SalaryRead);
    }
}
