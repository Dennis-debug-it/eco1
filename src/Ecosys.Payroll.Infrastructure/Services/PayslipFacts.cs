using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Engine.Jurisdictions.Kenya;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services;

/// <summary>
/// Running balances printed on a payslip: NSSF contributions to date and leave days remaining. Worked out when the
/// payslip is issued and stored on it, so a reprint shows the same figures.
/// </summary>
public sealed class PayslipFacts(PayrollDbContext db, LeaveBalanceService leave, TimeProvider clock)
{
    /// <summary>Runs whose figures count: approved and later, never reversed.</summary>
    private static readonly PayrollRunStatus[] Counted = [PayrollRunStatus.Approved, PayrollRunStatus.PaymentProcessing, PayrollRunStatus.Paid, PayrollRunStatus.Finalised];

    /// <summary>
    /// Employee plus employer NSSF contributions for pay periods ending on or before <paramref name="periodEnd"/>, plus
    /// the opening balance carried in from before this payroll. <paramref name="includeRunId"/> counts a run that is
    /// being finalised in the same transaction.
    /// </summary>
    public async Task<decimal> NssfToDateAsync(Guid employeeId, DateOnly periodEnd, Guid? includeRunId, CancellationToken ct = default)
    {
        var runIds = db.PayrollRuns
            .Where(r => r.Id == includeRunId || Counted.Contains(r.Status))
            .Join(db.PayrollPeriods, r => r.PayrollPeriodId, p => p.Id, (r, p) => new { r.Id, p.PeriodEnd })
            .Where(x => x.PeriodEnd <= periodEnd).Select(x => x.Id);
        var contributed = await db.PayrollRunEmployees.Where(e => e.EmployeeId == employeeId && runIds.Contains(e.PayrollRunId))
            .Join(db.PayrollResultLines, e => e.Id, l => l.PayrollRunEmployeeId, (e, l) => l)
            .Where(l => l.StatutoryRuleCode == KenyaRuleCodes.Nssf && (l.Type == ResultLineType.StatutoryDeduction || l.Type == ResultLineType.EmployerContribution))
            .SumAsync(l => (decimal?)l.Amount, ct) ?? 0m;
        var opening = await db.EmployeeStatutoryDetails.Where(s => s.EmployeeId == employeeId).Select(s => (decimal?)s.NssfOpeningBalance).FirstOrDefaultAsync(ct) ?? 0m;
        return opening + contributed;
    }

    /// <summary>
    /// Days remaining at the end of the pay period for leave types that accrue monthly (annual leave). Accrual earned
    /// and leave taken after the period end are both left out, so a payslip issued late still shows the period-end figure.
    /// Opens the leave year if needed; the caller saves.
    /// </summary>
    public async Task<List<PayslipLeaveBalance>> LeaveRemainingAsync(Guid employeeId, DateOnly periodEnd, CancellationToken ct = default)
    {
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (emp is null) return [];
        await leave.EnsureAsync(employeeId, periodEnd.Year, ct);
        var types = await db.LeaveTypes.Where(t => t.LegalEntityId == emp.LegalEntityId && t.IsActive && t.IsPaid && t.AccruesMonthly && t.AnnualEntitlementDays > 0)
            .OrderBy(t => t.Name).ToListAsync(ct);
        var today = BusinessClock.Today(clock);
        var result = new List<PayslipLeaveBalance>();
        foreach (var t in types)
        {
            // EnsureAsync leaves new balances in the change tracker only, so read from there.
            var b = db.EmployeeLeaveBalances.Local.FirstOrDefault(x => x.EmployeeId == employeeId && x.LeaveTypeId == t.Id && x.Year == periodEnd.Year && !x.IsDeleted);
            if (b is null) continue;
            var laterAccrual = Math.Max(0m, LeaveBalanceService.Accrued(t, emp.HireDate, periodEnd.Year, today) - LeaveBalanceService.Accrued(t, emp.HireDate, periodEnd.Year, periodEnd));
            var yearEnd = new DateOnly(periodEnd.Year, 12, 31);
            var takenLater = await db.LeaveRequests.Where(r => r.EmployeeId == employeeId && r.LeaveTypeId == t.Id && r.Status == Domain.Leave.LeaveRequestStatus.Approved
                && r.StartDate > periodEnd && r.StartDate <= yearEnd).SumAsync(r => (decimal?)r.Days, ct) ?? 0m;
            result.Add(new PayslipLeaveBalance { LeaveType = t.Name, DaysRemaining = Math.Round(b.Available - laterAccrual + takenLater, 2) });
        }
        return result;
    }
}
