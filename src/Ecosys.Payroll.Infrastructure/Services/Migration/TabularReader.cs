using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Infrastructure.Services.Migration;

/// <summary>A spreadsheet read into text cells. <see cref="RowNumber"/> is the row as the user sees it in their file (header = 1).</summary>
public sealed record SheetRow(int RowNumber, IReadOnlyList<string> Cells);

public sealed record Sheet(IReadOnlyList<string> Headers, IReadOnlyList<SheetRow> Rows);

/// <summary>
/// Reads an uploaded import file: CSV (comma, semicolon or tab; quoted cells may hold line breaks) or the first sheet of
/// an .xlsx workbook. No third-party parser: an .xlsx is a zip of XML parts, read with the framework's own classes.
/// Blank rows are skipped. Cells are trimmed text; dates in .xlsx stay as Excel serial numbers for <see cref="ImportValues"/>.
/// </summary>
public static class TabularReader
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxRows = 20_000;

    public static Sheet Read(byte[] content, string? fileName)
    {
        if (content.Length == 0) throw new DomainException("The file is empty.", "VALIDATION");
        if (content.Length > MaxBytes) throw new DomainException("The file is larger than 5 MB. Split it into smaller files.", "VALIDATION");
        var isZip = content.Length > 4 && content[0] == 'P' && content[1] == 'K';
        var name = (fileName ?? "").ToLowerInvariant();
        if (name.EndsWith(".xls")) throw new DomainException("Old .xls workbooks can't be read. Save the file as .xlsx or CSV.", "VALIDATION");
        var rows = isZip ? ReadXlsx(content) : ReadCsv(Decode(content));
        var nonBlank = rows.Select((cells, i) => new SheetRow(i + 1, cells)).Where(r => r.Cells.Any(c => c.Length > 0)).ToList();
        if (nonBlank.Count < 2) throw new DomainException("The file needs a header row and at least one data row.", "VALIDATION");
        if (nonBlank.Count - 1 > MaxRows) throw new DomainException($"The file has more than {MaxRows:N0} rows. Split it into smaller files.", "VALIDATION");
        var headers = nonBlank[0].Cells.Select(h => h.Trim()).ToList();
        return new Sheet(headers, nonBlank.Skip(1).ToList());
    }

    private static string Decode(byte[] bytes)
    {
        // UTF-8 (with or without BOM) is the norm; Excel's "CSV" on Windows writes Windows-1252, which is not valid UTF-8.
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    public static List<List<string>> ReadCsv(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ',', ';', '\t' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"' && cell.ToString().Trim().Length == 0) { cell.Clear(); quoted = true; }
            else if (c == delimiter) { row.Add(cell.ToString().Trim()); cell.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString().Trim()); cell.Clear();
                rows.Add(row); row = [];
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString().Trim()); rows.Add(row); }
        return rows;
    }

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<List<string>> ReadXlsx(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            XDocument Load(string path) => XDocument.Load((zip.GetEntry(path) ?? throw new DomainException("This isn't a readable .xlsx workbook.", "VALIDATION")).Open());

            // The first sheet in the workbook's own order, found through its relationship id.
            var workbook = Load("xl/workbook.xml");
            var firstSheet = workbook.Descendants(Main + "sheet").FirstOrDefault() ?? throw new DomainException("The workbook has no sheets.", "VALIDATION");
            var relId = (string?)firstSheet.Attribute(Rel + "id");
            var rels = Load("xl/_rels/workbook.xml.rels");
            var target = rels.Descendants(PkgRel + "Relationship").FirstOrDefault(r => (string?)r.Attribute("Id") == relId)?.Attribute("Target")?.Value ?? "worksheets/sheet1.xml";
            var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

            var shared = zip.GetEntry("xl/sharedStrings.xml") is { } ss
                ? XDocument.Load(ss.Open()).Root!.Elements(Main + "si").Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value))).ToList()
                : [];

            var rows = new List<List<string>>();
            foreach (var r in Load(sheetPath).Descendants(Main + "row"))
            {
                var rowIndex = (int?)r.Attribute("r") ?? rows.Count + 1;
                while (rows.Count < rowIndex - 1) rows.Add([]); // rows the workbook leaves out are blank
                var cells = new List<string>();
                foreach (var c in r.Elements(Main + "c"))
                {
                    var col = ColumnIndex((string?)c.Attribute("r")) ?? cells.Count;
                    while (cells.Count < col) cells.Add("");
                    var type = (string?)c.Attribute("t");
                    var v = c.Element(Main + "v")?.Value;
                    var text = type switch
                    {
                        "s" when int.TryParse(v, out var i) && i >= 0 && i < shared.Count => shared[i],
                        "inlineStr" => string.Concat(c.Descendants(Main + "t").Select(t => t.Value)),
                        "b" => v == "1" ? "TRUE" : "FALSE",
                        _ => v ?? "",
                    };
                    cells.Add(text.Trim());
                }
                rows.Add(cells);
            }
            return rows;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            throw new DomainException("This isn't a readable .xlsx workbook. Save it again from Excel, or export it as CSV.", "VALIDATION");
        }
    }

    /// <summary>"C7" → 2.</summary>
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var n = 0;
        foreach (var ch in reference)
        {
            if (!char.IsAsciiLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n == 0 ? null : n - 1;
    }
}

/// <summary>Turns import cells into typed values, reading the formats people actually have in payroll spreadsheets.</summary>
public static class ImportValues
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "d.M.yyyy", "d MMM yyyy", "dd MMM yyyy", "d-MMM-yyyy", "dd-MMM-yyyy", "d-MMM-yy", "yyyy/MM/dd", "yyyy-MM-ddTHH:mm:ss"];

    /// <summary>Day first, as Kenyan payroll files write it. An Excel serial number (a date cell in .xlsx) is accepted too.</summary>
    public static bool TryDate(string s, out DateOnly date)
    {
        s = s.Trim();
        if (DateOnly.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)) return true;
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt)) { date = DateOnly.FromDateTime(dt); return true; }
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 1 and < 100000)
        {
            date = DateOnly.FromDateTime(DateTime.FromOADate(Math.Floor(serial)));
            return true;
        }
        return false;
    }

    /// <summary>"12,500.00", "KES 12500", "(1,200)" for a negative.</summary>
    public static bool TryMoney(string s, out decimal value)
    {
        s = s.Trim();
        foreach (var prefix in new[] { "KES", "KSH", "Ksh", "Kshs", "KShs" }) if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..].Trim();
        var negative = s.StartsWith('(') && s.EndsWith(')');
        if (negative) s = s[1..^1];
        s = s.Replace(",", "").Replace(" ", "");
        if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value)) return false;
        if (negative) value = -value;
        return true;
    }

    public static bool? Bool(string s) => s.Trim().ToLowerInvariant() switch
    {
        "y" or "yes" or "true" or "1" or "x" => true,
        "n" or "no" or "false" or "0" or "" => false,
        _ => null,
    };
}
