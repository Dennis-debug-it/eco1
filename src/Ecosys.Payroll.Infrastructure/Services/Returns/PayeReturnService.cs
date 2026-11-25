using System.Globalization;
using System.Text.RegularExpressions;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Engine.Jurisdictions.Kenya;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services.Migration;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Returns;

public sealed record PayeReturnCheck(string Company, string Period, int Runs, int Employees, decimal TaxablePay, decimal Paye,
    bool Ready, IReadOnlyList<ReturnProblem> Problems, IReadOnlyList<string> Warnings);

public sealed record PayeReturnFile(byte[] Content, string FileName, PayeReturnCheck Check);

/// <summary>
/// KRA's monthly PAYE return (P10): one row per employee for a company and month, adding together every approved run whose
/// period ends in that month (weekly and twice-monthly payrolls, supplementary and correction runs). Pay is split into the
/// P10's columns by each component's P10 category; deductions, reliefs and tax come from the run's own calculation lines,
/// so the return can only say what the payroll did. Every employee is reconciled before a file is made.
/// </summary>
public sealed partial class PayeReturnService(PayrollDbContext db, ITenantContext tenant, AuditService audit, MigrationGuard migration)
{
    public sealed class Row
    {
        public string Pin = "", Name = "", EmployeeNumber = "";
        public decimal Basic, Housing, Transport, LeavePay, Overtime, DirectorsFee, LumpSum, OtherAllowance, CarBenefit, OtherNonCash;
        public decimal TaxableEarnings, ActualPension, PermissibleLimit, Mortgage, AmountOfBenefit, Shif, HousingLevy, Prmf, Disability;
        public decimal TaxablePay, TaxPayable, PersonalRelief, InsuranceRelief, Paye;
        public decimal CashPay => Basic + Housing + Transport + LeavePay + Overtime + DirectorsFee + LumpSum + OtherAllowance;
        public decimal NonCash => CarBenefit + OtherNonCash;
        public decimal GrossPay => CashPay + NonCash;
    }

    /// <summary>
    /// The P10 employee sheet's columns, in order. Kept as data so a change to KRA's template is an edit here.
    /// The housing-benefit block is filled for "benefit not given": housing benefits aren't modelled yet.
    /// </summary>
    public static readonly IReadOnlyList<(string Header, Func<Row, XlsxCell> Value)> Columns =
    [
        ("PIN of Employee", r => XlsxCell.T(r.Pin)),
        ("Name of Employee", r => XlsxCell.T(r.Name)),
        ("Residential Status", _ => XlsxCell.T("Resident")),
        ("Type of Employee", _ => XlsxCell.T("Primary Employee")),
        ("Basic Salary", r => XlsxCell.N(r.Basic)),
        ("Housing Allowance", r => XlsxCell.N(r.Housing)),
        ("Transport Allowance", r => XlsxCell.N(r.Transport)),
        ("Leave Pay", r => XlsxCell.N(r.LeavePay)),
        ("Over Time Allowance", r => XlsxCell.N(r.Overtime)),
        ("Directors Fee", r => XlsxCell.N(r.DirectorsFee)),
        ("Lump Sum Payment if any", r => XlsxCell.N(r.LumpSum)),
        ("Other Allowance", r => XlsxCell.N(r.OtherAllowance)),
        ("Total Cash Pay (A)", r => XlsxCell.N(r.CashPay)),
        ("Value of Car Benefit", r => XlsxCell.N(r.CarBenefit)),
        ("Other Non Cash Benefits", r => XlsxCell.N(r.OtherNonCash)),
        ("Total Non Cash Pay", r => XlsxCell.N(r.NonCash)),
        ("Global Income (In case of Non Resident Director)", _ => XlsxCell.N(0m)),
        ("Type of Housing", _ => XlsxCell.T("Benefit not given")),
        ("Rent of House/Market Value", _ => XlsxCell.N(0m)),
        ("Computed Rent of House", _ => XlsxCell.N(0m)),
        ("Rent Recovered from Employee", _ => XlsxCell.N(0m)),
        ("Net Value of Housing", _ => XlsxCell.N(0m)),
        ("Total Gross Pay", r => XlsxCell.N(r.GrossPay)),
        ("30% of Cash Pay", r => XlsxCell.N(Math.Round(r.CashPay * 0.3m, 2))),
        ("Actual Contribution", r => XlsxCell.N(r.ActualPension)),
        ("Permissible Limit", r => XlsxCell.N(r.PermissibleLimit)),
        ("Mortgage Interest", r => XlsxCell.N(r.Mortgage)),
        ("Deposit on Home Ownership Saving Plan", _ => XlsxCell.N(0m)),
        ("Amount of Benefit", r => XlsxCell.N(r.AmountOfBenefit)),
        ("Social Health Insurance Fund (SHIF)", r => XlsxCell.N(r.Shif)),
        ("Affordable Housing Levy (AHL)", r => XlsxCell.N(r.HousingLevy)),
        ("Post Retirement Medical Fund (PRMF)", r => XlsxCell.N(r.Prmf)),
        ("Taxable Pay", r => XlsxCell.N(r.TaxablePay)),
        ("Tax Payable", r => XlsxCell.N(r.TaxPayable)),
        ("Monthly Personal Relief", r => XlsxCell.N(r.PersonalRelief)),
        ("Amount of Insurance Relief", r => XlsxCell.N(r.InsuranceRelief)),
        ("PAYE Tax", r => XlsxCell.N(r.Paye)),
        ("Self Assessed PAYE Tax", r => XlsxCell.N(r.Paye)),
    ];

