using System.Globalization;
using System.Text;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Engine.Jurisdictions.Kenya;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services;

public sealed record DashboardSummary(
    int TotalEmployees, int ActiveEmployees, decimal CurrentGross, decimal CurrentNet, decimal StatutoryLiabilities, decimal EmployerContributions,
    IReadOnlyList<RunStatusCount> RunsByStatus, IReadOnlyList<TrendPoint> Trend, IReadOnlyList<CostBucket> ByDepartment, IReadOnlyList<CostBucket> ByBranch,
    IReadOnlyList<PendingItem> PendingApprovals, int OpenExceptions);
public sealed record RunStatusCount(string Status, int Count);
public sealed record TrendPoint(string Period, decimal Gross, decimal Net, decimal EmployerCost);
public sealed record CostBucket(string Name, int Employees, decimal Gross, decimal Net, decimal EmployerCost);
public sealed record PendingItem(Guid RunId, string Reference, string Status, int Employees, decimal Net, DateTimeOffset? SubmittedAt);

public sealed record StatutoryReportRow(string EmployeeNumber, string EmployeeName, string? Pin, string? MemberNumber, decimal Gross, decimal Base, decimal Employee, decimal Employer, decimal Total);
public sealed record StatutoryReport(string RuleCode, string Title, string RunReference, string Period, IReadOnlyList<StatutoryReportRow> Rows, decimal TotalEmployee, decimal TotalEmployer);

public sealed record AnnualMonth(int Month, string Period, decimal Gross, decimal TaxableEarnings, decimal AllowableDeductions, decimal TaxablePay, decimal GrossTax, decimal Reliefs, decimal Paye, decimal Nssf, decimal Shif, decimal HousingLevy, decimal NetPay);
public sealed record AnnualEmployee(Guid EmployeeId, string EmployeeNumber, string EmployeeName, string? KraPin, IReadOnlyList<AnnualMonth> Months, AnnualMonth Totals);
public sealed record AnnualReport(int Year, string LegalEntity, string EmployerPin, IReadOnlyList<AnnualEmployee> Employees);

public sealed class ReportingService(PayrollDbContext db, ITenantContext tenant)
{
    /// <summary>
    /// Annual payroll summary per employee (P9-style tax deduction card), built from finalised regular,
    /// supplementary and correction runs whose period ends in the year. Reversed runs are excluded.
    /// </summary>
    public async Task<AnnualReport> AnnualAsync(Guid legalEntityId, int year, Guid? employeeId, CancellationToken ct = default)
    {
        var self = employeeId is { } eid && tenant.EmployeeId == eid;
        if (!self) { tenant.Require(Domain.Identity.Permissions.ReportsRead); tenant.Require(Domain.Identity.Permissions.SalaryRead); }
        tenant.RequireLegalEntity(legalEntityId);
        var entity = await db.LegalEntities.FirstOrDefaultAsync(e => e.Id == legalEntityId, ct) ?? throw new NotFoundException("LegalEntity", legalEntityId);
        var start = new DateOnly(year, 1, 1); var end = new DateOnly(year, 12, 31);
        var runs = await db.PayrollRuns.Where(r => r.LegalEntityId == legalEntityId && r.Status == PayrollRunStatus.Finalised)
            .Join(db.PayrollPeriods, r => r.PayrollPeriodId, p => p.Id, (r, p) => new { r.Id, p.PeriodEnd })
            .Where(x => x.PeriodEnd >= start && x.PeriodEnd <= end).ToListAsync(ct);
        var runMonth = runs.ToDictionary(r => r.Id, r => r.PeriodEnd.Month);
        var runIds = runMonth.Keys.ToList();
        var q = db.PayrollRunEmployees.Where(e => runIds.Contains(e.PayrollRunId));
        if (employeeId is { } only) q = q.Where(e => e.EmployeeId == only);
        var rows = await q.ToListAsync(ct);
        var ids = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var pins = await db.EmployeeStatutoryDetails.Where(s => ids.Contains(s.EmployeeId)).ToDictionaryAsync(s => s.EmployeeId, s => s.KraPin, ct);

        static AnnualMonth Sum(int month, string label, IEnumerable<PayrollRunEmployee> xs)
        {
            var l = xs.ToList();
            return new AnnualMonth(month, label, l.Sum(x => x.GrossPay), l.Sum(x => x.TaxableEarnings), l.Sum(x => x.TaxableEarnings - x.TaxablePay), l.Sum(x => x.TaxablePay),
                l.Sum(x => x.GrossTax), l.Sum(x => x.TotalReliefs), l.Sum(x => x.Paye), l.Sum(x => x.Nssf), l.Sum(x => x.Shif), l.Sum(x => x.HousingLevy), l.Sum(x => x.NetPay));
        }
        var employees = rows.GroupBy(r => r.EmployeeId).Select(g =>
        {
            var first = g.OrderByDescending(x => runMonth[x.PayrollRunId]).First();
            var months = Enumerable.Range(1, 12).Select(m => Sum(m, new DateOnly(year, m, 1).ToString("MMM yyyy"), g.Where(x => runMonth[x.PayrollRunId] == m))).ToList();
            return new AnnualEmployee(g.Key, first.EmployeeNumber, first.EmployeeName, pins.GetValueOrDefault(g.Key), months, Sum(0, "Total", g));
        }).OrderBy(e => e.EmployeeNumber).ToList();
        return new AnnualReport(year, entity.Name, entity.TaxPin, employees);
    }

