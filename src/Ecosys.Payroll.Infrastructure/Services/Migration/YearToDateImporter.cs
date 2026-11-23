using System.Globalization;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Migration;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Migration;

/// <summary>
/// Pay and tax for the months of this tax year that the old system paid, one row per employee per month, so P9 cards
/// and the annual summary cover the whole year after a mid-year switch. Figures are kept as given, never recalculated.
/// A month Ecosys has already finalised is refused, so nothing is counted twice.
/// </summary>
public sealed class YearToDateImporter(PayrollDbContext db)
{
    public static readonly IReadOnlyList<ImportColumn> Columns =
    [
        new("EmployeeNumber", "Staff number of an employee already in Ecosys", true, "E001", "EmployeeNo", "EmpNo", "Emp No", "StaffNo", "Staff Number", "PayrollNo", "EmployeeId", "EmpId", "EmployeeCode", "PF No"),
        new("Year", "Tax year (default this year)", false, "2026", "TaxYear", "PayYear"),
        new("Month", "Month paid: 1–12, Jan, January, or a date in the month", true, "3", "PayMonth", "Period", "PayPeriod", "MonthNo"),
        new("Gross", "Gross pay", true, "120000", "GrossPay", "GrossSalary", "TotalGross", "GrossEarnings", "GrossEmoluments"),
        new("TaxableEarnings", "Taxable earnings before allowable deductions (default taxable pay + allowable deductions)", false, null, "TaxableEmoluments", "TotalTaxableEarnings"),
        new("AllowableDeductions", "Pension, NSSF, SHIF and housing levy deducted before tax", false, null, "Allowable", "DeductibleAmount", "TaxDeductibleDeductions", "PensionAndNssf"),
        new("TaxablePay", "Chargeable (taxable) pay", true, "105000", "Taxable", "TaxableIncome", "ChargeablePay", "ChargeableIncome"),
        new("GrossTax", "Tax charged before reliefs (default PAYE + reliefs)", false, null, "TaxCharged", "TaxOnChargeable", "TaxBeforeRelief"),
        new("Reliefs", "Personal and insurance relief", false, "2400", "Relief", "PersonalRelief", "TotalReliefs", "TaxReliefs"),
        new("PAYE", "PAYE deducted", true, "24000", "Tax", "PayeTax", "IncomeTax", "PayeDeducted"),
        new("NSSF", "Employee NSSF", false, "4320", "NssfEmployee", "NSSFDeduction"),
        new("SHIF", "SHIF (SHA) deducted", false, "3300", "SHA", "ShifDeduction", "NHIF"),
        new("HousingLevy", "Housing levy, employee share", false, "1800", "AHL", "HousingLevyEmployee", "AffordableHousingLevy"),
        new("NetPay", "Net pay", false, null, "Net", "NetSalary", "TakeHome", "AmountPaid"),
    ];

    public sealed class Plan
    {
        public List<PayrollHistoryMonth> Months { get; } = [];
        public Dictionary<string, int> Summary { get; } = [];
        public List<string> Notes { get; } = [];
    }

    private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames;
    private static readonly string[] MonthAbbrev = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames;