    public static PayeReturnCategory CategoryOf(string code, SalaryComponent? c)
    {
        var chosen = c?.PayeReturnCategory ?? PayeReturnCategory.Auto;
        if (chosen != PayeReturnCategory.Auto) return chosen;
        if (c?.IsBasic == true) return PayeReturnCategory.BasicSalary;
        return code switch
        {
            "BASIC" or "UNPAID_LEAVE" => PayeReturnCategory.BasicSalary, // unpaid days reduce basic pay
            "HOUSE" => PayeReturnCategory.HousingAllowance,
            "TRANSPORT" => PayeReturnCategory.TransportAllowance,
            "LEAVE_PAY" => PayeReturnCategory.LeavePay,
            "OT" => PayeReturnCategory.Overtime,
            "NOTICE_PAY" or "SEVERANCE" => PayeReturnCategory.LumpSum,
            _ => PayeReturnCategory.OtherAllowance,
        };
    }

    public async Task<PayeReturnCheck> CheckAsync(Guid legalEntityId, int year, int month, CancellationToken ct = default) =>
        (await BuildAsync(legalEntityId, year, month, ct)).Check;

    public async Task<PayeReturnFile> ExportAsync(Guid legalEntityId, int year, int month, CancellationToken ct = default)
    {
        await migration.EnsureLiveAsync("export a PAYE return for filing", ct);
        var (check, rows, pin) = await BuildAsync(legalEntityId, year, month, ct);
        if (!check.Ready) throw new PayeReturnNotReadyException(check);
        var sheet = new List<IReadOnlyList<XlsxCell>> { Columns.Select(c => XlsxCell.T(c.Header)).ToList() };
        sheet.AddRange(rows.Select(r => (IReadOnlyList<XlsxCell>)Columns.Select(c => c.Value(r)).ToList()));
        audit.Record("report.p10.exported", "LegalEntity", legalEntityId, new { period = check.Period, check.Employees, check.Runs, check.Paye }, legalEntityId);
        await db.SaveChangesAsync(ct);
        return new PayeReturnFile(PlainXlsx.Write(sheet), $"P10_{pin}_{year:0000}{month:00}.xlsx", check);
    }

