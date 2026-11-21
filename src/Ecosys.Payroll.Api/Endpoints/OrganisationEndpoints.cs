using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record GeneratePeriodsRequest(int Year, int PayDay = 28);

/// <summary>Builds the period calendar for a pay frequency. Weekly periods run Monday–Sunday and belong to the year in which they end.</summary>
public static class PeriodCalendar
{
    public static IEnumerable<(DateOnly Start, DateOnly End, DateOnly PayDate, string Name)> Generate(PayFrequency frequency, int year, int payDay)
    {
        switch (frequency)
        {
            case PayFrequency.Weekly:
                var monday = new DateOnly(year, 1, 1);
                while (monday.DayOfWeek != DayOfWeek.Monday) monday = monday.AddDays(-1);
                for (var w = 1; ; w++)
                {
                    var end = monday.AddDays(6);
                    if (end.Year > year) yield break;
                    if (end.Year == year) yield return (monday, end, end.AddDays(-2) /* Friday */, $"Week {w:D2} {year} ({monday:dd MMM}–{end:dd MMM})");
                    monday = monday.AddDays(7);
                }
            case PayFrequency.SemiMonthly:
                for (var m = 1; m <= 12; m++)
                {
                    var first = new DateOnly(year, m, 1);
                    var mid = new DateOnly(year, m, 15);
                    var last = first.AddMonths(1).AddDays(-1);
                    yield return (first, mid, mid, $"{first:MMMM yyyy} (1st half)");
                    yield return (mid.AddDays(1), last, last, $"{first:MMMM yyyy} (2nd half)");
                }
                yield break;
            default:
                for (var m = 1; m <= 12; m++)
                {
                    var start = new DateOnly(year, m, 1);
                    yield return (start, start.AddMonths(1).AddDays(-1), new DateOnly(year, m, Math.Min(payDay, DateTime.DaysInMonth(year, m))), start.ToString("MMMM yyyy"));
                }
                yield break;
        }
    }
}

public static class OrganisationEndpoints
{
    /// <summary>The employer PIN goes on every P10 return and NSSF/SHA schedule, so a typo here breaks every filing.</summary>
    private static void ValidateEntity(LegalEntity e)
    {
        e.TaxPin = KenyaIdentifiers.NormalisePin(e.TaxPin) ?? "";
        if (e.Jurisdiction == "KE" && !KenyaIdentifiers.IsValidPin(e.TaxPin, individual: false))
            throw new DomainException("Employer KRA PIN should be 11 characters: P (or A for a sole proprietor), nine digits and a letter, e.g. P051234567A.", "VALIDATION");
        if (!string.IsNullOrWhiteSpace(e.Phone))
            e.Phone = KenyaIdentifiers.NormalisePhone(e.Phone) ?? throw new DomainException($"Phone '{e.Phone}' isn't a valid number.", "VALIDATION");
    }