    public static int? ParseMonth(string s)
    {
        s = s.Trim();
        if (int.TryParse(s, out var n) && n is >= 1 and <= 12) return n;
        var i = Array.FindIndex(MonthNames, m => m.Length > 0 && m.Equals(s, StringComparison.OrdinalIgnoreCase));
        if (i < 0) i = Array.FindIndex(MonthAbbrev, m => m.Length > 0 && s.StartsWith(m, StringComparison.OrdinalIgnoreCase) && s.Length <= 4);
        if (i >= 0) return i + 1;
        return ImportValues.TryDate(s, out var d) ? d.Month : DateOnly.TryParseExact(s, ["yyyy-MM", "MM/yyyy", "M/yyyy", "MMM yyyy", "MMMM yyyy", "MMM-yy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var m2) ? m2.Month : null;
    }

    public async Task<Plan> PlanAsync(Guid legalEntityId, Sheet sheet, List<ImportProblem> problems, CancellationToken ct)
    {
        var plan = new Plan();
        var map = ColumnMap.Build(sheet, Columns, problems);
        foreach (var c in Columns.Where(c => c.Required && !map.Has(c.Name)))
            problems.Add(new ImportProblem(1, c.Name, $"The file needs a {c.Name} column ({c.Description})."));
        if (problems.Count > 0) return plan;
        if (map.Unrecognised.Count > 0) plan.Notes.Add($"Not imported (columns this import doesn't use): {string.Join(", ", map.Unrecognised)}.");

        var employees = await db.Employees.Where(e => e.LegalEntityId == legalEntityId).ToDictionaryAsync(e => e.EmployeeNumber, StringComparer.OrdinalIgnoreCase, ct);
        var today = BusinessClock.Today();
        // Months Ecosys has finalised for this company: their figures already count, so importing them would double them.
        var finalised = (await db.PayrollRuns.Where(r => r.LegalEntityId == legalEntityId && r.Status == PayrollRunStatus.Finalised)
            .Join(db.PayrollPeriods, r => r.PayrollPeriodId, p => p.Id, (r, p) => p.PeriodEnd).ToListAsync(ct)).Select(d => (d.Year, d.Month)).ToHashSet();
        var existing = (await db.PayrollHistoryMonths.Where(h => h.LegalEntityId == legalEntityId).Select(h => new { h.EmployeeId, h.Year, h.Month }).ToListAsync(ct))
            .Select(h => (h.EmployeeId, h.Year, h.Month)).ToHashSet();
        var seen = new Dictionary<(Guid, int, int), int>();

        foreach (var sheetRow in sheet.Rows)
        {
            var r = new RowReader(sheetRow, map, problems);
            var number = r.Text("EmployeeNumber", required: true);
            var year = r.Int("Year", 2000, today.Year) ?? today.Year;
            var monthCell = r.Text("Month", required: true);
            int? month = monthCell is null ? null : ParseMonth(monthCell);
            if (monthCell is not null && month is null) r.Problem("Month", $"Month '{monthCell}' isn't a month. Use 1–12, Jan or January.");
            decimal? gross = r.Money("Gross", required: true), taxable = r.Money("TaxablePay", required: true), paye = r.Money("PAYE", required: true);
            decimal? earnings = r.Money("TaxableEarnings"), allowable = r.Money("AllowableDeductions"), grossTax = r.Money("GrossTax"), reliefs = r.Money("Reliefs");
            decimal? nssf = r.Money("NSSF"), shif = r.Money("SHIF"), levy = r.Money("HousingLevy"), net = r.Money("NetPay");
            if (number is null || month is null) continue;
            if (!employees.TryGetValue(number, out var emp)) { r.Problem("EmployeeNumber", $"Employee {number} isn't in this company. Load employees first."); continue; }
            if (year > today.Year || (year == today.Year && month > today.Month)) r.Problem("Month", $"{MonthNames[month.Value - 1]} {year} hasn't happened yet.");
            if (finalised.Contains((year, month.Value))) r.Problem("Month", $"Ecosys has already finalised {MonthNames[month.Value - 1]} {year} for this company, so its figures already count.");
            if (existing.Contains((emp.Id, year, month.Value))) r.Problem("Month", $"{MonthNames[month.Value - 1]} {year} for {number} was already imported. Undo that import first to replace it.");
            else if (seen.TryGetValue((emp.Id, year, month.Value), out var other)) r.Problem("Month", $"{MonthNames[month.Value - 1]} {year} for {number} appears twice in the file (also row {other}).");
            else seen[(emp.Id, year, month.Value)] = r.Number;
            if (gross is { } g && taxable is { } t && t > g) r.Problem("TaxablePay", "Taxable pay is more than gross pay.");
            if (r.HasProblems || gross is null || taxable is null || paye is null) continue;

            var allow = allowable ?? (earnings is { } e2 ? Math.Max(0, e2 - taxable.Value) : 0m);
            plan.Months.Add(new PayrollHistoryMonth
            {
                LegalEntityId = legalEntityId, EmployeeId = emp.Id, Year = year, Month = month.Value, Gross = gross.Value,
                TaxableEarnings = earnings ?? taxable.Value + allow, AllowableDeductions = allow, TaxablePay = taxable.Value,
                Reliefs = reliefs ?? 0m, GrossTax = grossTax ?? paye.Value + (reliefs ?? 0m), Paye = paye.Value,
                Nssf = nssf ?? 0m, Shif = shif ?? 0m, HousingLevy = levy ?? 0m, NetPay = net ?? 0m,
            });
            if (grossTax is { } gt && Math.Abs(gt - (reliefs ?? 0m) - paye.Value) > 1m)
                plan.Notes.Add($"Row {r.Number}: tax charged {gt:N2} less reliefs {reliefs ?? 0m:N2} isn't the PAYE {paye:N2}. Imported as given; check it against the old P9.");
        }

        plan.Summary["historyMonths"] = plan.Months.Count;
        plan.Summary["employeesWithHistory"] = plan.Months.Select(m => m.EmployeeId).Distinct().Count();
        var months = plan.Months.Select(m => (m.Year, m.Month)).Distinct().OrderBy(x => x).ToList();
        if (months.Count > 0) plan.Notes.Add($"Months covered: {string.Join(", ", months.Select(m => $"{MonthAbbrev[m.Month - 1]} {m.Year}"))}.");
        return plan;
    }

    public void Apply(Plan plan, ImportBatch batch)
    {
        foreach (var m in plan.Months)
        {
            m.ImportBatchId = batch.Id;
            db.PayrollHistoryMonths.Add(m);
            batch.Records.HistoryMonths.Add(m.Id);
        }
    }
}
