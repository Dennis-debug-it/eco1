using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services.Hr;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record VariationRequest(DateOnly? EffectiveDate);
public sealed record CancelContractBody(string? Reason);

/// <summary>Contracts of service and variation letters: templates, drafting, issue for printing, signed-copy upload.</summary>
public static class ContractEndpoints
{
    private static async Task<object> View(ContractDocument d, ContractService svc, CancellationToken ct)
    {
        var (title, body, problems) = await svc.PreviewAsync(d, ct);
        return new
        {
            d.Id, d.EmployeeId, kind = d.Kind.ToString(), d.Reference, status = d.Status.ToString(), d.Title, d.Body, d.SignatoryName, d.SignatoryTitle, d.StartDate, d.EndDate,
            d.IssuedAt, d.IssuedBy, d.IssuedFileId, d.SignedOn, d.SignedFileId, d.CancelReason, d.CreatedAt,
            changes = d.Kind == ContractKind.VariationLetter ? ContractService.Changes(d.PreviousTerms, d.Terms) : [],
            preview = new { title, body, problems },
        };
    }

    public static void MapContracts(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization();

        g.MapGet("/contract-templates/{legalEntityId:guid}/{kind}", async (Guid legalEntityId, ContractKind kind, ContractService svc, CancellationToken ct) =>
        {
            var t = await svc.TemplateAsync(legalEntityId, kind, ct);
            return Results.Ok(new
            {
                template = t, placeholders = ContractService.Placeholders.Select(p => new { key = p.Key, meaning = p.Meaning }),
                standardTitle = ContractService.DefaultTitle(kind), standardBody = ContractService.DefaultBody(kind),
            });
        });
        g.MapPut("/contract-templates/{legalEntityId:guid}/{kind}", async (Guid legalEntityId, ContractKind kind, ContractTemplateRequest body, ContractService svc, CancellationToken ct) =>
            Results.Ok(await svc.SaveTemplateAsync(legalEntityId, kind, body, ct)));

        g.MapGet("/employees/{id:guid}/contracts", async (Guid id, ContractService svc, CancellationToken ct) =>
        {
            var list = new List<object>();
            foreach (var d in await svc.ForEmployeeAsync(id, ct)) list.Add(await View(d, svc, ct));
            return Results.Ok(list);
        });
        g.MapPost("/employees/{id:guid}/contracts", async (Guid id, ContractDraftRequest body, ContractService svc, CancellationToken ct) =>
            Results.Ok(await View(await svc.DraftContractAsync(id, body, ct), svc, ct)));
        g.MapPost("/employees/{id:guid}/variations", async (Guid id, VariationRequest body, ContractService svc, CancellationToken ct) =>
            Results.Ok(await View(await svc.DraftVariationAsync(id, body.EffectiveDate, ct), svc, ct)));

        g.MapPut("/contracts/{id:guid}", async (Guid id, ContractDraftEdit body, ContractService svc, CancellationToken ct) =>
            Results.Ok(await View(await svc.UpdateDraftAsync(id, body, ct), svc, ct)));
        g.MapGet("/contracts/{id:guid}/pdf", async (Guid id, PayrollDbContext db, ContractService svc, CancellationToken ct) =>
        {
            var d = await svc.GetAsync(id, ct); // checks access and company scope
            // Issued documents are served exactly as printed; drafts are rendered fresh.
            if (d.IssuedFileId is { } fid && await db.StoredFiles.FirstOrDefaultAsync(f => f.Id == fid, ct) is { } f) return Results.File(f.Content, "application/pdf", f.FileName);
            return Results.File(await svc.PdfAsync(d, ct), "application/pdf", $"{d.Reference}-draft.pdf");
        });
        g.MapPost("/contracts/{id:guid}/issue", async (Guid id, ContractService svc, CancellationToken ct) =>
            Results.Ok(await View(await svc.IssueAsync(id, ct), svc, ct)));
        g.MapPost("/contracts/{id:guid}/signed", async (Guid id, HttpRequest http, ContractService svc, CancellationToken ct) =>
        {
            var form = await http.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw new DomainException("Choose the scanned, signed copy.", "VALIDATION");
            if (!DateOnly.TryParse(form["signedOn"].ToString(), out var signedOn)) throw new DomainException("Enter the date it was signed.", "VALIDATION");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            return Results.Ok(await View(await svc.UploadSignedAsync(id, file.FileName, ms.ToArray(), signedOn, ct), svc, ct));
        }).DisableAntiforgery();
        g.MapPost("/contracts/{id:guid}/cancel", async (Guid id, CancelContractBody body, ContractService svc, CancellationToken ct) =>
            Results.Ok(await View(await svc.CancelAsync(id, body.Reason ?? "", ct), svc, ct)));
    }
}
