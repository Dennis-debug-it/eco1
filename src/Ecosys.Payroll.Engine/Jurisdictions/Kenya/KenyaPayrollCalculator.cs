using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Engine.Rules;

namespace Ecosys.Payroll.Engine.Jurisdictions.Kenya;

/// <summary>
/// Kenyan gross-to-net calculation. Every rate, threshold and cap comes from the
/// effective-dated <see cref="StatutoryRuleSet"/>; nothing statutory is hardcoded here.
/// <para>
/// Sequence (per PRD §4.1): gross → taxable earnings → allowable deductions (NSSF/pension within cap,
/// SHIF and AHL where the rule says they are deductible, PRMF, mortgage interest) → taxable pay →
/// PAYE bands → reliefs (personal, insurance) → other deductions → employer contributions → net pay.
/// </para>
/// </summary>
public sealed class KenyaPayrollCalculator : IPayrollCalculator
{
    public string Jurisdiction => KenyaRuleCodes.Jurisdiction;

    public EmployeePayrollResult Calculate(EmployeePayrollInput employee, PayrollPeriodInput period, StatutoryRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(rules);
        if (!string.Equals(rules.Jurisdiction, Jurisdiction, StringComparison.OrdinalIgnoreCase))
            throw new StatutoryConfigurationException($"Rule set for {rules.Jurisdiction} supplied to the {Jurisdiction} calculator.");

        var lines = new List<PayrollResultLine>();
        var issues = new List<PayrollValidationIssue>();
        var usedRules = new List<Guid>();
        var seq = 0;
        var money = RoundingRule.Default;

        // ---- 1. Earnings -------------------------------------------------------------------
        decimal basic = 0, gross = 0, taxableEarnings = 0, pensionable = 0;
        foreach (var e in employee.Earnings)
        {
            if (e.Amount < 0) issues.Add(new(ValidationSeverity.Error, "NEGATIVE_EARNING", $"Earning {e.Code} is negative."));
            var amt = money.Apply(e.Amount);
            gross += amt;
            if (e.Taxable) taxableEarnings += amt;
            if (e.Pensionable) pensionable += amt;
            if (e.IsBasic) basic += amt;
            lines.Add(new PayrollResultLine
            {
                Type = ResultLineType.Earning, Code = e.Code, Name = e.Name, Amount = amt,
                Taxable = e.Taxable, Pensionable = e.Pensionable, ComponentId = e.ComponentId,
                GlAccountCode = e.GlAccountCode, Sequence = ++seq,
            });
        }

        if (employee.UnpaidLeaveDeduction > 0)
        {
            var unpaid = money.Apply(employee.UnpaidLeaveDeduction);
            gross -= unpaid;
            taxableEarnings -= unpaid;
            pensionable = Math.Max(0, pensionable - unpaid);
            lines.Add(new PayrollResultLine
            {
                Type = ResultLineType.Earning, Code = "UNPAID_LEAVE", Name = "Unpaid leave", Amount = -unpaid,
                Taxable = true, Sequence = ++seq, Working = "Absence adjustment",
            });
        }

        if (basic == 0) issues.Add(new(ValidationSeverity.Warning, "NO_BASIC", "No basic salary component."));
        if (gross <= 0) issues.Add(new(ValidationSeverity.Error, "NO_GROSS", "Gross pay is zero or negative."));
        // NSSF pensionable earnings default to gross cash pay when no component is flagged pensionable.
        var nssfBase = pensionable > 0 ? pensionable : Math.Max(0, gross);

        // ---- 2. Statutory contributions on earnings (NSSF, SHIF, AHL, NITA) ------------------
        decimal allowableFromStatutory = 0;   // employee statutory amounts the rules say are tax-deductible
        decimal nssfEmployee = 0;
        decimal totalStatutory = 0, totalEmployer = 0;

        foreach (var code in new[] { KenyaRuleCodes.Nssf, KenyaRuleCodes.Shif, KenyaRuleCodes.HousingLevy, KenyaRuleCodes.Nita })
        {
            var rule = rules.Find(code);
            if (rule is null)
            {
                issues.Add(new(ValidationSeverity.Error, "RULE_MISSING", $"No approved {code} rule effective on {rules.AsOf:yyyy-MM-dd}."));
                continue;
            }
            usedRules.Add(rule.Id);

            if (!rule.AppliesToCategory(employee.Category) || employee.StatutoryExemptions.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add(Info(++seq, code, rule, $"{rule.Name} not applicable", 0m, "Exempt / category not covered"));
                continue;
            }

            var baseAmount = rule.Base switch
            {
                StatutoryBase.PensionableEarnings => nssfBase,
                StatutoryBase.TaxableEarnings => Math.Max(0, taxableEarnings),
                _ => Math.Max(0, gross),
            };
            var outcome = RuleEvaluator.Evaluate(rule, baseAmount);

            if (outcome.Employee != 0)
            {
                totalStatutory += outcome.Employee;
                lines.Add(new PayrollResultLine
                {
                    Type = ResultLineType.StatutoryDeduction, Code = code, Name = rule.Name, Amount = outcome.Employee,
                    Working = outcome.Working, StatutoryRuleVersionId = rule.Id, StatutoryRuleCode = code, Sequence = ++seq,
                });
                if (code == KenyaRuleCodes.Nssf) nssfEmployee = outcome.Employee;
                else if (rule.Parameters.DeductibleFromTaxablePay) allowableFromStatutory += outcome.Employee;
            }
            if (outcome.Employer != 0)
            {
                totalEmployer += outcome.Employer;
                lines.Add(new PayrollResultLine
                {
                    Type = ResultLineType.EmployerContribution, Code = code + ".ER", Name = rule.Name + " (employer)", Amount = outcome.Employer,
                    Working = outcome.Working, StatutoryRuleVersionId = rule.Id, StatutoryRuleCode = code, Sequence = ++seq,
                });
            }
        }

        // ---- 3. Allowable deductions → taxable pay -------------------------------------------
        var allowable = rules.Find(KenyaRuleCodes.AllowableDeductions);
        if (allowable is not null) usedRules.Add(allowable.Id);
        var pensionCap = allowable?.Parameters.PensionDeductionCap ?? decimal.MaxValue;
        var prmfCap = allowable?.Parameters.PostRetirementMedicalCap ?? 0m;
        var mortgageCap = allowable?.Parameters.MortgageInterestCap ?? 0m;

        var nssfRule = rules.Find(KenyaRuleCodes.Nssf);
        var pensionContribs = (nssfRule?.Parameters.CountsAsPensionContribution ?? true ? nssfEmployee : 0m)
                              + employee.Deductions.Where(d => d.Treatment == DeductionTreatment.RegisteredPension).Sum(d => money.Apply(d.Amount));
        var pensionAllowable = Math.Min(pensionContribs, pensionCap);
        var prmfAllowable = Math.Min(employee.Deductions.Where(d => d.Treatment == DeductionTreatment.PostRetirementMedicalFund).Sum(d => money.Apply(d.Amount)), prmfCap);
        var mortgageAllowable = Math.Min(employee.Deductions.Where(d => d.Treatment == DeductionTreatment.MortgageInterest).Sum(d => money.Apply(d.Amount)), mortgageCap);
        var disabilityExempt = employee.HasDisabilityExemption ? money.Apply(employee.DisabilityExemptAmount) : 0m;

        var totalAllowable = pensionAllowable + prmfAllowable + mortgageAllowable + allowableFromStatutory + disabilityExempt;
        var taxablePay = Math.Max(0, taxableEarnings - totalAllowable);

        if (pensionAllowable > 0) lines.Add(Info(++seq, "ALLOW.PENSION", allowable, "Allowable pension contributions", pensionAllowable, $"min({pensionContribs:N2}, cap {pensionCap:N2})"));
        if (allowableFromStatutory > 0) lines.Add(Info(++seq, "ALLOW.STATUTORY", allowable, "Allowable SHIF / Housing Levy", allowableFromStatutory, "Deductible statutory contributions"));
        if (prmfAllowable > 0) lines.Add(Info(++seq, "ALLOW.PRMF", allowable, "Allowable post-retirement medical fund", prmfAllowable, $"cap {prmfCap:N2}"));
        if (mortgageAllowable > 0) lines.Add(Info(++seq, "ALLOW.MORTGAGE", allowable, "Allowable mortgage interest", mortgageAllowable, $"cap {mortgageCap:N2}"));
        if (disabilityExempt > 0) lines.Add(Info(++seq, "ALLOW.DISABILITY", null, "Disability exemption", disabilityExempt, "KRA exemption certificate"));

        // ---- 4. PAYE ----------------------------------------------------------------------
        decimal grossTax = 0, reliefs = 0, paye = 0;
        var payeRule = rules.Find(KenyaRuleCodes.Paye);
        if (payeRule is null)
        {
            issues.Add(new(ValidationSeverity.Error, "RULE_MISSING", $"No approved {KenyaRuleCodes.Paye} rule effective on {rules.AsOf:yyyy-MM-dd}."));
        }
        else
        {
            usedRules.Add(payeRule.Id);
            grossTax = payeRule.Rounding.Apply(RuleEvaluator.ProgressiveTax(payeRule.Parameters.Bands, taxablePay, out var working));
            lines.Add(Info(++seq, "PAYE.GROSS", payeRule, "Tax on taxable pay", grossTax, working));

            var personalRelief = payeRule.Parameters.PersonalRelief ?? 0m;
            var premiums = employee.Deductions.Where(d => d.Treatment == DeductionTreatment.QualifyingInsurancePremium).Sum(d => money.Apply(d.Amount));
            var insuranceRelief = 0m;
            if (premiums > 0 && payeRule.Parameters.InsuranceReliefRate is { } irr)
            {
                insuranceRelief = payeRule.Rounding.Apply(premiums * irr);
                if (payeRule.Parameters.InsuranceReliefCap is { } cap) insuranceRelief = Math.Min(insuranceRelief, cap);
            }

            // Reliefs cannot reduce PAYE below zero; record what was actually used.
            var reliefAvailable = personalRelief + insuranceRelief;
            reliefs = Math.Min(reliefAvailable, grossTax);
            paye = payeRule.Rounding.Apply(Math.Max(0, grossTax - reliefAvailable));

            if (personalRelief > 0) lines.Add(new PayrollResultLine { Type = ResultLineType.TaxRelief, Code = "RELIEF.PERSONAL", Name = "Personal relief", Amount = Math.Min(personalRelief, grossTax), StatutoryRuleVersionId = payeRule.Id, StatutoryRuleCode = KenyaRuleCodes.Paye, Sequence = ++seq });
            if (insuranceRelief > 0) lines.Add(new PayrollResultLine { Type = ResultLineType.TaxRelief, Code = "RELIEF.INSURANCE", Name = "Insurance relief", Amount = Math.Min(insuranceRelief, Math.Max(0, grossTax - personalRelief)), Working = $"{RuleEvaluator.Pct(payeRule.Parameters.InsuranceReliefRate ?? 0)} x {premiums:N2}", StatutoryRuleVersionId = payeRule.Id, StatutoryRuleCode = KenyaRuleCodes.Paye, Sequence = ++seq });

            totalStatutory += paye;
            lines.Add(new PayrollResultLine
            {
                Type = ResultLineType.StatutoryDeduction, Code = KenyaRuleCodes.Paye, Name = payeRule.Name, Amount = paye,
                Working = $"{grossTax:N2} less reliefs {reliefs:N2}", StatutoryRuleVersionId = payeRule.Id, StatutoryRuleCode = KenyaRuleCodes.Paye, Sequence = ++seq,
            });
        }

        // ---- 5. Other deductions and employer contributions --------------------------------
        decimal otherDeductions = 0;
        foreach (var d in employee.Deductions)
        {
            if (d.Amount < 0) issues.Add(new(ValidationSeverity.Error, "NEGATIVE_DEDUCTION", $"Deduction {d.Code} is negative."));
            var amt = money.Apply(d.Amount);
            otherDeductions += amt;
            lines.Add(new PayrollResultLine
            {
                Type = ResultLineType.Deduction, Code = d.Code, Name = d.Name, Amount = amt,
                ComponentId = d.ComponentId, GlAccountCode = d.GlAccountCode, Sequence = ++seq,
                Working = d.Treatment == DeductionTreatment.Ordinary ? null : d.Treatment.ToString(),
            });
            if (d.EmployerAmount > 0)
            {
                var er = money.Apply(d.EmployerAmount);
                totalEmployer += er;
                lines.Add(new PayrollResultLine
                {
                    Type = ResultLineType.EmployerContribution, Code = d.Code + ".ER", Name = d.Name + " (employer)", Amount = er,
                    ComponentId = d.ComponentId, GlAccountCode = d.GlAccountCode, Sequence = ++seq,
                });
            }
        }

        // ---- 6. Net pay -------------------------------------------------------------------
        var totalEmployeeDeductions = totalStatutory + otherDeductions;
        var netPay = money.Apply(gross - totalEmployeeDeductions);
        if (netPay < 0) issues.Add(new(ValidationSeverity.Error, "NEGATIVE_NET", $"Net pay is negative ({netPay:N2})."));
        else if (gross > 0 && netPay < gross * 0.3m) issues.Add(new(ValidationSeverity.Warning, "LOW_NET", $"Net pay is below 30% of gross ({netPay:N2} of {gross:N2})."));

        return new EmployeePayrollResult
        {
            EmployeeId = employee.EmployeeId,
            EmployeeNumber = employee.EmployeeNumber,
            DisplayName = employee.DisplayName,
            BasicPay = basic,
            GrossPay = gross,
            TaxableEarnings = taxableEarnings,
            PensionableEarnings = nssfBase,
            TaxablePay = taxablePay,
            GrossTax = grossTax,
            TotalReliefs = reliefs,
            Paye = paye,
            TotalStatutoryDeductions = totalStatutory,
            TotalOtherDeductions = otherDeductions,
            TotalEmployeeDeductions = totalEmployeeDeductions,
            NetPay = netPay,
            TotalEmployerContributions = totalEmployer,
            TotalEmployerCost = gross + totalEmployer,
            Lines = lines,
            Issues = issues,
            StatutoryRuleVersionIds = usedRules.Distinct().ToList(),
        };
    }

    private static PayrollResultLine Info(int seq, string code, StatutoryRuleVersion? rule, string name, decimal amount, string working) => new()
    {
        Type = ResultLineType.Information, Code = code, Name = name, Amount = amount, Working = working,
        StatutoryRuleVersionId = rule?.Id, StatutoryRuleCode = rule?.RuleCode, Sequence = seq,
    };
}