    public async Task<DashboardSummary> DashboardAsync(Guid? legalEntityId, CancellationToken ct = default)
    {
        tenant.Require(Permissions.ReportsRead);
        var emps = db.Employees.AsQueryable();
        var runs = db.PayrollRuns.AsQueryable();
        if (legalEntityId is { } le) { emps = emps.Where(e => e.LegalEntityId == le); runs = runs.Where(r => r.LegalEntityId == le); }

        var total = await emps.CountAsync(ct);
        var active = await emps.CountAsync(e => e.Status == Domain.Employees.EmploymentStatus.Active, ct);

        var latest = await runs.Where(r => r.Status == PayrollRunStatus.Finalised || r.Status == PayrollRunStatus.Approved || r.Status == PayrollRunStatus.Paid || r.Status == PayrollRunStatus.PaymentProcessing)
            .OrderByDescending(r => r.ApprovedAt).FirstOrDefaultAsync(ct);

        var byStatus = await runs.GroupBy(r => r.Status).Select(g => new RunStatusCount(g.Key.ToString(), g.Count())).ToListAsync(ct);

        var trendRuns = await runs.Where(r => r.Status == PayrollRunStatus.Finalised && r.RunType == PayrollRunType.Regular)
            .Join(db.PayrollPeriods, r => r.PayrollPeriodId, p => p.Id, (r, p) => new { p.PeriodEnd, r.TotalGross, r.TotalNet, r.TotalEmployerCost })
            .OrderByDescending(x => x.PeriodEnd).Take(60).ToListAsync(ct);
        var trend = trendRuns.GroupBy(x => x.PeriodEnd.ToString("yyyy-MM")).OrderBy(g => g.Key).TakeLast(12)
            .Select(g => new TrendPoint(g.Key, g.Sum(x => x.TotalGross), g.Sum(x => x.TotalNet), g.Sum(x => x.TotalEmployerCost))).ToList();

        var byDept = new List<CostBucket>();
        var byBranch = new List<CostBucket>();
        if (latest is not null)
        {
            var rows = await db.PayrollRunEmployees.Where(e => e.PayrollRunId == latest.Id).ToListAsync(ct);
            var depts = await db.Departments.Where(d => d.LegalEntityId == latest.LegalEntityId).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
            var branches = await db.Branches.Where(d => d.LegalEntityId == latest.LegalEntityId).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
            byDept = rows.GroupBy(r => r.DepartmentId is { } d && depts.TryGetValue(d, out var n) ? n : "Unassigned")
                .Select(g => new CostBucket(g.Key, g.Count(), g.Sum(x => x.GrossPay), g.Sum(x => x.NetPay), g.Sum(x => x.TotalEmployerCost))).OrderByDescending(b => b.EmployerCost).ToList();
            byBranch = rows.GroupBy(r => r.BranchId is { } d && branches.TryGetValue(d, out var n) ? n : "Unassigned")
                .Select(g => new CostBucket(g.Key, g.Count(), g.Sum(x => x.GrossPay), g.Sum(x => x.NetPay), g.Sum(x => x.TotalEmployerCost))).OrderByDescending(b => b.EmployerCost).ToList();
        }

        var pending = await runs.Where(r => r.Status == PayrollRunStatus.UnderReview || r.Status == PayrollRunStatus.Calculated)
            .OrderBy(r => r.SubmittedAt).Take(10)
            .Select(r => new PendingItem(r.Id, r.Reference, r.Status.ToString(), r.EmployeeCount, r.TotalNet, r.SubmittedAt)).ToListAsync(ct);
        var exceptions = await runs.Where(r => r.Status != PayrollRunStatus.Finalised && r.Status != PayrollRunStatus.Reversed).SumAsync(r => r.ErrorCount + r.WarningCount, ct);

        var liabilities = latest is null ? 0 : latest.TotalPaye + latest.TotalNssfEmployee + latest.TotalNssfEmployer + latest.TotalShif + latest.TotalHousingLevyEmployee + latest.TotalHousingLevyEmployer + latest.TotalNita;
        return new DashboardSummary(total, active, latest?.TotalGross ?? 0, latest?.TotalNet ?? 0, liabilities, latest?.TotalEmployerContributions ?? 0,
            byStatus, trend, byDept, byBranch, pending, exceptions);
    }

