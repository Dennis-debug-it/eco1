using System.Security.Cryptography;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record LeaveRequestBody(Guid LeaveTypeId, DateOnly StartDate, DateOnly EndDate, decimal Days, string? Reason);
public sealed record LeaveDecision(string? Comment);

public static class SelfServiceEndpoints
{
    public static void MapSelfService(this RouteGroupBuilder api)
    {
        // ---- Payslips --------------------------------------------------------------------
        api.MapGet("/employees/{id:guid}/payslips", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            RequireSelfOrSalaryRead(ctx, id);
            var slips = await db.Payslips.Where(p => p.EmployeeId == id).OrderByDescending(p => p.PeriodEnd)
                .Join(db.PayrollRuns, p => p.PayrollRunId, r => r.Id, (p, r) => new { p.Id, p.PayslipNumber, p.PeriodStart, p.PeriodEnd, p.GrossPay, p.NetPay, p.IssuedAt, runReference = r.Reference, r.CurrencyCode })
                .ToListAsync(ct);
            return Results.Ok(slips);
        }).RequireAuthorization();

        api.MapGet("/payslips/{payslipId:guid}", async (Guid payslipId, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var slip = await db.Payslips.FirstOrDefaultAsync(p => p.Id == payslipId, ct) ?? throw new NotFoundException(nameof(Payslip), payslipId);
            RequireSelfOrSalaryRead(ctx, slip.EmployeeId);
            var result = await db.PayrollRunEmployees.Include(e => e.Lines.OrderBy(l => l.Sequence)).FirstAsync(e => e.Id == slip.PayrollRunEmployeeId, ct);
            return Results.Ok(new { slip, result });
        }).RequireAuthorization();

        api.MapGet("/payslips/{payslipId:guid}/pdf", async (Guid payslipId, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var slip = await db.Payslips.FirstOrDefaultAsync(p => p.Id == payslipId, ct) ?? throw new NotFoundException(nameof(Payslip), payslipId);
            RequireSelfOrSalaryRead(ctx, slip.EmployeeId);
            var run = await db.PayrollRuns.FirstAsync(r => r.Id == slip.PayrollRunId, ct);
            var result = await db.PayrollRunEmployees.Include(e => e.Lines).FirstAsync(e => e.Id == slip.PayrollRunEmployeeId, ct);
            var employee = await db.Employees.FirstAsync(e => e.Id == slip.EmployeeId, ct);
            var entity = await db.LegalEntities.FirstAsync(e => e.Id == run.LegalEntityId, ct);
            var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PayrollPeriodId, ct);
            var bank = await db.EmployeeBankAccounts.Where(b => b.EmployeeId == slip.EmployeeId).OrderByDescending(b => b.IsPrimary).FirstOrDefaultAsync(ct);
            var dept = employee.DepartmentId is { } d ? await db.Departments.Where(x => x.Id == d).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
            var stat = await db.EmployeeStatutoryDetails.FirstOrDefaultAsync(s => s.EmployeeId == slip.EmployeeId, ct);

            var pdf = PayslipRenderer.Render(entity, employee, result, run, period, slip, bank, dept, FieldEncryptor.Mask(stat?.KraPin));
            var hash = Convert.ToHexStringLower(SHA256.HashData(pdf));
            slip.ContentHash ??= hash;
            if (ctx.EmployeeId == slip.EmployeeId) slip.FirstViewedAt ??= DateTimeOffset.UtcNow;
            audit.Record("payslip.downloaded", nameof(Payslip), slip.Id, new { slip.PayslipNumber, self = ctx.EmployeeId == slip.EmployeeId }, slip.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.File(pdf, "application/pdf", $"{slip.PayslipNumber}.pdf");
        }).RequireAuthorization();

        // ---- Leave -------------------------------------------------------------------------
        var leave = api.MapGroup("/leave").RequireAuthorization();

