using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Infrastructure.Services;
using Ecosys.Payroll.Infrastructure.Services.Hr;

namespace Ecosys.Payroll.Tests.Engine;

public class LeaveRulesTests
{
    private static readonly LeaveType Sick = new() { Code = "SICK", Name = "Sick leave", AnnualEntitlementDays = 14, FullPayDaysPerYear = 7, HalfPayDaysPerYear = 7, MinServiceMonths = 2 };

    [Theory]
    [InlineData(0, 5, 5, 0, 0)]
    [InlineData(5, 6, 2, 4, 0)]   // 2 days left on full pay, then half pay
    [InlineData(12, 5, 0, 2, 3)]  // 2 half-pay days left, rest unpaid
    [InlineData(14, 3, 0, 0, 3)]
    public void Sick_leave_follows_employment_act_tiers(decimal taken, decimal days, decimal full, decimal half, decimal unpaid)
    {
        var s = LeaveService.Split(Sick, days, taken, serviceEligible: true);
        Assert.Equal((full, half, unpaid), (s.FullPay, s.HalfPay, s.Unpaid));
    }

    [Fact]
    public void Before_qualifying_service_sick_leave_is_unpaid() =>
        Assert.Equal(3m, LeaveService.Split(Sick, 3, 0, serviceEligible: false).Unpaid);

    [Fact]
    public void Deduction_is_allocated_in_date_order_across_periods()
    {
        // Mon 2 Mar – Fri 13 Mar 2026 = 10 working days: 2 full, 4 half, 4 unpaid.
        var r = new LeaveRequest { StartDate = new(2026, 3, 2), EndDate = new(2026, 3, 13), Days = 10, FullPayDays = 2, HalfPayDays = 4, UnpaidDays = 4 };
        var none = new HashSet<DateOnly>();
        Assert.Equal(6m, PayrollInputAssembler.LeaveDeductionDays(r, false, true, new(2026, 3, 1), new(2026, 3, 31), none));
        // Week one only (positions 1–5): 2 full + 3 half = 1.5 days deducted; week two: 1 half + 4 unpaid = 4.5.
        Assert.Equal(1.5m, PayrollInputAssembler.LeaveDeductionDays(r, false, true, new(2026, 3, 1), new(2026, 3, 6), none));
        Assert.Equal(4.5m, PayrollInputAssembler.LeaveDeductionDays(r, false, true, new(2026, 3, 7), new(2026, 3, 31), none));
    }

    [Fact]
    public void Unpaid_leave_spanning_two_months_is_deducted_in_each()
    {
        var r = new LeaveRequest { StartDate = new(2026, 1, 26), EndDate = new(2026, 2, 6), Days = 10 };
        var none = new HashSet<DateOnly>();
        Assert.Equal(5m, PayrollInputAssembler.LeaveDeductionDays(r, true, true, new(2026, 1, 1), new(2026, 1, 31), none));
        Assert.Equal(5m, PayrollInputAssembler.LeaveDeductionDays(r, true, true, new(2026, 2, 1), new(2026, 2, 28), none));
    }

    [Theory]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    public void Easter_dates(int y, int m, int d) => Assert.Equal(new DateOnly(y, m, d), WorkCalendar.EasterSunday(y));

    [Fact]
    public void Sunday_holidays_move_to_the_next_free_day()
    {
        // 2022: Christmas on Sunday; the 26th is already Utamaduni Day, so Christmas is observed on Tuesday 27th.
        var y2022 = WorkCalendar.WithObservedDates(WorkCalendar.KenyaHolidays(2022)).ToDictionary(h => h.Name, h => h.Observed);
        Assert.Equal(new DateOnly(2022, 12, 27), y2022["Christmas Day"]);
        Assert.Equal(new DateOnly(2022, 12, 26), y2022["Utamaduni Day"]);
        // 2024: Mashujaa Day (20 Oct) was a Sunday → Monday 21st.
        Assert.Equal(new DateOnly(2024, 10, 21), WorkCalendar.WithObservedDates(WorkCalendar.KenyaHolidays(2024)).Single(h => h.Name == "Mashujaa Day").Observed);
    }

    [Fact]
    public void Working_days_skip_weekends_and_holidays()
    {
        var holidays = new HashSet<DateOnly> { new(2026, 4, 3), new(2026, 4, 6) }; // Good Friday, Easter Monday
        Assert.Equal(8, WorkCalendar.WorkingDates(new(2026, 3, 30), new(2026, 4, 10), holidays).Count());
    }
}

public class ExitRulesTests
{
    [Theory]
    [InlineData("2018-03-01", "2026-02-28", 7)]
    [InlineData("2018-03-01", "2026-03-01", 8)]
    [InlineData("2026-01-10", "2026-06-30", 0)]
    public void Completed_years_of_service(string hire, string exit, int years) =>
        Assert.Equal(years, SeparationService.CompletedYears(DateOnly.Parse(hire), DateOnly.Parse(exit)));

    [Fact]
    public void Training_bond_outstanding_is_pro_rata_by_days()
    {
        var bond = new Ecosys.Payroll.Domain.Hr.TrainingBond { Amount = 120_000m, StartDate = new(2026, 1, 1), Months = 12 };
        Assert.Equal(120_000m, bond.OutstandingOn(new(2025, 12, 1)));
        Assert.Equal(0m, bond.OutstandingOn(new(2027, 1, 1)));
        // 1 Jul 2026: 184 of 365 days remain.
        Assert.Equal(Math.Round(120_000m * 184 / 365, 2), bond.OutstandingOn(new(2026, 7, 1)));
    }
}
