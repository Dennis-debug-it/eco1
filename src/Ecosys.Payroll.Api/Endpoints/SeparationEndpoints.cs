using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Ecosys.Payroll.Infrastructure.Services.Hr;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record ChecklistUpdate(string Key, bool Done, string? Note);
public sealed record ExitInterviewBody(string Notes, bool EligibleForRehire);

public static class SeparationEndpoints
{
    public static void MapSeparations(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/separations").RequireAuthorization();

        g.MapGet("", async (SeparationStatus? status, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            if (!ctx.Has(Permissions.HrSeparations) && !ctx.Has(Permissions.PayrollPrepare)) ctx.Require(Permissions.PayrollApprove);
            var q = db.Separations.AsQueryable();
            if (status is { } s) q = q.Where(x => x.Status == s);
            var rows = await q.OrderByDescending(x => x.CreatedAt).Join(db.Employees, s => s.EmployeeId, e => e.Id, (s, e) => new { s, e }).ToListAsync(ct);
            return Results.Ok(rows.Select(x => new
            {
                x.s.Id, x.s.EmployeeId, x.e.EmployeeNumber, employee = x.e.FullName, x.e.JobTitle, x.s.Type, x.s.NoticeDate, x.s.LastWorkingDay, x.s.Status,
                checklistDone = x.s.Checklist.Count(i => i.Done), checklistTotal = x.s.Checklist.Count, x.s.CreatedAt,
            }));
        });

        g.MapGet("/{id:guid}", async (Guid id, ITenantContext ctx, PayrollDbContext db, SeparationService svc, CancellationToken ct) =>
        {
            if (!ctx.Has(Permissions.HrSeparations) && !ctx.Has(Permissions.PayrollPrepare)) ctx.Require(Permissions.PayrollApprove);
            var s = await db.Separations.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Separation), id);
            var e = await db.Employees.FirstAsync(x => x.Id == s.EmployeeId, ct);
            var items = await db.ScheduledPayItems.Where(i => i.SourceType == nameof(Separation) && i.SourceId == id).ToListAsync(ct);
            var seePay = ctx.Has(Permissions.SalaryRead);
            if (!seePay) { db.Entry(s).State = EntityState.Detached; s.Dues = null; } // HR without salary visibility manages the exit but not the figures
            return Results.Ok(new
            {
                separation = s, duesHidden = !seePay, employee = new { e.Id, e.EmployeeNumber, name = e.FullName, e.JobTitle, e.HireDate, e.NoticePeriodDays, e.Status, e.TerminationDate },
                payItems = seePay ? items.Select(i => new { i.Id, i.Description, i.Amount, i.Status, i.DueDate, i.ProcessedInRunId }).ToList<object>() : [],
                canApprove = ctx.Has(Permissions.PayrollApprove) && (s.InitiatedBy != ctx.UserId || ctx.IsSystem || await svc.OnePersonPayrollAsync(s.LegalEntityId, ct)),
            });
        });

        g.MapPost("", async (SeparationRequest body, SeparationService svc, CancellationToken ct) =>
        {
            var s = await svc.InitiateAsync(body, ct);
            return Results.Created($"/api/payroll/separations/{s.Id}", s);
        });
        g.MapPost("/{id:guid}/checklist", async (Guid id, ChecklistUpdate body, SeparationService svc, CancellationToken ct) => Results.Ok(await svc.UpdateChecklistAsync(id, body.Key, body.Done, body.Note, ct)));
        g.MapPost("/{id:guid}/exit-interview", async (Guid id, ExitInterviewBody body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrSeparations);
            var s = await db.Separations.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Separation), id);
            s.ExitInterviewNotes = body.Notes; s.EligibleForRehire = body.EligibleForRehire;
            audit.Record("separation.exit_interview", nameof(Separation), id, new { body.EligibleForRehire }, s.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(s);
        });
        g.MapPost("/{id:guid}/calculate", async (Guid id, SeparationService svc, CancellationToken ct) => Results.Ok(await svc.CalculateAsync(id, ct)));
        g.MapPost("/{id:guid}/approve", async (Guid id, SeparationService svc, CancellationToken ct) => Results.Ok(await svc.ApproveAsync(id, ct)));
        g.MapPost("/{id:guid}/complete", async (Guid id, SeparationService svc, CancellationToken ct) => Results.Ok(await svc.CompleteAsync(id, ct)));
        g.MapPost("/{id:guid}/cancel", async (Guid id, Reason body, SeparationService svc, CancellationToken ct) => Results.Ok(await svc.CancelAsync(id, body.Text, ct)));

        g.MapGet("/{id:guid}/statement.pdf", async (Guid id, ITenantContext ctx, PayrollDbContext db, AuditService audit, LetterheadService letterheads, CancellationToken ct) =>
        {
            if (!ctx.Has(Permissions.HrSeparations) && !ctx.Has(Permissions.PayrollPrepare)) ctx.Require(Permissions.PayrollApprove);
            ctx.Require(Permissions.SalaryRead);
            var s = await db.Separations.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Separation), id);
            var e = await db.Employees.FirstAsync(x => x.Id == s.EmployeeId, ct);
            var le = await db.LegalEntities.FirstAsync(x => x.Id == s.LegalEntityId, ct);
            audit.Record("separation.statement.downloaded", nameof(Separation), id, null, s.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.File(HrDocumentRenderer.FinalDuesStatement(le, e, s, le.CurrencyCode, await letterheads.BrandAsync(le.Id, ct)), "application/pdf", $"final-dues-{e.EmployeeNumber}.pdf");
        });

        // Certificate of service: HR, or the former employee themselves once the exit is approved.
        api.MapGet("/employees/{employeeId:guid}/certificate-of-service.pdf", async (Guid employeeId, ITenantContext ctx, PayrollDbContext db, AuditService audit, LetterheadService letterheads, CancellationToken ct) =>
        {
            if (ctx.EmployeeId != employeeId) ctx.Require(Permissions.HrSeparations);
            var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == employeeId, ct) ?? throw new NotFoundException("Employee", employeeId);
            if (e.TerminationDate is null) throw new InvalidStateException("A certificate of service is issued when employment ends.");
            var le = await db.LegalEntities.FirstAsync(x => x.Id == e.LegalEntityId, ct);
            var history = await db.EmploymentRecords.Where(r => r.EmployeeId == employeeId && r.EffectiveFrom <= e.TerminationDate).OrderBy(r => r.EffectiveFrom)
                .Select(r => new { r.EffectiveFrom, r.EffectiveTo, r.JobTitle }).ToListAsync(ct);
            // Collapse consecutive records with the same title.
            var collapsed = new List<(DateOnly, DateOnly?, string?)>();
            foreach (var h in history)
            {
                if (collapsed.Count > 0 && collapsed[^1].Item3 == h.JobTitle) collapsed[^1] = (collapsed[^1].Item1, h.EffectiveTo, h.JobTitle);
                else collapsed.Add((h.EffectiveFrom, h.EffectiveTo, h.JobTitle));
            }
            if (collapsed.Count == 0) collapsed.Add((e.HireDate, e.TerminationDate, e.JobTitle));
            var signatory = ctx.EmployeeId == employeeId ? null : ctx.UserName;
            audit.Record("certificate_of_service.issued", "Employee", employeeId, new { self = ctx.EmployeeId == employeeId }, e.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.File(HrDocumentRenderer.CertificateOfService(le, e, collapsed, BusinessClock.Today(), signatory, await letterheads.BrandAsync(le.Id, ct)), "application/pdf", $"certificate-of-service-{e.EmployeeNumber}.pdf");
        }).RequireAuthorization();
    }
}
