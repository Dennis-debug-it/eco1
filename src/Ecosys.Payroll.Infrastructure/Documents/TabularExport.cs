using Ecosys.Payroll.Domain.Common;
using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Ecosys.Payroll.Infrastructure.Documents;

public enum ExportFormat { Csv, Xlsx, Pdf }

public sealed record ExportColumn<T>(string Header, Func<T, object?> Value, bool Numeric = false);

public sealed record ExportFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// One tabular report → CSV, Excel (.xlsx, OOXML written directly — no third-party licence) or PDF
/// (landscape table). PRD §12: reports must support Excel, CSV and PDF exports.
/// </summary>
public static class TabularExport
{
    public static ExportFormat? Parse(string? format) => format?.ToLowerInvariant() switch
    {
        "csv" => ExportFormat.Csv, "xlsx" or "excel" => ExportFormat.Xlsx, "pdf" => ExportFormat.Pdf, _ => null,
    };

    public static ExportFile Build<T>(ExportFormat format, string title, string subtitle, string fileStem, IReadOnlyList<T> rows, IReadOnlyList<ExportColumn<T>> columns, IReadOnlyList<object?>? totals = null)
    {
        var safeStem = string.Concat(fileStem.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-'));
        return format switch
        {
            ExportFormat.Csv => new(Csv(rows, columns, totals), "text/csv", safeStem + ".csv"),
            ExportFormat.Xlsx => new(Xlsx(title, subtitle, rows, columns, totals), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", safeStem + ".xlsx"),
            ExportFormat.Pdf => new(Pdf(title, subtitle, rows, columns, totals), "application/pdf", safeStem + ".pdf"),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    private static string Text(object? v) => v switch
    {
        null => "",
        decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd"),
        DateTimeOffset d => BusinessClock.ToLocal(d).ToString("yyyy-MM-dd HH:mm"),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    // ---------------- CSV ----------------
    private static byte[] Csv<T>(IReadOnlyList<T> rows, IReadOnlyList<ExportColumn<T>> cols, IReadOnlyList<object?>? totals)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', cols.Select(c => Q(c.Header))));
        foreach (var r in rows) sb.AppendLine(string.Join(',', cols.Select(c => Q(Text(c.Value(r))))));
        if (totals is not null) sb.AppendLine(string.Join(',', totals.Select(t => Q(Text(t)))));
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        static string Q(string s) => s.Length > 0 && (s.Contains(',') || s.Contains('"') || s.Contains('\n') || "=+-@".Contains(s[0]) && !decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            ? $"\"{(("=+-@".Contains(s[0]) && !decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) ? "'" : "")}{s.Replace("\"", "\"\"")}\""
            : s; // quotes and neutralises spreadsheet formula injection
    }

    // ---------------- XLSX ----------------
    private static byte[] Xlsx<T>(string title, string subtitle, IReadOnlyList<T> rows, IReadOnlyList<ExportColumn<T>> cols, IReadOnlyList<object?>? totals)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string path, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            Entry("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Entry("_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Entry("xl/workbook.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Report" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Entry("xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            // Styles: 0 default, 1 bold, 2 number #,##0.00, 3 bold number, 4 title (bold 14pt)
            Entry("xl/styles.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="#,##0.00"/></numFmts><fonts count="3"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="14"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFE3F1EE"/></patternFill></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="5"><xf/><xf fontId="1" fillId="2" applyFont="1" applyFill="1"/><xf numFmtId="164" applyNumberFormat="1"/><xf numFmtId="164" fontId="1" fillId="2" applyNumberFormat="1" applyFont="1" applyFill="1"/><xf fontId="2" applyFont="1"/></cellXfs></styleSheet>""");

            var sheet = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0"><pane ySplit="4" topLeftCell="A5" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><cols>""");
            for (var i = 0; i < cols.Count; i++) sheet.Append(CultureInfo.InvariantCulture, $"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{(cols[i].Numeric ? 15 : Math.Clamp(cols[i].Header.Length + 6, 12, 36))}\" customWidth=\"1\"/>");
            sheet.Append("</cols><sheetData>");
            var rowNo = 0;
            void Row(IEnumerable<(object? v, bool numeric)> cells, bool bold = false, int? style = null)
            {
                rowNo++;
                sheet.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNo}\">");
                var c = 0;
                foreach (var (v, numeric) in cells)
                {
                    var refName = $"{ColName(c++)}{rowNo}";
                    if (v is null) continue;
                    if (numeric && v is decimal or int or long or double)
                        sheet.Append(CultureInfo.InvariantCulture, $"<c r=\"{refName}\" s=\"{(bold ? 3 : 2)}\"><v>{Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}</v></c>");
                    else
                        sheet.Append(CultureInfo.InvariantCulture, $"<c r=\"{refName}\" t=\"inlineStr\" s=\"{style ?? (bold ? 1 : 0)}\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(Text(v))}</t></is></c>");
                }
                sheet.Append("</row>");
            }
            Row([(title, false)], style: 4);
            Row([(subtitle, false)]);
            Row([]);
            Row(cols.Select(c => ((object?)c.Header, false)), bold: true);
            foreach (var r in rows) Row(cols.Select(c => (c.Value(r), c.Numeric)));
            if (totals is not null) Row(totals.Select((t, i) => (t, i < cols.Count && cols[i].Numeric)), bold: true);
            sheet.Append("</sheetData></worksheet>");
            Entry("xl/worksheets/sheet1.xml", sheet.ToString());
        }
        return ms.ToArray();
    }

    private static string ColName(int i) { var s = ""; i++; while (i > 0) { var m = (i - 1) % 26; s = (char)('A' + m) + s; i = (i - 1) / 26; } return s; }

    // ---------------- PDF ----------------
    private static byte[] Pdf<T>(string title, string subtitle, IReadOnlyList<T> rows, IReadOnlyList<ExportColumn<T>> cols, IReadOnlyList<object?>? totals)
    {
        var pdf = new PdfWriter(PdfWriter.A4Height, PdfWriter.A4Width); // landscape
        const float margin = 28, rowH = 12.5f;
        var usable = pdf.Width - margin * 2;
        var fontSize = cols.Count > 12 ? 6.5f : cols.Count > 9 ? 7.2f : 8f;

        // Column widths proportional to the widest content (sampled), numeric columns at least 52pt.
        var widths = cols.Select(c =>
        {
            var w = PdfWriter.Measure(c.Header, fontSize, true);
            foreach (var r in rows.Take(200)) w = Math.Max(w, PdfWriter.Measure(Display(c.Value(r), c.Numeric), fontSize));
            return Math.Max(c.Numeric ? 52f : 30f, w + 8);
        }).ToArray();
        var scale = Math.Min(1f, usable / widths.Sum());
        for (var i = 0; i < widths.Length; i++) widths[i] *= scale;

        var page = 1;
        float y = 0;
        void Header()
        {
            y = margin + 10;
            pdf.Text(margin, y, title, 13, bold: true);
            pdf.Text(pdf.Width - margin, y, $"Page {page}", 8, align: TextAlign.Right);
            y += 13;
            pdf.Text(margin, y, subtitle, 8.5f);
            y += 12;
            pdf.Rect(margin, y, usable, rowH + 2, 0.88f);
            float x = margin;
            for (var i = 0; i < cols.Count; i++)
            {
                if (cols[i].Numeric) pdf.Text(x + widths[i] - 3, y + 9.5f, cols[i].Header, fontSize, true, TextAlign.Right, widths[i] - 4);
                else pdf.Text(x + 3, y + 9.5f, cols[i].Header, fontSize, true, maxWidth: widths[i] - 4);
                x += widths[i];
            }
            y += rowH + 2;
        }
        void Line(IReadOnlyList<(string Text, bool Numeric)> cells, bool bold)
        {
            if (y + rowH > pdf.Height - margin) { pdf.NewPage(); page++; Header(); }
            float x = margin;
            for (var i = 0; i < cells.Count && i < widths.Length; i++)
            {
                if (cells[i].Numeric) pdf.Text(x + widths[i] - 3, y + 9, cells[i].Text, fontSize, bold, TextAlign.Right, widths[i] - 4);
                else pdf.Text(x + 3, y + 9, cells[i].Text, fontSize, bold, maxWidth: widths[i] - 4);
                x += widths[i];
            }
            y += rowH;
            pdf.Line(margin, y, margin + usable, y, 0.3f, 0.85f);
        }
        Header();
        foreach (var r in rows) Line(cols.Select(c => (Display(c.Value(r), c.Numeric), c.Numeric)).ToList(), false);
        if (totals is not null) Line(totals.Select((t, i) => (Display(t, i < cols.Count && cols[i].Numeric), i < cols.Count && cols[i].Numeric)).ToList(), true);
        pdf.Text(margin, pdf.Height - 14, $"Generated {BusinessClock.Now():dd MMM yyyy HH:mm} {BusinessClock.Abbreviation} · Ecosys Payroll · Confidential", 7);
        return pdf.ToBytes();
    }

    private static string Display(object? v, bool numeric) => numeric && v is decimal d ? d.ToString("N2", CultureInfo.InvariantCulture) : Text(v);
}