    public static void MapOrganisation(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/organisation").RequireAuthorization();

        g.MapGet("/legal-entities", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.LegalEntities.AsQueryable();
            if (ctx.LegalEntityIds.Count > 0) q = q.Where(e => ctx.LegalEntityIds.Contains(e.Id));
            return Results.Ok(await q.OrderBy(e => e.Name).ToListAsync(ct));
        });
        g.MapPost("/legal-entities", async (LegalEntity body, ITenantContext ctx, PayrollDbContext db, AuditService audit, Ecosys.Payroll.Infrastructure.Services.Saas.SubscriptionService plan, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            await plan.EnsureCanAddLegalEntityAsync(ct);
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant();
            ValidateEntity(body);
            db.LegalEntities.Add(body);
            audit.Record("legal_entity.created", nameof(LegalEntity), body.Id, new { body.Name, body.TaxPin });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/organisation/legal-entities/{body.Id}", body);
        });
        g.MapPut("/legal-entities/{id:guid}", async (Guid id, LegalEntity body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage); ctx.RequireLegalEntity(id);
            var e = await db.LegalEntities.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(LegalEntity), id);
            ValidateEntity(body);
            e.Name = body.Name; e.RegistrationNumber = body.RegistrationNumber; e.TaxPin = body.TaxPin; e.NssfEmployerNumber = body.NssfEmployerNumber; e.ShifEmployerNumber = body.ShifEmployerNumber;
            e.NitaRegistrationNumber = body.NitaRegistrationNumber; e.Address = body.Address; e.Email = body.Email; e.Phone = body.Phone; e.CurrencyCode = body.CurrencyCode;
            e.FinancialYearStartMonth = body.FinancialYearStartMonth; e.IsActive = body.IsActive; e.Settings = body.Settings;
            audit.Record("legal_entity.updated", nameof(LegalEntity), id, new { e.Name, e.Settings });
            await db.SaveChangesAsync(ct);
            return Results.Ok(e);
        });

        Crud<Branch>(g, "branches", (e, b) => { e.Code = b.Code; e.Name = b.Name; e.Location = b.Location; e.IsActive = b.IsActive; });
        Crud<Department>(g, "departments", (e, b) => { e.Code = b.Code; e.Name = b.Name; e.ParentDepartmentId = b.ParentDepartmentId; e.ManagerEmployeeId = b.ManagerEmployeeId; e.DefaultCostCentreId = b.DefaultCostCentreId; e.IsActive = b.IsActive; });
        Crud<CostCentre>(g, "cost-centres", (e, b) => { e.Code = b.Code; e.Name = b.Name; e.GlSegment = b.GlSegment; e.IsActive = b.IsActive; });
        Crud<PayrollGroup>(g, "payroll-groups", (e, b) => { e.Code = b.Code; e.Name = b.Name; e.Frequency = b.Frequency; e.PayDay = b.PayDay; e.Description = b.Description; e.IsActive = b.IsActive; });

        g.MapGet("/payroll-groups/{groupId:guid}/periods", async (Guid groupId, PayrollDbContext db, CancellationToken ct) =>
            Results.Ok(await db.PayrollPeriods.Where(p => p.PayrollGroupId == groupId).OrderByDescending(p => p.PeriodStart).ToListAsync(ct)));

        g.MapPost("/payroll-groups/{groupId:guid}/periods/generate", async (Guid groupId, GeneratePeriodsRequest req, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var group = await db.PayrollGroups.FirstOrDefaultAsync(x => x.Id == groupId, ct) ?? throw new NotFoundException(nameof(PayrollGroup), groupId);
            ctx.RequireLegalEntity(group.LegalEntityId);
            var existing = await db.PayrollPeriods.Where(p => p.PayrollGroupId == groupId && p.PeriodEnd.Year == req.Year).Select(p => p.PeriodStart).ToHashSetAsync(ct);
            var created = 0;
            foreach (var (start, end, payDate, name) in PeriodCalendar.Generate(group.Frequency, req.Year, req.PayDay))
            {
                if (existing.Contains(start)) continue;
                db.PayrollPeriods.Add(new PayrollPeriod { LegalEntityId = group.LegalEntityId, PayrollGroupId = groupId, Name = name, PeriodStart = start, PeriodEnd = end, PayDate = payDate });
                created++;
            }
            audit.Record("payroll_periods.generated", nameof(PayrollGroup), groupId, new { req.Year, created });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { created });
        });
    }

    /// <summary>Standard list/create/update/delete for legal-entity-scoped lookup entities.</summary>
    private static void Crud<T>(RouteGroupBuilder g, string path, Action<T, T> apply) where T : LegalEntityScopedEntity
    {
        g.MapGet($"/{path}", async (Guid? legalEntityId, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.Set<T>().AsQueryable();
            if (legalEntityId is { } le) q = q.Where(x => x.LegalEntityId == le);
            if (ctx.LegalEntityIds.Count > 0) q = q.Where(x => ctx.LegalEntityIds.Contains(x.LegalEntityId));
            return Results.Ok(await q.OrderBy(x => x.CreatedAt).ToListAsync(ct));
        });
        g.MapPost($"/{path}", async (T body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage); ctx.RequireLegalEntity(body.LegalEntityId);
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant();
            db.Set<T>().Add(body);
            audit.Record($"{typeof(T).Name.ToLowerInvariant()}.created", typeof(T).Name, body.Id, null, body.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/organisation/{path}/{body.Id}", body);
        });
        g.MapPut($"/{path}/{{id:guid}}", async (Guid id, T body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var e = await db.Set<T>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(typeof(T).Name, id);
            ctx.RequireLegalEntity(e.LegalEntityId);
            apply(e, body);
            audit.Record($"{typeof(T).Name.ToLowerInvariant()}.updated", typeof(T).Name, id, null, e.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(e);
        });
        g.MapDelete($"/{path}/{{id:guid}}", async (Guid id, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var e = await db.Set<T>().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(typeof(T).Name, id);
            ctx.RequireLegalEntity(e.LegalEntityId);
            db.Set<T>().Remove(e); // soft delete
            audit.Record($"{typeof(T).Name.ToLowerInvariant()}.deleted", typeof(T).Name, id, null, e.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