    private async Task<(PayeReturnCheck Check, List<Row> Rows, string EmployerPin)> BuildAsync(Guid legalEntityId, int year, int month, CancellationToken ct)
    {
        tenant.Require(Permissions.ReportsRead);
        tenant.Require(Permissions.SalaryRead); // every row is an employee's pay
        tenant.RequireLegalEntity(legalEntityId);
        if (month is < 1 or > 12 || year is < 2000 or > 2100) throw new DomainException("Choose a month and year.", "VALIDATION");
        var entity = await db.LegalEntities.FirstOrDefaultAsync(e => e.Id == legalEntityId, ct) ?? throw new NotFoundException("LegalEntity", legalEntityId);
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var periodLabel = first.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        var problems = new List<ReturnProblem>();
        var warnings = new List<string>();

        // Approved and later, never reversed: the figures that were (or will be) paid. A correction run counts in its own month.
        var runs = await db.PayrollRuns.Where(r => r.LegalEntityId == legalEntityId && r.ApprovedAt != null && r.Status != PayrollRunStatus.Reversed)
            .Join(db.PayrollPeriods.Where(p => p.PeriodEnd >= first && p.PeriodEnd <= last), r => r.PayrollPeriodId, p => p.Id, (r, p) => r.Id).ToListAsync(ct);
        var pending = await db.PayrollRuns.Where(r => r.LegalEntityId == legalEntityId && r.ApprovedAt == null && r.Status != PayrollRunStatus.Reversed)
            .Join(db.PayrollPeriods.Where(p => p.PeriodEnd >= first && p.PeriodEnd <= last), r => r.PayrollPeriodId, p => p.Id, (r, p) => r.Reference).ToListAsync(ct);
        if (runs.Count == 0) problems.Add(new("", "", $"No approved payroll for {entity.Name} in {periodLabel}."));
        if (pending.Count > 0) warnings.Add($"Not yet approved, so not in this return: {string.Join(", ", pending)}. Approve them first if they belong to {periodLabel}.");

        var results = await db.PayrollRunEmployees.Include(e => e.Lines).Where(e => runs.Contains(e.PayrollRunId)).AsNoTracking().ToListAsync(ct);
        var components = await db.SalaryComponents.Where(c => c.LegalEntityId == legalEntityId).AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var byCode = components.Values.GroupBy(c => c.Code).ToDictionary(g => g.Key, g => g.First());
        var ids = results.Select(r => r.EmployeeId).Distinct().ToList();
        var pins = await db.EmployeeStatutoryDetails.Where(s => ids.Contains(s.EmployeeId)).ToDictionaryAsync(s => s.EmployeeId, s => s.KraPin, ct);
        var cap = await PensionCapAsync(last, ct);

        var rows = new List<Row>();
        var otherAllowanceFrom = new SortedSet<string>();
        foreach (var g in results.GroupBy(r => r.EmployeeId).OrderBy(g => g.First().EmployeeNumber))
        {
            var latest = g.Last();
            var row = new Row { EmployeeNumber = latest.EmployeeNumber, Name = latest.EmployeeName.ToUpperInvariant(), Pin = (pins.GetValueOrDefault(g.Key) ?? "").Trim().ToUpperInvariant(), PermissibleLimit = cap };
            void Problem(string m) => problems.Add(new(row.EmployeeNumber, latest.EmployeeName, m));
            foreach (var e in g)
            {
                foreach (var l in e.Lines)
                {
                    var component = l.ComponentId is { } cid ? components.GetValueOrDefault(cid) : byCode.GetValueOrDefault(l.Code);
                    switch (l.Type)
                    {
                        case ResultLineType.Earning when l.Taxable:
                            var cat = CategoryOf(l.Code, component);
                            if (cat == PayeReturnCategory.OtherAllowance && l.Code is not ("BASIC" or "UNPAID_LEAVE")) otherAllowanceFrom.Add(component?.Name ?? l.Name);
                            Add(row, cat, l.Amount);
                            break;
                        case ResultLineType.StatutoryDeduction when l.Code == KenyaRuleCodes.Nssf: row.ActualPension += l.Amount; break;
                        case ResultLineType.StatutoryDeduction when l.Code == KenyaRuleCodes.Shif: row.Shif += l.Amount; break;
                        case ResultLineType.StatutoryDeduction when l.Code == KenyaRuleCodes.HousingLevy: row.HousingLevy += l.Amount; break;
                        case ResultLineType.Deduction when component?.DeductionTreatment == DeductionTreatment.RegisteredPension: row.ActualPension += l.Amount; break;
                        case ResultLineType.TaxRelief when l.Code == "RELIEF.PERSONAL": row.PersonalRelief += l.Amount; break;
                        case ResultLineType.TaxRelief when l.Code == "RELIEF.INSURANCE": row.InsuranceRelief += l.Amount; break;
                    }
                    switch (l.Code)
                    {
                        case "ALLOW.PENSION": row.AmountOfBenefit += l.Amount; break;
                        case "ALLOW.MORTGAGE": row.Mortgage += l.Amount; break;
                        case "ALLOW.PRMF": row.Prmf += l.Amount; break;
                        case "ALLOW.DISABILITY": row.Disability += l.Amount; break;
                    }
                }
                row.TaxableEarnings += e.TaxableEarnings; row.TaxablePay += e.TaxablePay; row.TaxPayable += e.GrossTax; row.Paye += e.Paye;
            }

            if (row.Pin.Length == 0) Problem("No KRA PIN. Add it on the employee's statutory details.");
            else if (!PinRx().IsMatch(row.Pin)) Problem($"KRA PIN '{row.Pin}' should be A, nine digits and a letter (e.g. A012345678Z).");
            if (Math.Abs(row.GrossPay - row.TaxableEarnings) > 0.01m)
                Problem($"Pay columns add up to {row.GrossPay:N2} but taxable earnings were {row.TaxableEarnings:N2}. Recalculate the run before filing.");
            if (Math.Abs(row.TaxPayable - row.PersonalRelief - row.InsuranceRelief - row.Paye) > 0.01m && row.Paye > 0)
                Problem($"Tax {row.TaxPayable:N2} less reliefs {row.PersonalRelief + row.InsuranceRelief:N2} isn't the PAYE deducted ({row.Paye:N2}).");
            rows.Add(row);
        }

        if (otherAllowanceFrom.Count > 0)
            warnings.Add($"Reported as Other Allowance: {string.Join(", ", otherAllowanceFrom)}. If any belongs in another P10 column (director's fee, car benefit…), set its P10 column under Compensation.");
        var disabled = rows.Count(r => r.Disability > 0);
        if (disabled > 0) warnings.Add($"{disabled} employee(s) have a disability exemption. KRA's return lists them with their exemption certificate; check the template's disability section.");
        warnings.Add("Everyone is reported as a resident with primary employment, which is how Ecosys calculates their tax.");

        var check = new PayeReturnCheck(entity.Name, periodLabel, runs.Count, rows.Count, rows.Sum(r => r.TaxablePay), rows.Sum(r => r.Paye), problems.Count == 0, problems, warnings);
        return (check, rows, new string(entity.TaxPin.Where(char.IsLetterOrDigit).ToArray()));
    }

