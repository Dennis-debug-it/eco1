using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services;

/// <summary>
/// Opens leave years and keeps accrual current. A year's balance is created on first use: carry-over from
/// the previous year (capped at the type's maximum), plus entitlement accrued monthly (or granted up front),
/// pro-rated from the hire date in the joining year. Caller saves.
/// </summary>
public sealed class LeaveBalanceService(PayrollDbContext db, TimeProvider clock)
{
    public async Task EnsureAsync(Guid employeeId, int year, CancellationToken ct = default)
    {
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (emp is null) return;
        var types = await db.LeaveTypes.Where(t => t.LegalEntityId == emp.LegalEntityId && t.IsActive && t.IsPaid && t.AnnualEntitlementDays > 0).ToListAsync(ct);
        var balances = await db.EmployeeLeaveBalances.Where(b => b.EmployeeId == employeeId && (b.Year == year || b.Year == year - 1)).ToListAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        foreach (var t in types)
        {
            var accrued = Accrued(t, emp.HireDate, year, today);
            var current = balances.FirstOrDefault(b => b.LeaveTypeId == t.Id && b.Year == year);
            if (current is null)
            {
                var prev = balances.FirstOrDefault(b => b.LeaveTypeId == t.Id && b.Year == year - 1);
                var carry = prev is null ? 0m : Math.Min(Math.Max(prev.Available, 0m), t.MaxCarryOverDays);
                db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
                {
                    TenantId = emp.TenantId, LegalEntityId = emp.LegalEntityId, EmployeeId = employeeId, LeaveTypeId = t.Id, Year = year,
                    Entitlement = t.AnnualEntitlementDays, Accrued = accrued, CarriedOver = carry,
                });
            }
            else if (accrued > current.Accrued)
            {
                current.Accrued = accrued; // accrual only moves forward; manual adjustments above the formula are kept
            }
        }
    }

    public static decimal Accrued(LeaveType t, DateOnly hireDate, int year, DateOnly today)
    {
        if (hireDate.Year > year) return 0m;
        // Months of service in the year (joining month counts if hired by the 15th).
        var firstMonth = hireDate.Year == year ? hireDate.Month + (hireDate.Day > 15 ? 1 : 0) : 1;
        var serviceMonths = Math.Max(0, 13 - firstMonth);
        if (!t.AccruesMonthly) return Math.Round(t.AnnualEntitlementDays * serviceMonths / 12m, 2);
        var lastMonth = year < today.Year ? 12 : year > today.Year ? 0 : today.Month;
        var months = Math.Max(0, lastMonth - firstMonth + 1);
        return Math.Round(t.AnnualEntitlementDays * months / 12m, 2);
    }
}
