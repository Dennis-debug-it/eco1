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
    private readonly List<PdfImage> _images = [];

    public PdfWriter(float width = A4Width, float height = A4Height)
    {
        _width = width; _height = height;
        _pages.Add(_current);
    }

    public float Width => _width;
    public float Height => _height;

    public void NewPage() { _current = new StringBuilder(); _pages.Add(_current); }

    public void Text(float x, float y, string text, float size = 10, bool bold = false, TextAlign align = TextAlign.Left, float? maxWidth = null, PdfColor? color = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        var w = Measure(text, size, bold);
        if (maxWidth is { } mw && w > mw)
        {
            while (text.Length > 1 && Measure(text + "…", size, bold) > mw) text = text[..^1];
            text += "…"; w = Measure(text, size, bold);
        }
        var tx = align switch { TextAlign.Right => x - w, TextAlign.Center => x - w / 2, _ => x };
        var fill = color is { } c ? string.Create(CultureInfo.InvariantCulture, $"{c.R:0.###} {c.G:0.###} {c.B:0.###} rg ") : "";
        _current.Append(CultureInfo.InvariantCulture, $"{fill}BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {tx:0.##} {_height - y:0.##} Td ({Escape(text)}) Tj ET{(fill.Length > 0 ? " 0 g" : "")}\n");
    }

    /// <summary>A coloured line or filled rectangle, e.g. the brand rule on a letterhead.</summary>
    public void Line(float x1, float y1, float x2, float y2, float width, PdfColor color) =>
        _current.Append(CultureInfo.InvariantCulture, $"{color.R:0.###} {color.G:0.###} {color.B:0.###} RG {width:0.##} w {x1:0.##} {_height - y1:0.##} m {x2:0.##} {_height - y2:0.##} l S 0 G\n");

    public void Rect(float x, float y, float w, float h, PdfColor fill) =>
        _current.Append(CultureInfo.InvariantCulture, $"{fill.R:0.###} {fill.G:0.###} {fill.B:0.###} rg {x:0.##} {_height - y - h:0.##} {w:0.##} {h:0.##} re f 0 g\n");

    /// <summary>Draws an image with its top-left corner at (x, y), scaled to w × h points.</summary>
    public void Image(PdfImage image, float x, float y, float w, float h)
    {
        var index = _images.IndexOf(image);
        if (index < 0) { _images.Add(image); index = _images.Count - 1; }
        _current.Append(CultureInfo.InvariantCulture, $"q {w:0.##} 0 0 {h:0.##} {x:0.##} {_height - y - h:0.##} cm /Im{index} Do Q\n");
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
        // Objects: 1 catalog, 2 pages, 3 F1, 4 F2, then images (each followed by its soft mask, if any), then page/content pairs.
        var objects = new List<(string Dict, byte[]? Stream)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("", null), // pages: filled in once page ids are known
            ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null),
            ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>", null),
        };
        var imageIds = new List<int>();
        foreach (var img in _images)
        {
            var id = objects.Count + 1;
            imageIds.Add(id);
            var smask = img.SoftMask is null ? "" : $" /SMask {id + 1} 0 R";
            objects.Add((ImageDict(img, smask), img.Data));
            if (img.SoftMask is { } m) objects.Add((ImageDict(m, ""), m.Data));
        }
        var xobjects = _images.Count == 0 ? "" : $" /XObject << {string.Join(' ', imageIds.Select((id, i) => $"/Im{i} {id} 0 R"))} >>";
        var pageIds = new List<int>();
        foreach (var page in _pages)
        {
            var pageId = objects.Count + 1;
            pageIds.Add(pageId);
            objects.Add((string.Create(CultureInfo.InvariantCulture, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {_width:0.##} {_height:0.##}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >>{xobjects} >> /Contents {pageId + 1} 0 R >>"), null));
            objects.Add(("", Latin1.GetBytes(page.ToString())));
        }
        objects[1] = ($"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {_pages.Count} >>", null);

        using var ms = new MemoryStream();
        void Write(string text) { var bytes = Latin1.GetBytes(text); ms.Write(bytes); }
        Write("%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            var (dict, stream) = objects[i];
            if (stream is null) Write($"{i + 1} 0 obj\n{dict}\nendobj\n");
            else
            {
                var header = dict.Length == 0 ? $"<< /Length {stream.Length} >>" : dict.Insert(dict.LastIndexOf(">>", StringComparison.Ordinal), $" /Length {stream.Length} ");
                Write($"{i + 1} 0 obj\n{header}\nstream\n");
                ms.Write(stream);
                Write("\nendstream\nendobj\n");
            }
        }
        var xref = ms.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write(string.Create(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n"));
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string ImageDict(PdfImage img, string smask) => string.Create(CultureInfo.InvariantCulture,
        $"<< /Type /XObject /Subtype /Image /Width {img.Width} /Height {img.Height} /ColorSpace /{img.ColorSpace} /BitsPerComponent 8 /Filter /{img.Filter}{smask} >>");

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
                case '\u2018': sb.Append("\\221"); break; // left single quote
                case '\u2019': sb.Append("\\222"); break; // right single quote
                case '\u201C': sb.Append("\\223"); break; // left double quote (text pasted from Word)
                case '\u201D': sb.Append("\\224"); break; // right double quote
                case '\u2022': sb.Append("\\225"); break; // bullet
                case '\r' or '\n': sb.Append(' '); break;
                default: sb.Append(c < 256 ? c : '?'); break;
            }
        }
        return sb.ToString();
    }
}

public enum TextAlign { Left, Right, Center }
