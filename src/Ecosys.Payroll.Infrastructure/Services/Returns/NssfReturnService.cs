using System.Text;
using System.Text.RegularExpressions;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Engine.Jurisdictions.Kenya;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Engine.Rules;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services.Migration;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Returns;

/// <summary>
/// <see cref="PortalTemplate"/>: NSSF's employer-portal payroll upload (template release 03.02.18): one row per member with
/// gross pay; the portal works out the contributions. <see cref="Returns2014"/>: the returns file in NSSF's "new returns
/// file structure" specification (NSSF Act 2013): a header block, then one row per member per contribution type.
/// </summary>
public enum NssfFileLayout { PortalTemplate, Returns2014 }

public sealed record ReturnProblem(string EmployeeNumber, string EmployeeName, string Message);

public sealed record NssfReturnCheck(string RunReference, string Period, int Members, decimal Pensionable, decimal Member, decimal Employer,
    bool Ready, IReadOnlyList<ReturnProblem> Problems, IReadOnlyList<string> Warnings);

public sealed record NssfReturnFile(byte[] Content, string FileName, NssfReturnCheck Check);

/// <summary>
/// Builds NSSF upload files from an approved payroll run, exactly as NSSF's specifications lay them out, after checking
/// every member: an NSSF number of 9–10 characters, an ID number, names without the characters the portal rejects, and
/// Tier I + Tier II adding back to what the payroll deducted. A file is only produced when nothing is wrong.
/// </summary>
public sealed partial class NssfReturnService(PayrollDbContext db, ITenantContext tenant, AuditService audit, MigrationGuard migration)
{
    private sealed record Member(string PayrollNumber, string Surname, string OtherNames, string IdNumber, string KraPin, string NssfNumber,
        decimal Pensionable, decimal Gross, TierSplit Tiers, decimal Employee, decimal Employer);

    public async Task<NssfReturnCheck> CheckAsync(Guid runId, CancellationToken ct = default) => (await BuildAsync(runId, ct)).Check;

