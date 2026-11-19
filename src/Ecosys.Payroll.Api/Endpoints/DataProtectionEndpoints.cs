using System.Text.Json;
using System.Text.Json.Serialization;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record BreachRequest(DateTimeOffset DetectedAt, DateTimeOffset? OccurredAt, string Description, List<string>? DataCategories, int? SubjectsAffected, BreachRisk Risk,
    string? Cause, string? Containment, DateTimeOffset? ReportedToCommissionerAt, string? CommissionerReference, string? NotReportedReason,
    DateTimeOffset? SubjectsNotifiedAt, string? SubjectsNotNotifiedReason, BreachStatus Status);

/// <summary>Data Protection Act 2019: subject access (s.26), retention (s.39) and the breach register (s.43).</summary>
public static class DataProtectionEndpoints
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    public static void MapDataProtection(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/data-protection").RequireAuthorization();

        // ---- s.26 access: HR/DPO on request, or the employee themselves from self-service ----
        g.MapGet("/employees/{id:guid}/export", async (Guid id, DataProtectionService svc, PayrollDbContext db, CancellationToken ct) => await Export(id, svc, db, ct));
        api.MapGet("/self-service/data-export", async (ITenantContext ctx, DataProtectionService svc, PayrollDbContext db, CancellationToken ct) =>
            ctx.EmployeeId is { } me ? await Export(me, svc, db, ct) : Results.Problem("Your sign-in isn't linked to an employee record.", statusCode: 400)).RequireAuthorization();

        // ---- s.39 retention ----
        g.MapGet("/retention", async (DataProtectionService svc, CancellationToken ct) =>
        {
            var (employees, applicants) = await svc.DueAsync(ct);
            return Results.Ok(new { svc.EmployeeRetentionYears, svc.ApplicantMonths, svc.ApplicantConsentMonths, employees, applicants });
        });
        g.MapPost("/employees/{id:guid}/anonymise", async (Guid id, Reason r, DataProtectionService svc, CancellationToken ct) =>
        {
            await svc.AnonymiseEmployeeAsync(id, r.Text, ct);
            return Results.NoContent();
        });
        g.MapPost("/applicants/purge", async (DataProtectionService svc, CancellationToken ct) => Results.Ok(new { removed = await svc.PurgeApplicantsAsync(ct) }));

        // ---- s.43 breach register ----
        g.MapGet("/breaches", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var now = DateTimeOffset.UtcNow;
            var list = await db.DataBreaches.OrderByDescending(b => b.DetectedAt).ToListAsync(ct);
            return Results.Ok(list.Select(b => new
            {
                breach = b,
                b.CommissionerDeadline,
                commissionerOverdue = b.ReportedToCommissionerAt is null && b.NotReportedReason is null && now > b.CommissionerDeadline,
                hoursLeft = b.ReportedToCommissionerAt is null && b.NotReportedReason is null ? Math.Round((b.CommissionerDeadline - now).TotalHours, 1) : (double?)null,
            }));
        });
        g.MapPost("/breaches", async (BreachRequest req, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var b = new DataBreach { Description = "" };
            Apply(b, req);
            var year = BusinessClock.ToLocal(b.DetectedAt).Year;
            var n = await db.DataBreaches.CountAsync(x => x.Reference.StartsWith($"BR-{year}-"), ct) + 1;
            b.Reference = $"BR-{year}-{n:D3}";
            db.DataBreaches.Add(b);
            audit.Record("dpa.breach.recorded", nameof(DataBreach), b.Id, new { b.Reference, b.Risk, b.SubjectsAffected, b.DataCategories });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/data-protection/breaches/{b.Id}", b);
        });
        g.MapPut("/breaches/{id:guid}", async (Guid id, BreachRequest req, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var b = await db.DataBreaches.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(DataBreach), id);
            if (b.Status == BreachStatus.Closed) throw new InvalidStateException("A closed breach record can't be changed. Record a new entry if something new comes to light.");
            Apply(b, req);
            audit.Record("dpa.breach.updated", nameof(DataBreach), b.Id, new { b.Reference, b.Status, reported = b.ReportedToCommissionerAt, notified = b.SubjectsNotifiedAt });
            await db.SaveChangesAsync(ct);
            return Results.Ok(b);
        });
    }

    private static async Task<IResult> Export(Guid id, DataProtectionService svc, PayrollDbContext db, CancellationToken ct)
    {
        var data = await svc.ExportAsync(id, ct);
        var number = await db.Employees.Where(e => e.Id == id).Select(e => e.EmployeeNumber).FirstAsync(ct);
        return Results.File(JsonSerializer.SerializeToUtf8Bytes(data, ExportJson), "application/json", $"personal-data-{number}-{BusinessClock.Today():yyyyMMdd}.json");
    }

    private static void Apply(DataBreach b, BreachRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Description)) throw new DomainException("Describe what happened.", "VALIDATION");
        if (r.DetectedAt > DateTimeOffset.UtcNow.AddMinutes(5)) throw new DomainException("The detection time can't be in the future.", "VALIDATION");
        if (r.Status == BreachStatus.Closed)
        {
            if (r.ReportedToCommissionerAt is null && string.IsNullOrWhiteSpace(r.NotReportedReason))
                throw new DomainException("Before closing, record when the Data Commissioner was notified, or why notification wasn't required.", "VALIDATION");
            if (r.SubjectsNotifiedAt is null && string.IsNullOrWhiteSpace(r.SubjectsNotNotifiedReason))
                throw new DomainException("Before closing, record when affected people were told, or why they weren't (s.43(6)).", "VALIDATION");
        }
        b.DetectedAt = r.DetectedAt; b.OccurredAt = r.OccurredAt; b.Description = r.Description.Trim(); b.DataCategories = r.DataCategories ?? [];
        b.SubjectsAffected = r.SubjectsAffected; b.Risk = r.Risk; b.Cause = r.Cause; b.Containment = r.Containment;
        b.ReportedToCommissionerAt = r.ReportedToCommissionerAt; b.CommissionerReference = r.CommissionerReference; b.NotReportedReason = r.NotReportedReason;
        b.SubjectsNotifiedAt = r.SubjectsNotifiedAt; b.SubjectsNotNotifiedReason = r.SubjectsNotNotifiedReason;
        if (r.Status == BreachStatus.Closed && b.Status != BreachStatus.Closed) b.ClosedAt = DateTimeOffset.UtcNow;
        b.Status = r.Status;
    }
}
