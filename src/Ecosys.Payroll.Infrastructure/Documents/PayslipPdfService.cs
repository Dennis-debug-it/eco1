using System.Security.Cryptography;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Documents;

/// <summary>
/// Renders an issued payslip as the employee sees it: company letterhead, the run's figures, and the balances captured
/// at issue. Balances missing from payslips issued before they were recorded are worked out once and kept, so every
/// reprint matches. The caller checks access and saves.
/// </summary>
public sealed class PayslipPdfService(PayrollDbContext db, PayslipFacts facts, LetterheadService letterheads)
{
    public async Task<byte[]> RenderAsync(Payslip slip, CancellationToken ct = default)
    {
        var run = await db.PayrollRuns.FirstAsync(r => r.Id == slip.PayrollRunId, ct);
        var result = await db.PayrollRunEmployees.Include(e => e.Lines).FirstAsync(e => e.Id == slip.PayrollRunEmployeeId, ct);
        var employee = await db.Employees.FirstAsync(e => e.Id == slip.EmployeeId, ct);
        var entity = await db.LegalEntities.FirstAsync(e => e.Id == run.LegalEntityId, ct);
        var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PayrollPeriodId, ct);
        var bank = await db.EmployeeBankAccounts.Where(b => b.EmployeeId == slip.EmployeeId).OrderByDescending(b => b.IsPrimary).FirstOrDefaultAsync(ct);
        var dept = employee.DepartmentId is { } d ? await db.Departments.Where(x => x.Id == d).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var stat = await db.EmployeeStatutoryDetails.FirstOrDefaultAsync(s => s.EmployeeId == slip.EmployeeId, ct);

        if (slip.NssfContributionsToDate is null && run.Status != PayrollRunStatus.Reversed)
            slip.NssfContributionsToDate = await facts.NssfToDateAsync(slip.EmployeeId, slip.PeriodEnd, null, ct);
        slip.LeaveBalances ??= await facts.LeaveRemainingAsync(slip.EmployeeId, slip.PeriodEnd, ct);
        var pdf = PayslipRenderer.Render(entity, employee, result, run, period, slip, bank, dept, FieldEncryptor.Mask(stat?.KraPin), slip.NssfContributionsToDate, slip.LeaveBalances, await letterheads.BrandAsync(entity.Id, ct));
        slip.ContentHash ??= Convert.ToHexStringLower(SHA256.HashData(pdf));
        return pdf;
    }
}