    public async Task<NssfReturnFile> ExportAsync(Guid runId, NssfFileLayout layout, CancellationToken ct = default)
    {
        await migration.EnsureLiveAsync("export an NSSF return for filing", ct);
        var (check, members, header) = await BuildAsync(runId, ct);
        if (!check.Ready) throw new ReturnNotReadyException(check);
        var rows = layout == NssfFileLayout.PortalTemplate ? PortalRows(members) : ReturnsRows(members, header);
        var name = $"NSSF_{(layout == NssfFileLayout.PortalTemplate ? "Payroll" : "Return")}_{Safe(header.EmployerNssf)}_{header.Period}.xlsx";
        audit.Record("report.nssf.exported", nameof(PayrollRun), runId, new { layout = layout.ToString(), members = check.Members, check.Member, check.Employer }, header.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return new NssfReturnFile(PlainXlsx.Write(rows), name, check);
    }

    private sealed record Header(Guid LegalEntityId, string EmployerPin, string EmployerNssf, string EmployerName, string Period);

    private async Task<(NssfReturnCheck Check, List<Member> Members, Header Header)> BuildAsync(Guid runId, CancellationToken ct)
    {
        tenant.Require(Permissions.ReportsRead);
        tenant.Require(Permissions.SalaryRead); // the file carries each member's pay
        var run = await db.PayrollRuns.Include(r => r.Employees).FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new NotFoundException(nameof(PayrollRun), runId);
        tenant.RequireLegalEntity(run.LegalEntityId);
        var entity = await db.LegalEntities.FirstAsync(e => e.Id == run.LegalEntityId, ct);
        var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PayrollPeriodId, ct);
        var problems = new List<ReturnProblem>();
        var warnings = new List<string>();
        if (run.Status == PayrollRunStatus.Reversed) problems.Add(new("", "", "This run was reversed. File the return from the run that replaced it."));
        else if (run.ApprovedAt is null) problems.Add(new("", "", "Approve the run first: a return is filed from the figures that were approved."));
        if (string.IsNullOrWhiteSpace(entity.NssfEmployerNumber)) problems.Add(new("", "", $"{entity.Name} has no NSSF employer number. Add it under Organisation, Legal entities."));
        var header = new Header(entity.Id, entity.TaxPin, entity.NssfEmployerNumber?.Trim() ?? "", Clean(entity.Name), period.PeriodEnd.ToString("MMyyyy"));

        var rowIds = run.Employees.Select(e => e.Id).ToList();
        var lines = await db.PayrollResultLines.Where(l => rowIds.Contains(l.PayrollRunEmployeeId) && l.StatutoryRuleCode == KenyaRuleCodes.Nssf).ToListAsync(ct);
        var versionIds = lines.Where(l => l.StatutoryRuleVersionId != null).Select(l => l.StatutoryRuleVersionId!.Value).Distinct().ToList();
        // The exact rule each line was calculated with (platform rules carry no tenant, so the tenant filter is bypassed by id).
        var rules = (await db.StatutoryRuleVersions.IgnoreQueryFilters().Where(r => versionIds.Contains(r.Id)).ToListAsync(ct)).ToDictionary(r => r.Id, r => r.ToEngine());
        var employeeIds = run.Employees.Select(e => e.EmployeeId).ToList();
        var people = await db.Employees.IgnoreQueryFilters().Where(e => employeeIds.Contains(e.Id) && e.TenantId == run.TenantId).ToDictionaryAsync(e => e.Id, ct);
        var statutory = await db.EmployeeStatutoryDetails.Where(s => employeeIds.Contains(s.EmployeeId)).ToDictionaryAsync(s => s.EmployeeId, ct);

        var members = new List<Member>();
        var belowGross = 0;
        foreach (var e in run.Employees.OrderBy(x => x.EmployeeNumber))
        {
            var mine = lines.Where(l => l.PayrollRunEmployeeId == e.Id).ToList();
            var employee = mine.Where(l => l.Type == ResultLineType.StatutoryDeduction).Sum(l => l.Amount);
            var employer = mine.Where(l => l.Type == ResultLineType.EmployerContribution).Sum(l => l.Amount);
            if (employee == 0 && employer == 0) continue; // exempt or not covered: not on the return
            void Problem(string m) => problems.Add(new(e.EmployeeNumber, e.EmployeeName, m));
            var person = people.GetValueOrDefault(e.EmployeeId);
            var stat = statutory.GetValueOrDefault(e.EmployeeId);

            var nssf = (stat?.NssfNumber ?? "").Replace(" ", "").ToUpperInvariant();
            if (nssf.Length == 0) Problem("No NSSF number. Add it on the employee's statutory details.");
            else if (!NssfNumberRx().IsMatch(nssf)) Problem($"NSSF number '{nssf}' should be 9–10 letters or digits, with its leading zeros and any trailing X.");
            var id = person?.NationalId ?? person?.PassportNumber;
            if (string.IsNullOrWhiteSpace(id)) Problem("No ID or passport number. NSSF needs one for every member.");
            var rule = mine.Select(l => l.StatutoryRuleVersionId).FirstOrDefault(v => v != null) is { } vid && rules.TryGetValue(vid, out var r) ? r : null;
            TierSplit? tiers = null;
            if (rule is null || rule.Method != StatutoryCalculationMethod.TieredContribution) Problem("The NSSF rule used for this run isn't a tiered contribution, so Tier I and Tier II can't be reported.");
            else
            {
                tiers = RuleEvaluator.Tiers(rule, e.PensionableEarnings);
                if (tiers.Tier1Employee + tiers.Tier2Employee != employee || tiers.Tier1Employer + tiers.Tier2Employer != employer)
                    Problem($"NSSF deducted ({employee:N2} + {employer:N2}) doesn't match Tier I + Tier II on pensionable pay of {e.PensionableEarnings:N2}. Recalculate the run before filing.");
            }
            if (e.PensionableEarnings < e.GrossPay) belowGross++;
            if (person is not null && tiers is not null)
                members.Add(new Member(Clean(e.EmployeeNumber), Clean(person.LastName), Clean($"{person.FirstName} {person.MiddleName}"), Clean(id ?? ""), (stat?.KraPin ?? "").ToUpperInvariant(),
                    nssf, e.PensionableEarnings, e.GrossPay, tiers, employee, employer));
        }
        if (members.Count == 0 && problems.Count == 0) problems.Add(new("", "", "Nobody in this run has NSSF deducted, so there is nothing to file."));
        var missingPins = members.Count(m => m.KraPin.Length == 0);
        if (missingPins > 0) warnings.Add($"{missingPins} member(s) have no KRA PIN. NSSF accepts the file without it, but add it when you can.");
        if (belowGross > 0)
            warnings.Add($"{belowGross} member(s) have NSSF worked out on pensionable pay below their gross pay. The files carry pensionable pay, so NSSF's own calculation matches what was deducted. If NSSF expects gross pay instead, change which pay components count as pensionable.");
        var rounded = members.Sum(m => Up(m.Tiers.Tier1Employee) + Up(m.Tiers.Tier2Employee) + Up(m.Tiers.Tier1Employer) + Up(m.Tiers.Tier2Employer)) - members.Sum(m => m.Employee + m.Employer);
        if (rounded > 0) warnings.Add($"The returns file rounds each amount up to the shilling, as NSSF requires, which adds {rounded:N2} to the total paid.");

        var check = new NssfReturnCheck(run.Reference, period.PeriodEnd.ToString("MMMM yyyy"), members.Count, members.Sum(m => m.Pensionable), members.Sum(m => m.Employee), members.Sum(m => m.Employer),
            problems.Count == 0, problems, warnings);
        return (check, members, header);
    }

