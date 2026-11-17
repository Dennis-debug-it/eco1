using System.Globalization;
using System.Text;

namespace Ecosys.Payroll.Infrastructure.Documents;

/// <summary>
/// Minimal, dependency-free PDF 1.4 writer sufficient for tabular documents (payslips, registers).
/// Uses the standard Helvetica fonts (no embedding) with WinAnsi encoding. Coordinates are in points
/// with the origin at the top-left for convenience.
/// </summary>
public sealed class PdfWriter
{
    public const float A4Width = 595.28f, A4Height = 841.89f;
    private readonly List<StringBuilder> _pages = [];
    private StringBuilder _current = new();
    private readonly float _width, _height;

    public PdfWriter(float width = A4Width, float height = A4Height)
    {
        _width = width; _height = height;
        _pages.Add(_current);
    }

    public float Width => _width;
    public float Height => _height;

    public void NewPage() { _current = new StringBuilder(); _pages.Add(_current); }

    public void Text(float x, float y, string text, float size = 10, bool bold = false, TextAlign align = TextAlign.Left, float? maxWidth = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        var w = Measure(text, size, bold);
        if (maxWidth is { } mw && w > mw)
        {
            while (text.Length > 1 && Measure(text + "…", size, bold) > mw) text = text[..^1];
            text += "…"; w = Measure(text, size, bold);
        }
        var tx = align switch { TextAlign.Right => x - w, TextAlign.Center => x - w / 2, _ => x };
        _current.Append(CultureInfo.InvariantCulture, $"BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {tx:0.##} {_height - y:0.##} Td ({Escape(text)}) Tj ET\n");
    }

    public void Line(float x1, float y1, float x2, float y2, float width = 0.5f, float gray = 0f) =>
        _current.Append(CultureInfo.InvariantCulture, $"{gray:0.##} G {width:0.##} w {x1:0.##} {_height - y1:0.##} m {x2:0.##} {_height - y2:0.##} l S\n");

    public void Rect(float x, float y, float w, float h, float fillGray) =>
        _current.Append(CultureInfo.InvariantCulture, $"{fillGray:0.##} g {x:0.##} {_height - y - h:0.##} {w:0.##} {h:0.##} re f 0 g\n");

    /// <summary>Approximate Helvetica width (average glyph ≈ 0.52em; bold slightly wider).</summary>
    public static float Measure(string text, float size, bool bold = false)
    {
        float units = 0;
        foreach (var c in text) units += c switch
        {
            'i' or 'l' or 'j' or 't' or 'f' or 'I' or '.' or ',' or ':' or ';' or '\'' or '|' or ' ' => 0.28f,
            'm' or 'w' or 'M' or 'W' => 0.83f,
            >= '0' and <= '9' => 0.556f,
            >= 'A' and <= 'Z' => 0.68f,
            _ => 0.54f,
        };
        return units * size * (bold ? 1.05f : 1f);
    }

    public byte[] ToBytes()
    {
        var objects = new List<string>();
        // 1: catalog, 2: pages, 3: F1, 4: F2, then page/content pairs
        var pageIds = new List<int>();
        var next = 5;
        foreach (var _ in _pages) { pageIds.Add(next); next += 2; }

        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {_pages.Count} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (var i = 0; i < _pages.Count; i++)
        {
            var contentId = pageIds[i] + 1;
            objects.Add(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {_width:0.##} {_height:0.##}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>"));
            var content = _pages[i].ToString();
            objects.Add($"<< /Length {Latin1.GetByteCount(content)} >>\nstream\n{content}endstream");
        }

        var sb = new StringBuilder("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<int>();
        foreach (var (obj, i) in objects.Select((o, i) => (o, i + 1)))
        {
            offsets.Add(Latin1.GetByteCount(sb.ToString()));
            sb.Append(CultureInfo.InvariantCulture, $"{i} 0 obj\n{obj}\nendobj\n");
        }
        var xref = Latin1.GetByteCount(sb.ToString());
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Latin1.GetBytes(sb.ToString());
    }

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '(': sb.Append("\\("); break;
                case ')': sb.Append("\\)"); break;
                case '…': sb.Append("\\205"); break; // WinAnsi ellipsis
                case '–': sb.Append("\\226"); break; // en dash
                case '—': sb.Append("\\227"); break; // em dash
                case '\u2019': sb.Append("\\222"); break; // right single quote
                case '\r' or '\n': sb.Append(' '); break;
                default: sb.Append(c < 256 ? c : '?'); break;
            }
        }
        return sb.ToString();
    }
}

public enum TextAlign { Left, Right, Center }