    public async Task<StatutoryReport> StatutoryAsync(Guid runId, string ruleCode, CancellationToken ct = default)
    {
        tenant.Require(Permissions.ReportsRead);
        var run = await db.PayrollRuns.Include(r => r.Employees).FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new NotFoundException("PayrollRun", runId);
        tenant.RequireLegalEntity(run.LegalEntityId);
        var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PayrollPeriodId, ct);
        var ids = run.Employees.Select(e => e.EmployeeId).ToList();
        var stat = await db.EmployeeStatutoryDetails.Where(s => ids.Contains(s.EmployeeId)).ToDictionaryAsync(s => s.EmployeeId, ct);
        var lineIds = run.Employees.Select(e => e.Id).ToList();
        var lines = await db.PayrollResultLines.Where(l => lineIds.Contains(l.PayrollRunEmployeeId) && l.StatutoryRuleCode == ruleCode).ToListAsync(ct);

        var rows = new List<StatutoryReportRow>();
        foreach (var e in run.Employees.OrderBy(e => e.EmployeeNumber))
        {
            var mine = lines.Where(l => l.PayrollRunEmployeeId == e.Id).ToList();
            var ee = mine.Where(l => l.Type == ResultLineType.StatutoryDeduction).Sum(l => l.Amount);
            var er = mine.Where(l => l.Type == ResultLineType.EmployerContribution).Sum(l => l.Amount);
            if (ee == 0 && er == 0) continue;
            stat.TryGetValue(e.EmployeeId, out var s);
            var (pin, member, basis) = ruleCode switch
            {
                KenyaRuleCodes.Paye => (s?.KraPin, s?.KraPin, e.TaxablePay),
                KenyaRuleCodes.Nssf => (s?.KraPin, s?.NssfNumber, e.PensionableEarnings),
                KenyaRuleCodes.Shif => (s?.KraPin, s?.ShifNumber, e.GrossPay),
                _ => (s?.KraPin, null, e.GrossPay),
            };
            rows.Add(new StatutoryReportRow(e.EmployeeNumber, e.EmployeeName, pin, member, e.GrossPay, basis, ee, er, ee + er));
        }
        var title = ruleCode switch
        {
            KenyaRuleCodes.Paye => "PAYE return (P10 schedule)", KenyaRuleCodes.Nssf => "NSSF contribution return", KenyaRuleCodes.Shif => "SHIF contribution return",
            KenyaRuleCodes.HousingLevy => "Affordable Housing Levy return", KenyaRuleCodes.Nita => "NITA levy schedule", _ => ruleCode,
        };
        return new StatutoryReport(ruleCode, title, run.Reference, period.Name, rows, rows.Sum(r => r.Employee), rows.Sum(r => r.Employer));
    }

    public static byte[] ToCsv<T>(IEnumerable<T> rows, params (string Header, Func<T, object?> Value)[] columns)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', columns.Select(c => Q(c.Header))));
        foreach (var r in rows)
            sb.AppendLine(string.Join(',', columns.Select(c => Q(c.Value(r) switch { null => "", decimal d => d.ToString("0.00", CultureInfo.InvariantCulture), var v => v.ToString() ?? "" }))));
        return Encoding.UTF8.GetBytes(sb.ToString());
        static string Q(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
