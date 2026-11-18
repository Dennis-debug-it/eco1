using Ecosys.Payroll.Engine.Jurisdictions.Kenya;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Engine.Rules;

namespace Ecosys.Payroll.Tests.Engine;

/// <summary>
/// Hand-worked examples. Each expected figure is derived in the comment so a payroll
/// practitioner can check the arithmetic independently of the code (PRD §18).
/// </summary>
public class KenyaCalculatorTests
{
    private static readonly KenyaPayrollCalculator Calc = new();

    private static PayrollPeriodInput Period(int year, int month) =>
        new(Guid.NewGuid(), new DateOnly(year, month, 1), new DateOnly(year, month, DateTime.DaysInMonth(year, month)), "KE", "KES");

    private static StatutoryRuleSet Rules(PayrollPeriodInput p) => StatutoryRuleSet.Resolve("KE", p.RuleDate, KenyaStatutoryRules.All());

    private static EmployeePayrollInput Employee(decimal basic, params DeductionInput[] deductions) => new()
    {
        EmployeeId = Guid.NewGuid(), EmployeeNumber = "E001", DisplayName = "Test Employee",
        Earnings = [new EarningInput("BASIC", "Basic salary", basic, Taxable: true, Pensionable: true, IsBasic: true)],
        Deductions = deductions,
    };

    [Fact]
    public void Gross_100k_March_2025()
    {
        // NSSF yr3: Tier I 6% x 8,000 = 480; Tier II 6% x 64,000 = 3,840 → 4,320 (employee and employer)
        // SHIF 2.75% x 100,000 = 2,750; AHL 1.5% = 1,500 (+1,500 employer); NITA employer 50
        // Allowable: 4,320 + 2,750 + 1,500 = 8,570 → taxable pay 91,430
        // Tax: 24,000@10% = 2,400; 8,333@25% = 2,083.25; 59,097@30% = 17,729.10 → 22,212.35; less 2,400 → 19,812.35
        // Net: 100,000 − (4,320 + 2,750 + 1,500 + 19,812.35) = 71,617.65; employer cost 100,000 + 4,320 + 1,500 + 50 = 105,870
        var p = Period(2025, 3);
        var r = Calc.Calculate(Employee(100_000m), p, Rules(p));

        Assert.Equal(4_320m, r.StatutoryAmount(KenyaRuleCodes.Nssf));
        Assert.Equal(4_320m, r.EmployerAmount(KenyaRuleCodes.Nssf));
        Assert.Equal(2_750m, r.StatutoryAmount(KenyaRuleCodes.Shif));
        Assert.Equal(1_500m, r.StatutoryAmount(KenyaRuleCodes.HousingLevy));
        Assert.Equal(1_500m, r.EmployerAmount(KenyaRuleCodes.HousingLevy));
        Assert.Equal(50m, r.EmployerAmount(KenyaRuleCodes.Nita));
        Assert.Equal(91_430m, r.TaxablePay);
        Assert.Equal(22_212.35m, r.GrossTax);
        Assert.Equal(19_812.35m, r.Paye);
        Assert.Equal(71_617.65m, r.NetPay);
        Assert.Equal(105_870m, r.TotalEmployerCost);
        Assert.False(r.HasErrors);
        Assert.Equal(6, r.StatutoryRuleVersionIds.Count); // PAYE, allowable, NSSF, SHIF, AHL, NITA
    }

    [Fact]
    public void Low_earner_SHIF_minimum_and_zero_PAYE()
    {
        // Gross 10,000: NSSF 480 + 6% x 2,000 = 600; SHIF 275 → floor 300; AHL 150
        // Taxable pay 10,000 − 1,050 = 8,950 → tax 895 < relief 2,400 → PAYE 0. Net 8,950.
        var p = Period(2025, 6);
        var r = Calc.Calculate(Employee(10_000m), p, Rules(p));

        Assert.Equal(600m, r.StatutoryAmount(KenyaRuleCodes.Nssf));
        Assert.Equal(300m, r.StatutoryAmount(KenyaRuleCodes.Shif));
        Assert.Equal(0m, r.Paye);
        Assert.Equal(895m, r.TotalReliefs); // only the relief actually used is reported
        Assert.Equal(8_950m, r.NetPay);
    }

