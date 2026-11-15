using Ecosys.Payroll.Engine.Rules;

namespace Ecosys.Payroll.Engine.Jurisdictions.Kenya;

/// <summary>
/// Seed statutory configuration for Kenya. These are the published parameters as understood at
/// authoring time and are shipped in <see cref="RuleApprovalStatus.Approved"/> state for the
/// reference tenant only so that tests and demos run. <b>Per PRD §4.3 they must be independently
/// verified against KRA, NSSF, SHA and NITA publications before any production deployment.</b>
/// Deterministic GUIDs keep seeded ids stable across environments.
/// </summary>
public static class KenyaStatutoryRules
{
    private static Guid Id(string key) =>
        new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("ecosys.payroll.ke." + key)));

    public static IReadOnlyList<StatutoryRuleVersion> All() =>
    [
        // ---------------- PAYE -------------------------------------------------------------
        new StatutoryRuleVersion
        {
            Id = Id("paye.v1"), RuleCode = KenyaRuleCodes.Paye, Jurisdiction = "KE", Authority = "KRA", Name = "PAYE",
            Version = 1, Method = StatutoryCalculationMethod.ProgressiveBands, Base = StatutoryBase.TaxablePay,
            EffectiveFrom = new DateOnly(2023, 7, 1), EffectiveTo = null,
            ApprovalStatus = RuleApprovalStatus.Approved,
            LegalReference = "Income Tax Act Third Schedule as amended by Finance Act 2023 (monthly bands); Tax Laws (Amendment) Act 2024",
            Parameters = new StatutoryRuleParameters
            {
                Bands =
                [
                    new TaxBand(0m, 24_000m, 0.10m),
                    new TaxBand(24_000m, 32_333m, 0.25m),
                    new TaxBand(32_333m, 500_000m, 0.30m),
                    new TaxBand(500_000m, 800_000m, 0.325m),
                    new TaxBand(800_000m, null, 0.35m),
                ],
                PersonalRelief = 2_400m,
                InsuranceReliefRate = 0.15m,
                InsuranceReliefCap = 5_000m,
            },
        },

        // Allowable deduction caps — version 1 (to 26 Dec 2024), version 2 (from 27 Dec 2024).
        new StatutoryRuleVersion
        {
            Id = Id("allowable.v1"), RuleCode = KenyaRuleCodes.AllowableDeductions, Jurisdiction = "KE", Authority = "KRA",
            Name = "PAYE allowable deductions", Version = 1, Method = StatutoryCalculationMethod.Parameters,
            EffectiveFrom = new DateOnly(2023, 7, 1), EffectiveTo = new DateOnly(2024, 12, 26),
            ApprovalStatus = RuleApprovalStatus.Approved,
            LegalReference = "Income Tax Act s.22A/22B (KES 20,000 pension cap); s.15(3)(b) mortgage interest KES 25,000",
            Parameters = new StatutoryRuleParameters { PensionDeductionCap = 20_000m, PostRetirementMedicalCap = 0m, MortgageInterestCap = 25_000m },
        },
        new StatutoryRuleVersion
        {
            Id = Id("allowable.v2"), RuleCode = KenyaRuleCodes.AllowableDeductions, Jurisdiction = "KE", Authority = "KRA",
            Name = "PAYE allowable deductions", Version = 2, Method = StatutoryCalculationMethod.Parameters,
            EffectiveFrom = new DateOnly(2024, 12, 27), EffectiveTo = null,
            ApprovalStatus = RuleApprovalStatus.Approved,
            LegalReference = "Tax Laws (Amendment) Act 2024: pension cap KES 30,000; PRMF KES 15,000; mortgage interest KES 30,000",
            Parameters = new StatutoryRuleParameters { PensionDeductionCap = 30_000m, PostRetirementMedicalCap = 15_000m, MortgageInterestCap = 30_000m },
        },

        // ---------------- NSSF (Act No. 45 of 2013, phased limits) --------------------------
        new StatutoryRuleVersion
        {
            Id = Id("nssf.v2"), RuleCode = KenyaRuleCodes.Nssf, Jurisdiction = "KE", Authority = "NSSF", Name = "NSSF",
            Version = 2, Method = StatutoryCalculationMethod.TieredContribution, Base = StatutoryBase.PensionableEarnings,
            EffectiveFrom = new DateOnly(2024, 2, 1), EffectiveTo = new DateOnly(2025, 1, 31),
            EmployeeRate = 0.06m, EmployerRate = 0.06m,
            ApprovalStatus = RuleApprovalStatus.Approved,
            LegalReference = "NSSF Act 2013 Third Schedule, year 2: LEL 7,000 / UEL 36,000",
            Parameters = new StatutoryRuleParameters { LowerEarningsLimit = 7_000m, UpperEarningsLimit = 36_000m, CountsAsPensionContribution = true, DeductibleFromTaxablePay = true },
        },
        new StatutoryRuleVersion
        {
            Id = Id("nssf.v3"), RuleCode = KenyaRuleCodes.Nssf, Jurisdiction = "KE", Authority = "NSSF", Name = "NSSF",
            Version = 3, Method = StatutoryCalculationMethod.TieredContribution, Base = StatutoryBase.PensionableEarnings,
            EffectiveFrom = new DateOnly(2025, 2, 1), EffectiveTo = null,
            EmployeeRate = 0.06m, EmployerRate = 0.06m,
            ApprovalStatus = RuleApprovalStatus.Approved,
            LegalReference = "NSSF Act 2013 Third Schedule, year 3: LEL 8,000 / UEL 72,000 — verify year-4 limits before Feb 2026 payrolls",
            Parameters = new StatutoryRuleParameters { LowerEarningsLimit = 8_000m, UpperEarningsLimit = 72_000m, CountsAsPensionContribution = true, DeductibleFromTaxablePay = true },
        },

        // ---------------- SHIF (Social Health Insurance Act 2023) ---------------------------
        new StatutoryRuleVersion
        {
            Id = Id("shif.v1"), RuleCode = KenyaRuleCodes.Shif, Jurisdiction = "KE", Authority = "SHA", Name = "SHIF",
            Version = 1, Method = StatutoryCalculationMethod.Percentage, Base = StatutoryBase.GrossPay,
            EffectiveFrom = new DateOnly(2024, 10, 1), EffectiveTo = new DateOnly(2024, 12, 26),
            EmployeeRate = 0.0275m, EmployerRate = 0m, MinimumAmount = 300m,
            ApprovalStatus = RuleApprovalStatus.Approved,
            LegalReference = "Social Health Insurance (General) Regulations 2024: 2.75% of gross, minimum KES 300; 15% relief regime",
            Parameters = new StatutoryRuleParameters { DeductibleFromTaxablePay = false },
        },
        new StatutoryRuleVersion
        {
            Id = Id("shif.v2"), RuleCode = KenyaRuleCodes.Shif, Jurisdiction = "KE", Authority = "SHA", Name = "SHIF",
            Version = 2, Method = StatutoryCalculationMethod.Percentage, Base = StatutoryBase.GrossPay,
            EffectiveFrom = new DateOnly(2024, 12, 27), EffectiveTo = null,
            EmployeeRate = 0.0275m, EmployerRate = 0m, MinimumAmount = 300m,
            ApprovalStatus = RuleApprovalStatus.Approved,
            LegalReference = "Tax Laws (Amendment) Act 2024: SHIF contributions deductible from taxable income",
            Parameters = new StatutoryRuleParameters { DeductibleFromTaxablePay = true },
        },

        // ---------------- Affordable Housing Levy (Affordable Housing Act 2024) -------------
        new StatutoryRuleVersion
        {
            Id = Id("ahl.v1"), RuleCode = KenyaRuleCodes.HousingLevy, Jurisdiction = "KE", Authority = "KRA", Name = "Affordable Housing Levy",
            Version = 1, Method = StatutoryCalculationMethod.Percentage, Base = StatutoryBase.GrossPay,
            EffectiveFrom = new DateOnly(2024, 3, 19), EffectiveTo = new DateOnly(2024, 12, 26),
            EmployeeRate = 0.015m, EmployerRate = 0.015m,
            ApprovalStatus = RuleApprovalStatus.Approved,
            LegalReference = "Affordable Housing Act 2024 s.4: 1.5% employee + 1.5% employer on gross; 15% relief regime",
            Parameters = new StatutoryRuleParameters { DeductibleFromTaxablePay = false },
        },
        new StatutoryRuleVersion
        {
            Id = Id("ahl.v2"), RuleCode = KenyaRuleCodes.HousingLevy, Jurisdiction = "KE", Authority = "KRA", Name = "Affordable Housing Levy",
            Version = 2, Method = StatutoryCalculationMethod.Percentage, Base = StatutoryBase.GrossPay,
            EffectiveFrom = new DateOnly(2024, 12, 27), EffectiveTo = null,
            EmployeeRate = 0.015m, EmployerRate = 0.015m,
            ApprovalStatus = RuleApprovalStatus.Approved,
            LegalReference = "Tax Laws (Amendment) Act 2024: AHL contributions deductible from taxable income",
            Parameters = new StatutoryRuleParameters { DeductibleFromTaxablePay = true },
        },

        // ---------------- NITA industrial training levy -------------------------------------
        new StatutoryRuleVersion
        {
            Id = Id("nita.v1"), RuleCode = KenyaRuleCodes.Nita, Jurisdiction = "KE", Authority = "NITA", Name = "NITA levy",
            Version = 1, Method = StatutoryCalculationMethod.FixedAmount,
            EffectiveFrom = new DateOnly(2023, 1, 1), EffectiveTo = null,
            ApprovalStatus = RuleApprovalStatus.Approved,
            LegalReference = "Industrial Training (Training Levy) Order: KES 50 per employee per month, employer-paid",
            Parameters = new StatutoryRuleParameters { EmployeeFixedAmount = 0m, EmployerFixedAmount = 50m },
        },
    ];
}
