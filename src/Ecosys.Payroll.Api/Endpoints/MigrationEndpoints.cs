using System.Text;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Migration;
using Ecosys.Payroll.Infrastructure.Services.Migration;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record GoLiveRequest(DateOnly FirstPeriodStart, bool AcceptOpenItems = false);

/// <summary>
/// Moving an organisation in from another payroll system: migration mode (nothing paid or filed until go-live is
/// signed), and the imports that load employees, opening balances and this year's pay to date, each checked in a dry
/// run before anything is saved and undoable afterwards.
/// </summary>
public static class MigrationEndpoints
{
    public static void MapMigration(this RouteGroupBuilder api)
    {
        var m = api.MapGroup("/migration").RequireAuthorization();
        m.MapGet("", async (MigrationModeService svc, CancellationToken ct) => Results.Ok(await svc.StatusAsync(ct)));
        m.MapPost("/start", async (MigrationModeService svc, CancellationToken ct) => Results.Ok(await svc.StartAsync(ct)));
        m.MapPost("/go-live", async (GoLiveRequest req, MigrationModeService svc, CancellationToken ct) => Results.Ok(await svc.GoLiveAsync(req.FirstPeriodStart, req.AcceptOpenItems, ct)));
        m.MapPost("/cancel", async (MigrationModeService svc, CancellationToken ct) => Results.Ok(await svc.CancelAsync(ct)));

        // Everything the organisation keeps here, as a zip: for a rollback after go-live, leaving, or a whole-organisation data request.
        api.MapGet("/export/company.zip", async (bool? payslips, CompanyExportService svc, CancellationToken ct) =>
        {
            var export = await svc.BuildAsync(payslips ?? true, ct);
            return Results.File(export.Zip, "application/zip", export.FileName);
        }).RequireAuthorization();

        var g = api.MapGroup("/imports").RequireAuthorization();
        g.MapGet("", async (Guid? legalEntityId, MigrationImportService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListAsync(legalEntityId, ct)).Select(b => new
            {
                b.Id, b.LegalEntityId, kind = b.Kind.ToString(), b.FileName, b.RowCount, status = b.Status.ToString(), b.CreatedAt, b.AppliedByName, b.UndoneAt, b.UndoneByName, b.Summary,
            })));

        g.MapGet("/{kind}/columns", (string kind) => Results.Ok(MigrationImportService.ColumnsFor(Kind(kind)).Select(c => new { c.Name, c.Description, c.Required, c.Example, c.Aliases })));

        g.MapGet("/{kind}/template.csv", async (string kind, Guid? legalEntityId, MigrationImportService svc, CancellationToken ct) =>
        {
            var k = Kind(kind);
            return Results.File(Encoding.UTF8.GetBytes(await svc.TemplateAsync(k, legalEntityId, ct)), "text/csv", $"ecosys-import-{Slug(k)}.csv");
        });

        g.MapPost("/{kind}/preview", async (string kind, HttpRequest http, MigrationImportService svc, CancellationToken ct) =>
        {
            var (le, name, bytes) = await ReadUploadAsync(http, ct);
            return Results.Ok(await svc.PreviewAsync(Kind(kind), le, name, bytes, ct));
        }).DisableAntiforgery();

        g.MapPost("/{kind}", async (string kind, HttpRequest http, MigrationImportService svc, CancellationToken ct) =>
        {
            var (le, name, bytes) = await ReadUploadAsync(http, ct);
            var outcome = await svc.ApplyAsync(Kind(kind), le, name, bytes, ct);
            if (outcome.Batch is not { } b)
                return Results.Json(new { title = "The file has problems", status = 422, code = "IMPORT_PROBLEMS", detail = "Nothing was imported. Fix the rows listed and upload the file again.", preview = outcome.Preview }, statusCode: 422);
            return Results.Ok(new { batch = new { b.Id, kind = b.Kind.ToString(), b.FileName, b.RowCount, status = b.Status.ToString(), b.CreatedAt, b.Summary }, preview = outcome.Preview });
        }).DisableAntiforgery();

        g.MapPost("/{id:guid}/undo", async (Guid id, MigrationImportService svc, CancellationToken ct) =>
        {
            var b = await svc.UndoAsync(id, ct);
            return Results.Ok(new { b.Id, status = b.Status.ToString(), b.UndoneAt, b.UndoneByName });
        });
    }

    private static ImportKind Kind(string s) => s.ToLowerInvariant() switch
    {
        "employees" => ImportKind.Employees,
        "opening-balances" => ImportKind.OpeningBalances,
        "year-to-date" => ImportKind.YearToDate,
        _ => throw new DomainException("Unknown import. Use employees, opening-balances or year-to-date.", "VALIDATION"),
    };

    private static string Slug(ImportKind k) => k switch { ImportKind.Employees => "employees", ImportKind.OpeningBalances => "opening-balances", _ => "year-to-date" };

    /// <summary>A multipart form: <c>file</c> plus <c>legalEntityId</c>.</summary>
    private static async Task<(Guid LegalEntityId, string FileName, byte[] Content)> ReadUploadAsync(HttpRequest http, CancellationToken ct)
    {
        if (!http.HasFormContentType) throw new DomainException("Upload the file as a form with a 'file' field.", "VALIDATION");
        var form = await http.ReadFormAsync(ct);
        if (!Guid.TryParse(form["legalEntityId"], out var le)) throw new DomainException("Choose the company to import into.", "VALIDATION");
        var file = form.Files.GetFile("file") ?? throw new DomainException("Choose a file to upload.", "VALIDATION");
        if (file.Length > TabularReader.MaxBytes) throw new DomainException("The file is larger than 5 MB. Split it into smaller files.", "VALIDATION");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return (le, file.FileName, ms.ToArray());
    }
}