    private static void Add(Row r, PayeReturnCategory cat, decimal amount)
    {
        switch (cat)
        {
            case PayeReturnCategory.BasicSalary: r.Basic += amount; break;
            case PayeReturnCategory.HousingAllowance: r.Housing += amount; break;
            case PayeReturnCategory.TransportAllowance: r.Transport += amount; break;
            case PayeReturnCategory.LeavePay: r.LeavePay += amount; break;
            case PayeReturnCategory.Overtime: r.Overtime += amount; break;
            case PayeReturnCategory.DirectorsFee: r.DirectorsFee += amount; break;
            case PayeReturnCategory.LumpSum: r.LumpSum += amount; break;
            case PayeReturnCategory.CarBenefit: r.CarBenefit += amount; break;
            case PayeReturnCategory.OtherNonCashBenefit: r.OtherNonCash += amount; break;
            default: r.OtherAllowance += amount; break;
        }
    }

    /// <summary>The monthly cap on allowable pension contributions, from the rule in force at month end.</summary>
    private async Task<decimal> PensionCapAsync(DateOnly asOf, CancellationToken ct)
    {
        var rules = await db.StatutoryRuleVersions.IgnoreQueryFilters()
            .Where(r => r.RuleCode == KenyaRuleCodes.AllowableDeductions && (r.TenantId == null || r.TenantId == tenant.TenantId) && !r.IsDeleted
                        && r.EffectiveFrom <= asOf && (r.EffectiveTo == null || r.EffectiveTo >= asOf))
            .ToListAsync(ct);
        var rule = rules.Where(r => r.ApprovalStatus == Engine.Rules.RuleApprovalStatus.Approved).OrderByDescending(r => r.TenantId != null).ThenByDescending(r => r.EffectiveFrom).FirstOrDefault()?.ToEngine();
        return rule?.Parameters.PensionDeductionCap ?? 0m;
    }

    [GeneratedRegex("^A[0-9]{9}[A-Z]$")] private static partial Regex PinRx();
}

public sealed class PayeReturnNotReadyException(PayeReturnCheck check) : DomainException("The return has problems to fix first.", "RETURN_NOT_READY")
{
    public PayeReturnCheck Check { get; } = check;
}