    [Fact]
    public void Historical_period_uses_rules_in_force_then()
    {
        // November 2024: NSSF yr2 (LEL 7,000 / UEL 36,000), SHIF & AHL NOT deductible, pension cap 20,000.
        // Gross 50,000: NSSF 420 + 6% x 29,000 = 1,740 → 2,160; SHIF 1,375; AHL 750
        // Taxable pay 50,000 − 2,160 = 47,840 → 2,400 + 2,083.25 + 15,507 x 30% (4,652.10) = 9,135.35 − 2,400 = 6,735.35
        var p = Period(2024, 11);
        var rules = Rules(p);
        var r = Calc.Calculate(Employee(50_000m), p, rules);

        Assert.Equal(2, rules.Require(KenyaRuleCodes.Nssf).Version);
        Assert.Equal(2_160m, r.StatutoryAmount(KenyaRuleCodes.Nssf));
        Assert.Equal(47_840m, r.TaxablePay);
        Assert.Equal(6_735.35m, r.Paye);

        // Same gross in March 2025 produces a different (lower) PAYE because SHIF/AHL became deductible and NSSF rose.
        var p2 = Period(2025, 3);
        var r2 = Calc.Calculate(Employee(50_000m), p2, Rules(p2));
        Assert.Equal(3, Rules(p2).Require(KenyaRuleCodes.Nssf).Version);
        Assert.True(r2.Paye < r.Paye);
    }

    [Fact]
    public void Pension_cap_and_insurance_relief()
    {
        // Gross 200,000, registered pension 40,000, insurance premium 20,000 (Mar 2025).
        // NSSF 4,320; pension contributions 44,320 → capped 30,000. SHIF 5,500; AHL 3,000.
        // Allowable 30,000 + 8,500 = 38,500 → taxable 161,500
        // Tax: 2,400 + 2,083.25 + 129,167 x 30% (38,750.10) = 43,233.35
        // Insurance relief 15% x 20,000 = 3,000 (cap 5,000). PAYE = 43,233.35 − 2,400 − 3,000 = 37,833.35
        var p = Period(2025, 3);
        var emp = Employee(200_000m,
            new DeductionInput("PENSION", "Staff pension", 40_000m, DeductionTreatment.RegisteredPension, EmployerAmount: 40_000m),
            new DeductionInput("LIFE", "Life cover", 20_000m, DeductionTreatment.QualifyingInsurancePremium));
        var r = Calc.Calculate(emp, p, Rules(p));

        Assert.Equal(161_500m, r.TaxablePay);
        Assert.Equal(37_833.35m, r.Paye);
        Assert.Equal(40_000m, r.Lines.Single(l => l.Code == "PENSION.ER").Amount);
        Assert.Equal(200_000m + 4_320m + 3_000m + 50m + 40_000m, r.TotalEmployerCost);
    }

    [Fact]
    public void Top_bands_apply_above_500k_and_800k()
    {
        // Gross 1,000,000 Mar 2025: NSSF 4,320; SHIF 27,500; AHL 15,000 → allowable 46,820 → taxable 953,180
        // 2,400 + 2,083.25 + 467,667 x 30% (140,300.10) + 300,000 x 32.5% (97,500) + 153,180 x 35% (53,613) = 295,896.35 − 2,400 = 293,496.35
        var p = Period(2025, 3);
        var r = Calc.Calculate(Employee(1_000_000m), p, Rules(p));
        Assert.Equal(953_180m, r.TaxablePay);
        Assert.Equal(293_496.35m, r.Paye);
    }

    [Fact]
    public void Non_taxable_earning_excluded_from_PAYE_but_in_gross()
    {
        var p = Period(2025, 3);
        var emp = Employee(50_000m) with
        {
            Earnings =
            [
                new EarningInput("BASIC", "Basic", 50_000m, Taxable: true, Pensionable: true, IsBasic: true),
                new EarningInput("REIMB", "Expense reimbursement", 5_000m, Taxable: false),
            ],
        };
        var r = Calc.Calculate(emp, p, Rules(p));
        Assert.Equal(55_000m, r.GrossPay);
        Assert.Equal(50_000m, r.TaxableEarnings);
        // SHIF/AHL are on gross (55,000); NSSF on pensionable (50,000)
        Assert.Equal(1_512.5m, r.StatutoryAmount(KenyaRuleCodes.Shif));
        Assert.Equal(3_000m, r.StatutoryAmount(KenyaRuleCodes.Nssf));
    }

