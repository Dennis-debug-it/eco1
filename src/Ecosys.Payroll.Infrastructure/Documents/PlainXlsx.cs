using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Ecosys.Payroll.Infrastructure.Documents;

/// <summary>A cell in an upload file: text (stored as text, so leading zeros survive), a number, or empty.</summary>
public readonly record struct XlsxCell(string? Text, decimal? Number)
{
    public static XlsxCell T(string? text) => new(text ?? "", null);
    public static XlsxCell N(decimal number) => new(null, number);
    public static readonly XlsxCell Empty = new(null, null);
}

/// <summary>
/// The plainest possible .xlsx, for government upload portals that reject anything unusual: one sheet named Sheet1,
/// no formulas, no styling beyond "text" for identifier cells, numbers without thousands separators, strings in the
/// shared-strings table (the form every spreadsheet reader understands). Rows are written exactly as given, so a blank
/// row in the list is a blank row in the file.
/// </summary>
public static class PlainXlsx
{
    public static byte[] Write(IReadOnlyList<IReadOnlyList<XlsxCell>> rows)
    {
        var strings = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        int Shared(string s) { if (!index.TryGetValue(s, out var i)) { i = strings.Count; strings.Add(s); index[s] = i; } return i; }

        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.All(c => c.Text is null && c.Number is null)) continue; // a blank row: simply absent
            sheet.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < row.Count; c++)
            {
                var cell = row[c];
                var reference = Column(c) + (r + 1);
                // "450000", not "450000.0000": no trailing zeros, no thousands separators.
                if (cell.Number is { } n) sheet.Append($"<c r=\"{reference}\"><v>{n.ToString("0.##########", CultureInfo.InvariantCulture)}</v></c>");
                else if (cell.Text is { } t) sheet.Append($"<c r=\"{reference}\" s=\"1\" t=\"s\"><v>{Shared(t)}</v></c>");
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");

        var sst = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"{strings.Count}\" uniqueCount=\"{strings.Count}\">");
        foreach (var s in strings) sst.Append("<si><t xml:space=\"preserve\">").Append(SecurityElement.Escape(s)).Append("</t></si>");
        sst.Append("</sst>");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Put(string path, string xml) { using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)); w.Write(xml); }
            Put("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/></Types>");
            Put("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Put("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>");
            // Style 0 = General (numbers), style 1 = Text (numFmt 49), so identifiers keep leading zeros when the file is opened.
            Put("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs></styleSheet>");
            Put("xl/sharedStrings.xml", sst.ToString());
            Put("xl/worksheets/sheet1.xml", sheet.ToString());
        }
        return ms.ToArray();
    }

    private static string Column(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }
}
