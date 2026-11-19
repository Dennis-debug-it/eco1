using System.Globalization;
using System.Text;
using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Infrastructure.Services;

/// <summary>Figures for one employee in one payroll, from either system. Null means the column wasn't supplied.</summary>
public sealed record RegisterFigures(string EmployeeNumber, string? Name, decimal? Gross, decimal? Taxable, decimal? Paye, decimal? Nssf, decimal? Shif, decimal? HousingLevy, decimal? OtherDeductions, decimal? Net);

public sealed record FieldDifference(string Field, decimal Ecosys, decimal Legacy, decimal Difference);

public enum ReconcileStatus { Matched, Different, OnlyInEcosys, OnlyInLegacy }

public sealed record ReconcileRow(string EmployeeNumber, string Name, ReconcileStatus Status, IReadOnlyList<FieldDifference> Differences, RegisterFigures? Ecosys, RegisterFigures? Legacy);

public sealed record ReconcileTotal(string Field, decimal Ecosys, decimal Legacy, decimal Difference);

public sealed record ReconcileResult(decimal Tolerance, int Employees, int Matched, int Different, int OnlyInEcosys, int OnlyInLegacy,
    IReadOnlyList<ReconcileTotal> Totals, IReadOnlyList<ReconcileRow> Rows, IReadOnlyList<string> Columns, IReadOnlyList<string> Warnings)
{
    public bool FullyReconciled => Different == 0 && OnlyInEcosys == 0 && OnlyInLegacy == 0;
}

/// <summary>
/// Parallel-run check (PRD §18): compares the outgoing system's payroll register with an Ecosys run, employee by
/// employee and figure by figure. Only the columns present in the legacy file are compared, so a register with just
/// gross, PAYE and net still reconciles what it can. Pure: no database access.
/// </summary>
public static class ParallelRunReconciler
{
    public static readonly string[] Fields = ["Gross", "Taxable", "PAYE", "NSSF", "SHIF", "HousingLevy", "OtherDeductions", "Net"];