        leave.MapGet("/types", async (Guid? legalEntityId, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.LeaveTypes.AsQueryable();
            if (legalEntityId is { } le) q = q.Where(t => t.LegalEntityId == le);
            return Results.Ok(await q.OrderBy(t => t.Name).ToListAsync(ct));
        });
        leave.MapPost("/types", async (LeaveType body, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage); ctx.RequireLegalEntity(body.LegalEntityId);
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant();
            db.LeaveTypes.Add(body);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/leave/types/{body.Id}", body);
        });

        leave.MapGet("/balances/{employeeId:guid}", async (Guid employeeId, int? year, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            if (ctx.EmployeeId != employeeId) ctx.Require(Permissions.EmployeesRead);
            var y = year ?? DateTime.UtcNow.Year;
            var rows = await db.EmployeeLeaveBalances.Where(b => b.EmployeeId == employeeId && b.Year == y)
                .Join(db.LeaveTypes, b => b.LeaveTypeId, t => t.Id, (b, t) => new { b.Id, b.LeaveTypeId, leaveType = t.Name, t.Code, t.IsPaid, b.Year, b.Entitlement, b.Accrued, b.Taken, b.CarriedOver, available = b.CarriedOver + b.Accrued - b.Taken })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        leave.MapGet("/requests", async (Guid? employeeId, LeaveRequestStatus? status, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.LeaveRequests.AsQueryable();
            if (!ctx.Has(Permissions.LeaveApprove)) { if (ctx.EmployeeId is null) throw new ForbiddenException("No employee linked to this user."); q = q.Where(r => r.EmployeeId == ctx.EmployeeId); }
            else if (employeeId is { } e) q = q.Where(r => r.EmployeeId == e);
            if (status is { } s) q = q.Where(r => r.Status == s);
            var rows = await q.OrderByDescending(r => r.CreatedAt).Take(500)
                .Join(db.Employees, r => r.EmployeeId, e => e.Id, (r, e) => new { r, employee = e.FirstName + " " + e.LastName, e.EmployeeNumber })
                .Join(db.LeaveTypes, x => x.r.LeaveTypeId, t => t.Id, (x, t) => new { x.r.Id, x.r.EmployeeId, x.employee, x.EmployeeNumber, leaveType = t.Name, t.IsPaid, x.r.StartDate, x.r.EndDate, x.r.Days, x.r.Reason, x.r.Status, x.r.ApproverComment, x.r.DecidedAt, x.r.CreatedAt })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        leave.MapPost("/requests", async (LeaveRequestBody body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var employeeId = ctx.EmployeeId ?? throw new ForbiddenException("No employee linked to this user.");
            if (body.EndDate < body.StartDate || body.Days <= 0) throw new DomainException("Invalid leave dates.", "VALIDATION");
            var emp = await db.Employees.FirstAsync(e => e.Id == employeeId, ct);
            var type = await db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == body.LeaveTypeId, ct) ?? throw new NotFoundException(nameof(LeaveType), body.LeaveTypeId);
            if (type.IsPaid && type.AnnualEntitlementDays > 0)
            {
                var bal = await db.EmployeeLeaveBalances.FirstOrDefaultAsync(b => b.EmployeeId == employeeId && b.LeaveTypeId == type.Id && b.Year == body.StartDate.Year, ct);
                var pending = await db.LeaveRequests.Where(r => r.EmployeeId == employeeId && r.LeaveTypeId == type.Id && r.Status == LeaveRequestStatus.Pending).SumAsync(r => r.Days, ct);
                if (bal is null || bal.Available - pending < body.Days) throw new DomainException($"Insufficient {type.Name} balance.", "INSUFFICIENT_BALANCE");
            }
            var overlap = await db.LeaveRequests.AnyAsync(r => r.EmployeeId == employeeId && r.Status != LeaveRequestStatus.Rejected && r.Status != LeaveRequestStatus.Cancelled && r.StartDate <= body.EndDate && r.EndDate >= body.StartDate, ct);
            if (overlap) throw new DomainException("Overlaps an existing leave request.", "OVERLAP");
            var r = new LeaveRequest { LegalEntityId = emp.LegalEntityId, EmployeeId = employeeId, LeaveTypeId = type.Id, StartDate = body.StartDate, EndDate = body.EndDate, Days = body.Days, Reason = body.Reason };
            db.LeaveRequests.Add(r);
            audit.Record("leave.requested", nameof(LeaveRequest), r.Id, new { type.Code, r.StartDate, r.EndDate, r.Days }, emp.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/leave/requests/{r.Id}", r);
        });

        leave.MapPost("/requests/{id:guid}/approve", async (Guid id, LeaveDecision? d, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.LeaveApprove);
            var r = await db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(LeaveRequest), id);
            if (r.Status != LeaveRequestStatus.Pending) throw new InvalidStateException("Request is not pending.");
            if (r.EmployeeId == ctx.EmployeeId) throw new ForbiddenException("You cannot approve your own leave.");
            r.Status = LeaveRequestStatus.Approved; r.ApproverId = ctx.UserId; r.DecidedAt = DateTimeOffset.UtcNow; r.ApproverComment = d?.Comment;
            var bal = await db.EmployeeLeaveBalances.FirstOrDefaultAsync(b => b.EmployeeId == r.EmployeeId && b.LeaveTypeId == r.LeaveTypeId && b.Year == r.StartDate.Year, ct);
            if (bal is not null) bal.Taken += r.Days;
            audit.Record("leave.approved", nameof(LeaveRequest), id, new { r.Days }, r.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(r);
        });
        leave.MapPost("/requests/{id:guid}/reject", async (Guid id, LeaveDecision d, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.LeaveApprove);
            var r = await db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(LeaveRequest), id);
            if (r.Status != LeaveRequestStatus.Pending) throw new InvalidStateException("Request is not pending.");
            r.Status = LeaveRequestStatus.Rejected; r.ApproverId = ctx.UserId; r.DecidedAt = DateTimeOffset.UtcNow; r.ApproverComment = d.Comment;
            audit.Record("leave.rejected", nameof(LeaveRequest), id, new { d.Comment }, r.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(r);
        });
        leave.MapPost("/requests/{id:guid}/cancel", async (Guid id, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var r = await db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(LeaveRequest), id);
            if (r.EmployeeId != ctx.EmployeeId) ctx.Require(Permissions.LeaveApprove);
            if (r.Status == LeaveRequestStatus.Approved && r.ProcessedInRunId != null) throw new InvalidStateException("Leave already processed in payroll.");
            if (r.Status == LeaveRequestStatus.Approved)
            {
                var bal = await db.EmployeeLeaveBalances.FirstOrDefaultAsync(b => b.EmployeeId == r.EmployeeId && b.LeaveTypeId == r.LeaveTypeId && b.Year == r.StartDate.Year, ct);
                if (bal is not null) bal.Taken -= r.Days;
            }
            r.Status = LeaveRequestStatus.Cancelled;
            audit.Record("leave.cancelled", nameof(LeaveRequest), id, null, r.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(r);
        });
    }

    private static void RequireSelfOrSalaryRead(ITenantContext ctx, Guid employeeId)
    {
        if (ctx.EmployeeId == employeeId) return;
        ctx.Require(Permissions.SalaryRead);
    }
}
