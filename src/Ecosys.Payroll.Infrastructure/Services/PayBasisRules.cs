using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Organisation;

namespace Ecosys.Payroll.Infrastructure.Services;

/// <summary>
/// One place that turns a pay basis into a salary assignment, used by the salary screen, the employee import and
/// compensation reviews alike. A daily-paid worker's BasicSalary is the monthly equivalent of the daily rate, so every
/// monthly figure elsewhere (notice pay, final dues, contracts, reports) stays meaningful.
/// </summary>
public static class PayBasisRules
{
    /// <summary>The company's standard working days in a month (22 if the setting predates this field).</summary>
    public static decimal WorkingDays(LegalEntity entity) => entity.Settings.StandardMonthlyWorkingDays > 0 ? entity.Settings.StandardMonthlyWorkingDays : 22m;

    public static decimal MonthlyEquivalent(decimal dailyRate, LegalEntity entity) => Math.Round(dailyRate * WorkingDays(entity), 2, MidpointRounding.AwayFromZero);

    /// <summary>Sets basis, rate and monthly figure; throws on a missing or negative rate.</summary>
    public static void Apply(EmployeeSalaryAssignment a, PayBasis basis, decimal monthlyBasic, decimal? dailyRate, LegalEntity entity)
    {
        a.PayBasis = basis;
        if (basis == PayBasis.Daily)
        {
            if (dailyRate is not { } rate || rate <= 0) throw new DomainException("A daily-paid worker needs a daily rate above zero.", "VALIDATION");
            a.DailyRate = Math.Round(rate, 2, MidpointRounding.AwayFromZero);
            a.BasicSalary = MonthlyEquivalent(a.DailyRate.Value, entity);
        }
        else
        {
            if (monthlyBasic < 0) throw new DomainException("Basic salary cannot be negative.", "VALIDATION");
            a.DailyRate = null;
            a.BasicSalary = monthlyBasic;
        }
    }
}
