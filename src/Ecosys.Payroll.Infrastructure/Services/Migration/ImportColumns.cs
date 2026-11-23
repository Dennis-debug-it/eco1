using System.Text;

namespace Ecosys.Payroll.Infrastructure.Services.Migration;

/// <summary>One column an import understands. <see cref="Aliases"/> are header spellings seen in Kenyan payroll exports.</summary>
public sealed record ImportColumn(string Name, string Description, bool Required = false, string? Example = null, params string[] Aliases);

/// <summary>A problem with one row (or with the file, when <see cref="Row"/> is 0), in words a payroll officer can act on.</summary>
public sealed record ImportProblem(int Row, string? Column, string Message);

/// <summary>
/// Matches a file's header row to an import's columns. Headers are compared without case, spaces or punctuation, against
/// the column's own name and its aliases, so "Staff No.", "EMP NO" and "Employee Number" all land on EmployeeNumber.
/// Unknown headers are reported, not fatal: old systems export columns we don't need.
/// </summary>
public sealed class ColumnMap
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Unrecognised { get; }
    public IReadOnlyList<string> Matched => _index.Keys.ToList();
    /// <summary>Headers matched by the import's own extra rule (e.g. pay component columns), by header position.</summary>
    public IReadOnlyDictionary<int, string> Extra { get; }

    private ColumnMap(Dictionary<string, int> index, List<string> unrecognised, Dictionary<int, string> extra)
    {
        _index = index; Unrecognised = unrecognised; Extra = extra;
    }

    public static string Normalise(string header)
    {
        var sb = new StringBuilder(header.Length);
        foreach (var ch in header) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <param name="extra">Claims a header the columns don't know (returns the key it stands for), or null.</param>
    public static ColumnMap Build(Sheet sheet, IReadOnlyList<ImportColumn> columns, List<ImportProblem> problems, Func<string, string?>? extra = null)
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in columns)
            foreach (var a in c.Aliases.Prepend(c.Name)) lookup.TryAdd(Normalise(a), c.Name);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var unrecognised = new List<string>();
        var extras = new Dictionary<int, string>();
        for (var i = 0; i < sheet.Headers.Count; i++)
        {
            var h = sheet.Headers[i];
            if (h.Length == 0) continue;
            if (lookup.TryGetValue(Normalise(h), out var name))
            {
                if (!index.TryAdd(name, i)) problems.Add(new ImportProblem(1, h, $"Two columns both mean {name} (\"{sheet.Headers[index[name]]}\" and \"{h}\"). Remove one."));
            }
            else if (extra?.Invoke(h) is { } key) extras[i] = key;
            else unrecognised.Add(h);
        }
        return new ColumnMap(index, unrecognised, extras);
    }

    public bool Has(string column) => _index.ContainsKey(column);
    public int? IndexOf(string column) => _index.TryGetValue(column, out var i) ? i : null;
    public string HeaderOf(Sheet sheet, string column) => _index.TryGetValue(column, out var i) ? sheet.Headers[i] : column;
}

/// <summary>Reads one row's cells by column name, recording a problem (and returning null) for anything unreadable.</summary>
public sealed class RowReader(SheetRow row, ColumnMap map, List<ImportProblem> problems)
{
    public int Number => row.RowNumber;
    public bool HasProblems => problems.Any(p => p.Row == row.RowNumber);

    public void Problem(string? column, string message) => problems.Add(new ImportProblem(row.RowNumber, column, message));

    public string? Cell(int index) => index < row.Cells.Count && row.Cells[index].Length > 0 ? row.Cells[index] : null;

    public string? Text(string column, bool required = false)
    {
        var v = map.IndexOf(column) is { } i ? Cell(i) : null;
        if (v is null && required) Problem(column, $"{column} is required.");
        return v;
    }

    public DateOnly? Date(string column, bool required = false)
    {
        var s = Text(column, required);
        if (s is null) return null;
        if (ImportValues.TryDate(s, out var d)) return d;
        Problem(column, $"{column} '{s}' isn't a date. Use day/month/year, e.g. 05/01/2026, or 2026-01-05.");
        return null;
    }

    public decimal? Money(string column, bool required = false, bool allowNegative = false) => MoneyAt(map.IndexOf(column), column, required, allowNegative);

    public decimal? MoneyAt(int? index, string column, bool required = false, bool allowNegative = false)
    {
        var s = index is { } i ? Cell(i) : null;
        if (s is null) { if (required) Problem(column, $"{column} is required."); return null; }
        if (!ImportValues.TryMoney(s, out var v)) { Problem(column, $"{column} '{s}' isn't an amount."); return null; }
        if (v < 0 && !allowNegative) { Problem(column, $"{column} can't be negative."); return null; }
        return Math.Round(v, 2, MidpointRounding.AwayFromZero);
    }

    public int? Int(string column, int min = 0, int max = int.MaxValue)
    {
        var s = Text(column);
        if (s is null) return null;
        if (decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) && d == Math.Floor(d) && d >= min && d <= max) return (int)d;
        Problem(column, $"{column} '{s}' should be a whole number from {min} to {max}.");
        return null;
    }

    public bool? Bool(string column)
    {
        var s = Text(column);
        if (s is null) return null;
        if (ImportValues.Bool(s) is { } b) return b;
        Problem(column, $"{column} '{s}' should be Yes or No.");
        return null;
    }

    /// <summary>Reads a value from a closed list; <paramref name="words"/> maps normalised spellings to values.</summary>
    public T? Choice<T>(string column, IReadOnlyDictionary<string, T> words, string allowed) where T : struct
    {
        var s = Text(column);
        if (s is null) return null;
        if (words.TryGetValue(ColumnMap.Normalise(s), out var v)) return v;
        Problem(column, $"{column} '{s}' isn't one of: {allowed}.");
        return null;
    }
}
