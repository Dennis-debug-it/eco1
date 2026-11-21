using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Services.Hr;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record OfferResponseBody(bool Accept, string? FullName, string? Reason);

/// <summary>
/// The candidate's side of an offer, reached through the private link in their email. No sign-in: the link's token is
/// the credential (stored hashed, single offer, lapses after the respond-by date, cancelled if the offer is withdrawn).
/// </summary>
public static class OfferEndpoints
{
    public const string OfferRateLimit = "offer";

    public static void MapOffers(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/offers").AllowAnonymous().RequireRateLimiting(OfferRateLimit);

        g.MapGet("/{token}", async (string token, OfferLetterService svc, CancellationToken ct) =>
        {
            if (await svc.FindByTokenAsync(token, ct) is not { } found) return NotFound();
            var (l, app, company) = found;
            return Results.Ok(new
            {
                company, candidate = $"{app.FirstName} {app.LastName}", l.Reference, l.JobTitle, l.StartDate, l.AcceptBy, status = l.Status.ToString(), l.Subject,
                blocks = HrDocumentRenderer.Blocks(l.Body).Select(b => new { kind = b.Kind.ToString(), text = b.Text }),
                l.SignatoryName, l.SignatoryTitle, l.RespondedAt, l.ResponseName,
            });
        });

        g.MapGet("/{token}/pdf", async (string token, OfferLetterService svc, PayrollDbContext db, CancellationToken ct) =>
        {
            if (await svc.FindByTokenAsync(token, ct) is not { } found) return NotFound();
            var fileId = found.Letter.AcceptedFileId ?? found.Letter.SentFileId;
            var f = fileId is { } id ? await db.StoredFiles.FirstOrDefaultAsync(x => x.Id == id, ct) : null;
            return f is null ? NotFound() : Results.File(f.Content, "application/pdf", f.FileName);
        });

        g.MapPost("/{token}/respond", async (string token, OfferResponseBody body, HttpContext http, OfferLetterService svc, CancellationToken ct) =>
        {
            var l = await svc.RespondAsync(token, body.Accept, body.FullName, body.Reason, http.Connection.RemoteIpAddress?.ToString(), ct);
            return Results.Ok(new { status = l.Status.ToString(), l.RespondedAt });
        });
    }

    private static IResult NotFound() => Results.Json(new { title = "Offer not found", status = 404, code = "OFFER_NOT_FOUND",
        detail = "This link is not valid, or the offer has been withdrawn. Contact the employer if you think this is a mistake." }, statusCode: 404);
}
