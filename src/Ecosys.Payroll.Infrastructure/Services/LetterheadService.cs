using System.Text.RegularExpressions;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services.Hr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ecosys.Payroll.Infrastructure.Services;

public sealed record LetterheadRequest(LetterheadLayout Layout, string? BrandColour, string? Tagline, string? Website, string? PostalAddress, string? FooterText, bool ShowRegistrationDetails = true);

/// <summary>
/// Company letterheads: settings and images (logo, or full header artwork) used on letters and payslips. Images are
/// kept small because they are embedded in every document; decoded images are cached briefly.
/// </summary>
public sealed partial class LetterheadService(PayrollDbContext db, ITenantContext tenant, FileStore files, AuditService audit, IMemoryCache cache)
{
    public const int MaxImageBytes = 1024 * 1024;
    public static readonly string[] Kinds = ["logo", "header"];

    public async Task<Letterhead> GetAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        tenant.Require(Permissions.OrganisationManage); tenant.RequireLegalEntity(legalEntityId);
        if (!await db.LegalEntities.AnyAsync(e => e.Id == legalEntityId, ct)) throw new NotFoundException(nameof(LegalEntity), legalEntityId);
        return await db.Letterheads.FirstOrDefaultAsync(l => l.LegalEntityId == legalEntityId, ct) ?? new Letterhead { LegalEntityId = legalEntityId };
    }

    public async Task<Letterhead> SaveAsync(Guid legalEntityId, LetterheadRequest r, CancellationToken ct = default)
    {
        var l = await TrackedAsync(legalEntityId, ct);
        var colour = string.IsNullOrWhiteSpace(r.BrandColour) ? null : r.BrandColour.Trim().ToLowerInvariant();
        if (colour is not null && !ColourRx().IsMatch(colour)) throw new DomainException("Brand colour should look like #0B5D4F.", "VALIDATION");
        if (r.Layout == LetterheadLayout.HeaderImage && l.HeaderImageFileId is null) throw new DomainException("Upload the header image first.", "VALIDATION");
        static string? Clean(string? v, int max, string what) =>
            string.IsNullOrWhiteSpace(v) ? null : v.Trim().Length <= max ? v.Trim() : throw new DomainException($"{what} is limited to {max} characters.", "VALIDATION");
        l.Layout = r.Layout; l.BrandColour = colour; l.Tagline = Clean(r.Tagline, 120, "The tagline"); l.Website = Clean(r.Website, 100, "The website");
        l.PostalAddress = Clean(r.PostalAddress, 120, "The postal address"); l.FooterText = Clean(r.FooterText, 250, "The footer"); l.ShowRegistrationDetails = r.ShowRegistrationDetails;
        audit.Record("letterhead.updated", nameof(Letterhead), l.Id, new { l.Layout, l.BrandColour }, legalEntityId);
        await db.SaveChangesAsync(ct);
        return l;
    }

    /// <summary>Stores a logo or header image after checking it can be printed. Uploading a header switches to the header layout.</summary>
    public async Task<Letterhead> UploadAsync(Guid legalEntityId, string kind, string fileName, byte[] content, CancellationToken ct = default)
    {
        if (!Kinds.Contains(kind)) throw new DomainException("Upload a logo or a header image.", "VALIDATION");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg")) throw new DomainException("Use a PNG or JPG image.", "VALIDATION");
        if (content.Length > MaxImageBytes) throw new DomainException($"The image is {content.Length / 1024} KB. Keep it under {MaxImageBytes / 1024} KB: it is included in every letter and payslip.", "VALIDATION");
        PdfImage image;
        try { image = PdfImage.Load(content); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IndexOutOfRangeException or ArgumentException) { throw new DomainException(ex is NotSupportedException ? ex.Message : "This image could not be read. Save it again as PNG or JPG.", "VALIDATION"); }
        if (kind == "header" && image.Aspect < 2.5f)
            throw new DomainException($"A header image should be a wide banner (at least 2.5 times as wide as it is tall); this one is {image.Width}×{image.Height}. For a square or tall logo, upload it as the logo instead.", "VALIDATION");
        if (kind == "header" && image.Width < 1000) throw new DomainException($"The header image is only {image.Width} pixels wide and will print blurred. Use one at least 1000 pixels wide.", "VALIDATION");

        var l = await TrackedAsync(legalEntityId, ct);
        var stored = await files.SaveAsync(fileName, content, nameof(Letterhead), l.Id, null, ct);
        if (kind == "logo") l.LogoFileId = stored.Id; else { l.HeaderImageFileId = stored.Id; l.Layout = LetterheadLayout.HeaderImage; }
        audit.Record($"letterhead.{kind}.uploaded", nameof(Letterhead), l.Id, new { stored.FileName, image.Width, image.Height }, legalEntityId);
        await db.SaveChangesAsync(ct);
        return l;
    }

    public async Task<Letterhead> RemoveAsync(Guid legalEntityId, string kind, CancellationToken ct = default)
    {
        var l = await TrackedAsync(legalEntityId, ct);
        if (kind == "logo") l.LogoFileId = null;
        else if (kind == "header") { l.HeaderImageFileId = null; l.Layout = LetterheadLayout.Standard; }
        else throw new DomainException("Unknown image.", "VALIDATION");
        audit.Record($"letterhead.{kind}.removed", nameof(Letterhead), l.Id, null, legalEntityId);
        await db.SaveChangesAsync(ct);
        return l;
    }

    public async Task<(byte[] Content, string ContentType, string FileName)?> ImageAsync(Guid legalEntityId, string kind, CancellationToken ct = default)
    {
        var l = await GetAsync(legalEntityId, ct);
        var id = kind == "logo" ? l.LogoFileId : kind == "header" ? l.HeaderImageFileId : null;
        if (id is null) return null;
        var f = await db.StoredFiles.FirstOrDefaultAsync(x => x.Id == id, ct);
        return f is null ? null : (f.Content, f.ContentType, f.FileName);
    }

    /// <summary>The letterhead ready to draw, for any document of this legal entity. Plain text if none is set up.</summary>
    public async Task<Brand> BrandAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        var l = await db.Letterheads.AsNoTracking().FirstOrDefaultAsync(x => x.LegalEntityId == legalEntityId, ct);
        if (l is null) return Brand.Plain;
        var logo = await DecodedAsync(l.LogoFileId, ct);
        var header = await DecodedAsync(l.HeaderImageFileId, ct);
        var layout = l.Layout == LetterheadLayout.HeaderImage && header is not null ? LetterheadLayout.HeaderImage : LetterheadLayout.Standard;
        return new Brand(layout, logo, header, PdfColor.FromHex(l.BrandColour), l.Tagline, l.Website, l.PostalAddress, l.FooterText, l.ShowRegistrationDetails);
    }

    private async Task<PdfImage?> DecodedAsync(Guid? fileId, CancellationToken ct)
    {
        if (fileId is not { } id) return null;
        if (cache.TryGetValue($"pdfimg:{id}", out PdfImage? hit)) return hit;
        var bytes = await db.StoredFiles.Where(f => f.Id == id).Select(f => f.Content).FirstOrDefaultAsync(ct);
        PdfImage? img = null;
        if (bytes is not null) { try { img = PdfImage.Load(bytes); } catch (Exception) { img = null; } } // never let a bad image stop a payslip
        cache.Set($"pdfimg:{id}", img, TimeSpan.FromMinutes(10));
        return img;
    }

    private async Task<Letterhead> TrackedAsync(Guid legalEntityId, CancellationToken ct)
    {
        var l = await GetAsync(legalEntityId, ct);
        if (db.Entry(l).State == EntityState.Detached) { db.Letterheads.Add(l); }
        return l;
    }

    [GeneratedRegex("^#[0-9a-f]{6}$")] private static partial Regex ColourRx();
}