    [Fact]
    public void Negative_net_is_flagged_as_error()
    {
        var p = Period(2025, 3);
        var emp = Employee(20_000m, new DeductionInput("LOAN", "Staff loan", 25_000m));
        var r = Calc.Calculate(emp, p, Rules(p));
        Assert.True(r.NetPay < 0);
        Assert.Contains(r.Issues, i => i.Code == "NEGATIVE_NET" && i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Statutory_exemption_skips_rule_but_records_it()
    {
        var p = Period(2025, 3);
        var emp = Employee(100_000m) with { StatutoryExemptions = [KenyaRuleCodes.Nssf] };
        var r = Calc.Calculate(emp, p, Rules(p));
        Assert.Equal(0m, r.StatutoryAmount(KenyaRuleCodes.Nssf));
        Assert.Contains(r.Lines, l => l.Code == KenyaRuleCodes.Nssf && l.Type == ResultLineType.Information);
        // No pension allowable → taxable = 100,000 − 4,250
        Assert.Equal(95_750m, r.TaxablePay);
    }
}

public class StatutoryRuleSetTests
{
    private static StatutoryRuleVersion Rule(string code, int version, DateOnly from, DateOnly? to, RuleApprovalStatus status = RuleApprovalStatus.Approved) => new()
    {
        Id = Guid.NewGuid(), RuleCode = code, Jurisdiction = "KE", Authority = "X", Name = code, Version = version,
        Method = StatutoryCalculationMethod.FixedAmount, EffectiveFrom = from, EffectiveTo = to, ApprovalStatus = status,
    };

    [Fact]
    public void Overlapping_approved_versions_are_rejected()
    {
        var rules = new[]
        {
            Rule("X", 1, new(2025, 1, 1), null),
            Rule("X", 2, new(2025, 7, 1), null),
        };
        var ex = Assert.Throws<StatutoryConfigurationException>(() => StatutoryRuleSet.Resolve("KE", new(2025, 8, 1), rules));
        Assert.Contains("must not overlap", ex.Message);
    }

    [Fact]
    public void Unapproved_versions_are_ignored()
    {
        var rules = new[]
        {
            Rule("X", 1, new(2025, 1, 1), null),
            Rule("X", 2, new(2025, 7, 1), null, RuleApprovalStatus.PendingApproval),
        };
        var set = StatutoryRuleSet.Resolve("KE", new(2025, 8, 1), rules);
        Assert.Equal(1, set.Require("X").Version);
    }

    [Fact]
    public void Version_boundaries_are_inclusive()
    {
        var rules = new[] { Rule("X", 1, new(2025, 1, 1), new(2025, 6, 30)), Rule("X", 2, new(2025, 7, 1), null) };
        Assert.Equal(1, StatutoryRuleSet.Resolve("KE", new(2025, 6, 30), rules).Require("X").Version);
        Assert.Equal(2, StatutoryRuleSet.Resolve("KE", new(2025, 7, 1), rules).Require("X").Version);
    }

    [Fact]
    public void Seeded_Kenya_rules_resolve_cleanly_for_every_month_since_July_2023()
    {
        for (var d = new DateOnly(2023, 7, 31); d <= new DateOnly(2026, 12, 31); d = d.AddMonths(1))
        {
            var set = StatutoryRuleSet.Resolve("KE", d, KenyaStatutoryRules.All());
            Assert.NotNull(set.Find(KenyaRuleCodes.Paye));
        }
    }

    [Fact]
    public void Tax_band_validation_rejects_gaps()
    {
        var bands = new List<TaxBand> { new(0, 24_000, 0.1m), new(25_000, null, 0.25m) };
        Assert.Throws<StatutoryConfigurationException>(() => RuleEvaluator.ValidateBands(bands));
    }
}

public class KenyaYear4AndFrequencyTests
{
    private static readonly KenyaPayrollCalculator Calc = new();

    private static EmployeePayrollInput Employee(decimal basic) => new()
    {
        EmployeeId = Guid.NewGuid(), EmployeeNumber = "E001", DisplayName = "Test",
        Earnings = [new EarningInput("BASIC", "Basic salary", basic, Taxable: true, Pensionable: true, IsBasic: true)],
    };

    [Fact]
    public void Nssf_year4_applies_from_February_2026()
    {
        // Gross 150,000 (Mar 2026): NSSF 6% x 9,000 = 540 + 6% x 99,000 = 5,940 → 6,480 (capped at UEL 108,000)
        // SHIF 4,125; AHL 2,250 → allowable 12,855 → taxable 137,145
        // Tax 2,400 + 2,083.25 + 104,812 x 30% (31,443.60) = 35,926.85 − 2,400 = 33,526.85
        var p = new PayrollPeriodInput(Guid.NewGuid(), new(2026, 3, 1), new(2026, 3, 31), "KE", "KES");
        var rules = StatutoryRuleSet.Resolve("KE", p.RuleDate, KenyaStatutoryRules.All());
        var r = Calc.Calculate(Employee(150_000m), p, rules);
        Assert.Equal(4, rules.Require(KenyaRuleCodes.Nssf).Version);
        Assert.Equal(6_480m, r.StatutoryAmount(KenyaRuleCodes.Nssf));
        Assert.Equal(6_480m, r.EmployerAmount(KenyaRuleCodes.Nssf));
        Assert.Equal(137_145m, r.TaxablePay);
        Assert.Equal(33_526.85m, r.Paye);

        // January 2026 still uses year 3 (max 4,320).
        var jan = new PayrollPeriodInput(Guid.NewGuid(), new(2026, 1, 1), new(2026, 1, 31), "KE", "KES");
        var rJan = Calc.Calculate(Employee(150_000m), jan, StatutoryRuleSet.Resolve("KE", jan.RuleDate, KenyaStatutoryRules.All()));
        Assert.Equal(4_320m, rJan.StatutoryAmount(KenyaRuleCodes.Nssf));
    }

    [Fact]
    public void Semi_monthly_period_halves_thresholds_and_matches_monthly_tax()
    {
        // Two semi-monthly halves of 50,000 should give the same PAYE as one monthly 100,000 (bands scale linearly).
        var monthly = new PayrollPeriodInput(Guid.NewGuid(), new(2026, 3, 1), new(2026, 3, 31), "KE", "KES");
        var half = new PayrollPeriodInput(Guid.NewGuid(), new(2026, 3, 1), new(2026, 3, 15), "KE", "KES", PeriodFrequency.SemiMonthly);
        var rules = StatutoryRuleSet.Resolve("KE", monthly.RuleDate, KenyaStatutoryRules.All());
        var m = Calc.Calculate(Employee(100_000m), monthly, rules);
        var h = Calc.Calculate(Employee(50_000m), half, rules);
        Assert.Equal(m.StatutoryAmount(KenyaRuleCodes.Nssf), h.StatutoryAmount(KenyaRuleCodes.Nssf) * 2);
        Assert.InRange(m.Paye - h.Paye * 2, -0.02m, 0.02m); // each half is rounded to the cent independently
        Assert.Equal(25m, h.EmployerAmount(KenyaRuleCodes.Nita)); // KES 50/month levy split per half
    }

    [Fact]
    public void Weekly_period_scales_personal_relief()
    {
        var weekly = new PayrollPeriodInput(Guid.NewGuid(), new(2026, 3, 2), new(2026, 3, 8), "KE", "KES", PeriodFrequency.Weekly);
        var rules = StatutoryRuleSet.Resolve("KE", weekly.RuleDate, KenyaStatutoryRules.All()).Scale(weekly.MonthlyScaleFactor);
        Assert.Equal(Math.Round(2_400m * 12m / 52m, 4), rules.Require(KenyaRuleCodes.Paye).Parameters.PersonalRelief);
        var r = Calc.Calculate(Employee(5_000m), weekly, rules);
        Assert.Equal(0m, r.Paye); // ~KES 21.7k/month equivalent is below the relief threshold
    }
}

public class LeaveAccrualTests
{
    private static readonly Ecosys.Payroll.Domain.Leave.LeaveType Annual = new() { Code = "ANNUAL", Name = "Annual", AnnualEntitlementDays = 21, AccruesMonthly = true, MaxCarryOverDays = 5 };

    [Theory]
    [InlineData("2018-03-01", 2026, "2026-11-18", 19.25)] // 11 months of 2026 elapsed
    [InlineData("2026-03-10", 2026, "2026-11-18", 15.75)] // joined March (by the 15th) → Mar..Nov = 9 months
    [InlineData("2026-03-20", 2026, "2026-11-18", 14.00)] // joined after the 15th → Apr..Nov = 8 months
    [InlineData("2018-03-01", 2025, "2026-11-18", 21.00)] // past year fully accrued
    [InlineData("2027-01-05", 2026, "2026-11-18", 0)]     // not yet employed
    public void Monthly_accrual_is_prorated(string hire, int year, string today, decimal expected) =>
        Assert.Equal(expected, Ecosys.Payroll.Infrastructure.Services.LeaveBalanceService.Accrued(Annual, DateOnly.Parse(hire), year, DateOnly.Parse(today)));
}