    /// <summary>The portal template: its own header row, then one row per member. Columns A–F as text; gross and voluntary as numbers.</summary>
    private static List<IReadOnlyList<XlsxCell>> PortalRows(List<Member> members)
    {
        var rows = new List<IReadOnlyList<XlsxCell>>
        {
            new[] { "PAYROLL NUMBER", "SURNAME", "OTHER NAMES", "ID NO", "KRA PIN", "NSSF NO", "GROSS PAY", "VOLUNTARY" }.Select(XlsxCell.T).ToList(),
        };
        rows.AddRange(members.Select(m => (IReadOnlyList<XlsxCell>)
            [XlsxCell.T(m.PayrollNumber), XlsxCell.T(m.Surname), XlsxCell.T(m.OtherNames), XlsxCell.T(m.IdNumber), XlsxCell.T(m.KraPin), XlsxCell.T(m.NssfNumber), XlsxCell.N(m.Pensionable), XlsxCell.N(0m)]));
        return rows;
    }

    /// <summary>
    /// The 2014 returns file: header in A1:B10, row 11 blank, column titles in row 12, members from row 13, return type 01.
    /// Codes (return, contribution and income type) are stored as text, as the specification asks of identifiers.
    /// </summary>
    private static List<IReadOnlyList<XlsxCell>> ReturnsRows(List<Member> members, Header h)
    {
        var details = new List<(Member M, string Type, decimal Income, decimal Member, decimal Employer)>();
        foreach (var m in members)
        {
            details.Add((m, "101", Up(m.Pensionable), Up(m.Tiers.Tier1Employee), Up(m.Tiers.Tier1Employer)));
            if (m.Tiers.Tier2Employee > 0 || m.Tiers.Tier2Employer > 0) details.Add((m, "102", Up(m.Pensionable), Up(m.Tiers.Tier2Employee), Up(m.Tiers.Tier2Employer)));
        }
        IReadOnlyList<XlsxCell> Pair(string label, XlsxCell value) => [XlsxCell.T(label), value];
        var rows = new List<IReadOnlyList<XlsxCell>>
        {
            Pair("NSSF CONTRIBUTIONS", XlsxCell.T("01")),
            Pair("EMPLOYER KRA PIN:", XlsxCell.T(h.EmployerPin)),
            Pair("EMPLOYER NSSF NUMBER:", XlsxCell.T(h.EmployerNssf)),
            Pair("EMPLOYER NAME:", XlsxCell.T(h.EmployerName)),
            Pair("CONTRIBUTIONS PERIOD:", XlsxCell.T(h.Period)),
            Pair("TOTAL INCOME:", XlsxCell.N(details.Sum(d => d.Income))),
            Pair("TOTAL MEMBER:", XlsxCell.N(details.Sum(d => d.Member))),
            Pair("TOTAL EMPLOYER:", XlsxCell.N(details.Sum(d => d.Employer))),
            Pair("TOTAL CONTRIBUTIONS:", XlsxCell.N(details.Sum(d => d.Member + d.Employer))),
            Pair("TOTAL RECORDS", XlsxCell.N(details.Count)),
            Array.Empty<XlsxCell>(), // row 11: one blank row between the header and the details
            new[] { "PAYROLL NUMBER", "SURNAME", "OTHER NAMES", "ID NO", "KRA PIN", "NSSF NO.", "CONTRIBUTIONS TYPE", "INCOME", "INCOME TYPE", "MEMBER", "EMPLOYER", "TOTAL" }.Select(XlsxCell.T).ToList(),
        };
        rows.AddRange(details.Select(d => (IReadOnlyList<XlsxCell>)
            [XlsxCell.T(d.M.PayrollNumber), XlsxCell.T(d.M.Surname), XlsxCell.T(d.M.OtherNames), XlsxCell.T(d.M.IdNumber), XlsxCell.T(d.M.KraPin), XlsxCell.T(d.M.NssfNumber),
             XlsxCell.T(d.Type), XlsxCell.N(d.Income), XlsxCell.T("1"), XlsxCell.N(d.Member), XlsxCell.N(d.Employer), XlsxCell.N(d.Member + d.Employer)]));
        return rows;
    }

    /// <summary>NSSF: "rounded UPWARDS to the nearest Shilling".</summary>
    private static decimal Up(decimal v) => Math.Ceiling(v);

    /// <summary>The portal rejects special characters such as / , " and -: they become spaces, and runs of spaces collapse.</summary>
    public static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == ' ' ? ch : ' ');
        return SpacesRx().Replace(sb.ToString(), " ").Trim().ToUpperInvariant();
    }

    private static string Safe(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    [GeneratedRegex("^[A-Z0-9]{8,9}[0-9X]$")] private static partial Regex NssfNumberRx();
    [GeneratedRegex(" {2,}")] private static partial Regex SpacesRx();
}

/// <summary>A return was asked for while members still have problems; carries the check so the caller can show them.</summary>
public sealed class ReturnNotReadyException(NssfReturnCheck check) : DomainException("The return has problems to fix first.", "RETURN_NOT_READY")
{
    public NssfReturnCheck Check { get; } = check;
}