    // Header aliases seen in Kenyan payroll registers (Sage, QuickBooks, spreadsheets, bureau outputs).
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["employeenumber"] = "EmployeeNumber", ["employeeno"] = "EmployeeNumber", ["empno"] = "EmployeeNumber", ["staffno"] = "EmployeeNumber", ["staffnumber"] = "EmployeeNumber",
        ["payrollno"] = "EmployeeNumber", ["payrollnumber"] = "EmployeeNumber", ["employeeid"] = "EmployeeNumber", ["empid"] = "EmployeeNumber", ["no"] = "EmployeeNumber",
        ["name"] = "Name", ["employeename"] = "Name", ["fullname"] = "Name", ["names"] = "Name",
        ["gross"] = "Gross", ["grosspay"] = "Gross", ["grosssalary"] = "Gross", ["totalgross"] = "Gross", ["grossearnings"] = "Gross",
        ["taxable"] = "Taxable", ["taxablepay"] = "Taxable", ["taxableincome"] = "Taxable", ["chargeablepay"] = "Taxable",
        ["paye"] = "PAYE", ["tax"] = "PAYE", ["payetax"] = "PAYE", ["incometax"] = "PAYE", ["netpaye"] = "PAYE",
        ["nssf"] = "NSSF", ["nssfemployee"] = "NSSF", ["nssfee"] = "NSSF", ["nssfdeduction"] = "NSSF",
        ["shif"] = "SHIF", ["sha"] = "SHIF", ["shifdeduction"] = "SHIF", ["nhif"] = "SHIF",
        ["housinglevy"] = "HousingLevy", ["ahl"] = "HousingLevy", ["affordablehousinglevy"] = "HousingLevy", ["housinglevyemployee"] = "HousingLevy", ["ahlemployee"] = "HousingLevy",
        ["otherdeductions"] = "OtherDeductions", ["otherdeduction"] = "OtherDeductions", ["voluntarydeductions"] = "OtherDeductions",
        ["net"] = "Net", ["netpay"] = "Net", ["netsalary"] = "Net", ["takehome"] = "Net", ["amountpayable"] = "Net",
    };

    public static string Template => "EmployeeNumber,Name,Gross,Taxable,PAYE,NSSF,SHIF,HousingLevy,OtherDeductions,Net\r\n";

    /// <summary>Reads a CSV register. Accepts quoted fields, thousands separators, "KES"/"Ksh" prefixes and (negative) brackets.</summary>
    public static (List<RegisterFigures> Rows, List<string> Columns, List<string> Warnings) ParseCsv(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(l => l.Trim().Trim(',').Length > 0).ToList();
        if (lines.Count < 2) throw new DomainException("The file has no data rows. The first row must be column headings.", "VALIDATION");
        var delimiter = lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',') ? ';' : ',';
        var header = Split(lines[0], delimiter).Select(h => Aliases.TryGetValue(Normalise(h), out var f) ? f : null).ToList();
        if (!header.Contains("EmployeeNumber"))
            throw new DomainException("Couldn't find the employee number column. Name it EmployeeNumber (or Staff No, Emp No, Payroll No).", "VALIDATION");
        var columns = Fields.Where(header.Contains).ToList();
        if (columns.Count == 0) throw new DomainException("No amount columns recognised. Use headings such as Gross, PAYE, NSSF, SHIF, Housing Levy, Net.", "VALIDATION");

        var rows = new List<RegisterFigures>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Count; i++)
        {
            var cells = Split(lines[i], delimiter);
            string? Cell(string field) { var c = header.IndexOf(field); return c >= 0 && c < cells.Count ? cells[c] : null; }
            decimal? Num(string field)
            {
                var raw = Cell(field);
                if (raw is null || !header.Contains(field)) return null;
                if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "-") return 0m;
                return Amount(raw) ?? throw new DomainException($"Row {i + 1}: '{raw}' in the {field} column isn't a number.", "VALIDATION");
            }
            var number = Cell("EmployeeNumber")?.Trim();
            if (string.IsNullOrEmpty(number)) { warnings.Add($"Row {i + 1} has no employee number and was skipped (a totals row?)."); continue; }
            if (!seen.Add(number)) { warnings.Add($"Employee {number} appears more than once; only the first row was used."); continue; }
            rows.Add(new RegisterFigures(number, Cell("Name")?.Trim(), Num("Gross"), Num("Taxable"), Num("PAYE"), Num("NSSF"), Num("SHIF"), Num("HousingLevy"), Num("OtherDeductions"), Num("Net")));
        }
        return (rows, columns, warnings);
    }

    public static ReconcileResult Reconcile(IReadOnlyList<RegisterFigures> ecosys, IReadOnlyList<RegisterFigures> legacy, IReadOnlyList<string> columns, decimal tolerance, IReadOnlyList<string>? warnings = null)
    {
        var mine = ecosys.ToDictionary(r => r.EmployeeNumber, StringComparer.OrdinalIgnoreCase);
        var theirs = legacy.ToDictionary(r => r.EmployeeNumber, StringComparer.OrdinalIgnoreCase);
        var rows = new List<ReconcileRow>();
        foreach (var number in mine.Keys.Union(theirs.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            mine.TryGetValue(number, out var e); theirs.TryGetValue(number, out var l);
            var name = e?.Name ?? l?.Name ?? "";
            if (e is null) { rows.Add(new(number, name, ReconcileStatus.OnlyInLegacy, [], null, l)); continue; }
            if (l is null) { rows.Add(new(number, name, ReconcileStatus.OnlyInEcosys, [], e, null)); continue; }
            var diffs = new List<FieldDifference>();
            foreach (var f in columns)
            {
                var (a, b) = (Get(e, f) ?? 0m, Get(l, f));
                if (b is null) continue;
                if (Math.Abs(a - b.Value) > tolerance) diffs.Add(new(f, a, b.Value, a - b.Value));
            }
            rows.Add(new(number, name, diffs.Count == 0 ? ReconcileStatus.Matched : ReconcileStatus.Different, diffs, e, l));
        }
        var totals = columns.Select(f =>
        {
            var a = ecosys.Sum(r => Get(r, f) ?? 0m); var b = legacy.Sum(r => Get(r, f) ?? 0m);
            return new ReconcileTotal(f, a, b, a - b);
        }).ToList();
        return new ReconcileResult(tolerance, rows.Count, rows.Count(r => r.Status == ReconcileStatus.Matched), rows.Count(r => r.Status == ReconcileStatus.Different),
            rows.Count(r => r.Status == ReconcileStatus.OnlyInEcosys), rows.Count(r => r.Status == ReconcileStatus.OnlyInLegacy), totals, rows, columns, warnings ?? []);
    }

    public static decimal? Get(RegisterFigures r, string field) => field switch
    {
        "Gross" => r.Gross, "Taxable" => r.Taxable, "PAYE" => r.Paye, "NSSF" => r.Nssf, "SHIF" => r.Shif,
        "HousingLevy" => r.HousingLevy, "OtherDeductions" => r.OtherDeductions, "Net" => r.Net, _ => null,
    };

    private static string Normalise(string h) => new(h.Trim().Trim('"').Where(char.IsLetterOrDigit).ToArray());

    private static decimal? Amount(string raw)
    {
        var s = raw.Trim().Trim('"').Replace("KES", "", StringComparison.OrdinalIgnoreCase).Replace("Ksh", "", StringComparison.OrdinalIgnoreCase).Replace(",", "").Replace(" ", "");
        var negative = s.StartsWith('(') && s.EndsWith(')');
        if (negative) s = s[1..^1];
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? (negative ? -v : v) : null;
    }

    private static List<string> Split(string line, char delimiter)
    {
        var cells = new List<string>(); var cur = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else quoted = !quoted; }
            else if (c == delimiter && !quoted) { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        cells.Add(cur.ToString());
        return cells;
    }
}
