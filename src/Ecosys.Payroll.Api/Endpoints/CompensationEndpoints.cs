using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public static class CompensationEndpoints
{
    public static void MapCompensation(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/compensation").RequireAuthorization();

        g.MapGet("/components", async (Guid? legalEntityId, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.SalaryComponents.AsQueryable();
            if (legalEntityId is { } le) q = q.Where(c => c.LegalEntityId == le);
            if (ctx.LegalEntityIds.Count > 0) q = q.Where(c => ctx.LegalEntityIds.Contains(c.LegalEntityId));
            return Results.Ok(await q.OrderBy(c => c.Type).ThenBy(c => c.SortOrder).ThenBy(c => c.Code).ToListAsync(ct));
        });
        g.MapPost("/components", async (SalaryComponent body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.CompensationManage); ctx.RequireLegalEntity(body.LegalEntityId);
            if (string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.Name)) throw new DomainException("Code and name are required.", "VALIDATION");
            body.Code = body.Code.Trim().ToUpperInvariant();
            if (await db.SalaryComponents.AnyAsync(c => c.LegalEntityId == body.LegalEntityId && c.Code == body.Code, ct)) throw new DomainException($"Component {body.Code} already exists.", "DUPLICATE");
            if (body.IsBasic && await db.SalaryComponents.AnyAsync(c => c.LegalEntityId == body.LegalEntityId && c.IsBasic && c.IsActive, ct)) throw new DomainException("Only one active basic-salary component is allowed.", "VALIDATION");
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant();
            db.SalaryComponents.Add(body);
            audit.Record("component.created", nameof(SalaryComponent), body.Id, new { body.Code, body.Type, body.Taxable, body.Pensionable }, body.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/compensation/components/{body.Id}", body);
        });
        g.MapPut("/components/{id:guid}", async (Guid id, SalaryComponent body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.CompensationManage);
            var c = await db.SalaryComponents.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(SalaryComponent), id);
            ctx.RequireLegalEntity(c.LegalEntityId);
            c.Name = body.Name; c.Method = body.Method; c.DefaultAmount = body.DefaultAmount; c.DefaultRate = body.DefaultRate; c.Taxable = body.Taxable; c.Pensionable = body.Pensionable;
            c.DeductionTreatment = body.DeductionTreatment; c.EmployerMatchRate = body.EmployerMatchRate; c.Recurrence = body.Recurrence; c.GlAccountCode = body.GlAccountCode;
            c.LiabilityGlAccountCode = body.LiabilityGlAccountCode; c.ShowOnPayslip = body.ShowOnPayslip; c.SortOrder = body.SortOrder; c.IsActive = body.IsActive; c.PayeReturnCategory = body.PayeReturnCategory;
            audit.Record("component.updated", nameof(SalaryComponent), id, new { c.Code, c.Taxable, c.Pensionable, c.IsActive, p10 = c.PayeReturnCategory.ToString() }, c.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(c);
        });

        g.MapGet("/salary-structures", async (Guid? legalEntityId, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.SalaryStructures.Include(s => s.Lines.OrderBy(l => l.SortOrder)).AsQueryable();
            if (legalEntityId is { } le) q = q.Where(c => c.LegalEntityId == le);
            if (ctx.LegalEntityIds.Count > 0) q = q.Where(c => ctx.LegalEntityIds.Contains(c.LegalEntityId));
            return Results.Ok(await q.OrderBy(s => s.Code).ToListAsync(ct));
        });
        g.MapPost("/salary-structures", async (SalaryStructure body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.CompensationManage); ctx.RequireLegalEntity(body.LegalEntityId);
            if (string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.Name)) throw new DomainException("Code and name are required.", "VALIDATION");
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant();
            var i = 0;
            foreach (var l in body.Lines) { l.Id = Guid.NewGuid(); l.TenantId = body.TenantId; l.SalaryStructureId = body.Id; l.SortOrder = ++i; }
            db.SalaryStructures.Add(body);
            audit.Record("structure.created", nameof(SalaryStructure), body.Id, new { body.Code, lines = body.Lines.Count }, body.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/compensation/salary-structures/{body.Id}", body);
        });
        g.MapPut("/salary-structures/{id:guid}", async (Guid id, SalaryStructure body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.CompensationManage);
            var s = await db.SalaryStructures.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(SalaryStructure), id);
            ctx.RequireLegalEntity(s.LegalEntityId);
            s.Name = body.Name; s.Description = body.Description; s.IsActive = body.IsActive;
            db.SalaryStructureLines.RemoveRange(s.Lines);
            s.Lines.Clear();
            var i = 0;
            foreach (var l in body.Lines) s.Lines.Add(new SalaryStructureLine { TenantId = s.TenantId, SalaryStructureId = s.Id, SalaryComponentId = l.SalaryComponentId, Method = l.Method, Amount = l.Amount, Rate = l.Rate, SortOrder = ++i });
            audit.Record("structure.updated", nameof(SalaryStructure), id, new { s.Code, lines = s.Lines.Count }, s.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(s);
        });
    }
}
