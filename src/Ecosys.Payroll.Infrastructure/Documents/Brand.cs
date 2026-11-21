using Ecosys.Payroll.Domain.Organisation;

namespace Ecosys.Payroll.Infrastructure.Documents;

/// <summary>A company's letterhead, ready to draw: settings plus decoded images.</summary>
public sealed record Brand(LetterheadLayout Layout, PdfImage? Logo, PdfImage? Header, PdfColor? Colour, string? Tagline, string? Website, string? PostalAddress, string? Footer, bool ShowRegistration)
{
    public static readonly Brand Plain = new(LetterheadLayout.Standard, null, null, null, null, null, null, null, true);

    /// <summary>The brand colour if dark enough to read as text on white, otherwise near-black.</summary>
    public PdfColor? TextColour => Colour is { } c && c.Luminance < 0.45f ? c : null;

    public const float Margin = 56;

    /// <summary>
    /// Draws the letterhead at the top of the current page and returns the y below it. Standard layout: logo on the
    /// left, company name and details right-aligned beside it, then a rule in the brand colour. Header-image layout:
    /// the artwork across the full text width.
    /// </summary>
    public float Draw(PdfWriter pdf, LegalEntity entity, float left = Margin, float right = PdfWriter.A4Width - Margin, float top = 40)
    {
        var rule = Colour ?? new PdfColor(0, 0, 0);
        if (Layout == LetterheadLayout.HeaderImage && Header is { } art)
        {
            var w = right - left;
            var h = Math.Min(w / art.Aspect, 130f);
            var drawW = h * art.Aspect;
            pdf.Image(art, left + (w - drawW) / 2, top, drawW, h);
            var yy = top + h + 8;
            pdf.Line(left, yy, right, yy, 1.2f, rule);
            return yy + 22;
        }

        var details = new List<string>();
        var contact = string.Join("  ·  ", new[] { entity.Address, PostalAddress }.Where(x => !string.IsNullOrWhiteSpace(x))!);
        if (contact.Length > 0) details.Add(contact);
        var reach = string.Join("  ·  ", new[] { entity.Phone, entity.Email, Website }.Where(x => !string.IsNullOrWhiteSpace(x))!);
        if (reach.Length > 0) details.Add(reach);
        if (ShowRegistration) details.Add($"PIN {entity.TaxPin}{(string.IsNullOrWhiteSpace(entity.RegistrationNumber) ? "" : $"   Reg. {entity.RegistrationNumber}")}");

        float y;
        if (Logo is { } logo)
        {
            // Logo box: up to 64 pt tall and 170 pt wide, kept in proportion.
            var h = Math.Min(64f, 170f / logo.Aspect);
            var w = h * logo.Aspect;
            pdf.Image(logo, left, top, w, h);
            var ty = top + 14;
            pdf.Text(right, ty, entity.Name, 14, bold: true, TextAlign.Right, maxWidth: right - left - w - 16, color: TextColour);
            ty += 12;
            if (!string.IsNullOrWhiteSpace(Tagline)) { pdf.Text(right, ty, Tagline, 8.5f, align: TextAlign.Right, maxWidth: right - left - w - 16); ty += 11; }
            foreach (var d in details) { pdf.Text(right, ty, d, 8, align: TextAlign.Right, maxWidth: right - left - w - 16); ty += 10; }
            y = Math.Max(top + h, ty - 4) + 8;
        }
        else
        {
            y = top + 16;
            pdf.Text(left, y, entity.Name, 15, bold: true, color: TextColour); y += 13;
            if (!string.IsNullOrWhiteSpace(Tagline)) { pdf.Text(left, y, Tagline, 8.5f); y += 11; }
            foreach (var d in details) { pdf.Text(left, y, d, 8.5f, maxWidth: right - left); y += 11; }
        }
        pdf.Line(left, y, right, y, 1.2f, rule);
        return y + 22;
    }

    /// <summary>The footer line, centred just above the page's bottom note.</summary>
    public void DrawFooter(PdfWriter pdf, float left = Margin, float right = PdfWriter.A4Width - Margin)
    {
        if (string.IsNullOrWhiteSpace(Footer)) return;
        var y = PdfWriter.A4Height - 54;
        if (Colour is { } c) pdf.Line(left, y - 10, right, y - 10, 0.6f, c);
        pdf.Text((left + right) / 2, y, Footer, 7.5f, align: TextAlign.Center, maxWidth: right - left);
    }
}
