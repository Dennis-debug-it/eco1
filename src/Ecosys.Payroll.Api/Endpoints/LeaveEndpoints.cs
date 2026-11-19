using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Ecosys.Payroll.Infrastructure.Services.Hr;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record LeaveDecision(string? Comment);
public sealed record HolidayBody(Guid LegalEntityId, DateOnly Date, string Name);
public sealed record GenerateHolidaysBody(Guid LegalEntityId, int Year);
public sealed record ProvisionBody(Guid LegalEntityId, DateOnly AsOf);

public static class LeaveEndpoints
{
    public static void MapLeave(this RouteGroupBuilder api)
    {
        var leave = api.MapGroup("/leave").RequireAuthorization();

        // ---- Types ----
        leave.MapGet("/types", async (Guid? legalEntityId, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.LeaveTypes.AsQueryable();
            if (legalEntityId is { } le) q = q.Where(t => t.LegalEntityId == le);
            return Results.Ok(await q.OrderBy(t => t.Name).ToListAsync(ct));
        });
        leave.MapPost("/types", async (LeaveType body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage); ctx.RequireLegalEntity(body.LegalEntityId);
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant();
            db.LeaveTypes.Add(body);
            audit.Record("leave.type.created", nameof(LeaveType), body.Id, new { body.Code, body.AnnualEntitlementDays }, body.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/leave/types/{body.Id}", body);
        });
        leave.MapPut("/types/{id:guid}", async (Guid id, LeaveType body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var t = await db.LeaveTypes.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(LeaveType), id);
            ctx.RequireLegalEntity(t.LegalEntityId);
            t.Name = body.Name; t.AnnualEntitlementDays = body.AnnualEntitlementDays; t.IsPaid = body.IsPaid; t.AccruesMonthly = body.AccruesMonthly;
            t.MaxCarryOverDays = body.MaxCarryOverDays; t.RequiresAttachment = body.RequiresAttachment; t.IsActive = body.IsActive;
            t.FullPayDaysPerYear = body.FullPayDaysPerYear; t.HalfPayDaysPerYear = body.HalfPayDaysPerYear; t.MinServiceMonths = body.MinServiceMonths; t.CountsWorkingDaysOnly = body.CountsWorkingDaysOnly;
            audit.Record("leave.type.updated", nameof(LeaveType), id, new { t.Code, t.AnnualEntitlementDays, t.FullPayDaysPerYear, t.HalfPayDaysPerYear }, t.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(t);
        });

        // ---- Balances ----
        leave.MapGet("/balances/{employeeId:guid}", async (Guid employeeId, int? year, HrAccess access, PayrollDbContext db, LeaveBalanceService balances, CancellationToken ct) =>
        {
            await access.RequireSelfManagerOrAsync(employeeId, Permissions.EmployeesRead, ct);
            var y = year ?? BusinessClock.Today().Year;
            await balances.EnsureAsync(employeeId, y, ct);
            await db.SaveChangesAsync(ct);
            var rows = await db.EmployeeLeaveBalances.Where(b => b.EmployeeId == employeeId && b.Year == y)
                .Join(db.LeaveTypes, b => b.LeaveTypeId, t => t.Id, (b, t) => new { b.Id, b.LeaveTypeId, leaveType = t.Name, t.Code, t.IsPaid, b.Year, b.Entitlement, b.Accrued, b.Taken, b.CarriedOver, available = b.CarriedOver + b.Accrued - b.Taken })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // ---- Requests ----
        // scope: mine (default for employees), team (direct reports), all (HR).
        leave.MapGet("/requests", async (string? scope, Guid? employeeId, LeaveRequestStatus? status, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.LeaveRequests.AsQueryable();
            scope ??= ctx.Has(Permissions.LeaveApprove) ? "all" : "mine";
            if (scope == "team") { var team = await access.DirectReportsAsync(ct); q = q.Where(r => team.Contains(r.EmployeeId)); }
            else if (scope == "all") { ctx.Require(Permissions.LeaveApprove); if (employeeId is { } e) q = q.Where(r => r.EmployeeId == e); }
            else { var me = ctx.EmployeeId ?? throw new ForbiddenException("No employee linked to this user."); q = q.Where(r => r.EmployeeId == me); }
            if (status is { } s) q = q.Where(r => r.Status == s);
            var rows = await q.OrderByDescending(r => r.CreatedAt).Take(500)
                .Join(db.Employees, r => r.EmployeeId, e => e.Id, (r, e) => new { r, employee = e.FirstName + " " + e.LastName, e.EmployeeNumber })
                .Join(db.LeaveTypes, x => x.r.LeaveTypeId, t => t.Id, (x, t) => new
                {
                    x.r.Id, x.r.EmployeeId, x.employee, x.EmployeeNumber, leaveType = t.Name, t.IsPaid, t.RequiresAttachment, x.r.StartDate, x.r.EndDate, x.r.Days, x.r.Reason, x.r.Status,
                    x.r.ApproverComment, x.r.DecidedAt, x.r.CreatedAt, x.r.FullPayDays, x.r.HalfPayDays, x.r.UnpaidDays, x.r.AttachmentFileId, x.r.ApproverEmployeeId,
                })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        leave.MapGet("/days", async (Guid leaveTypeId, DateOnly start, DateOnly end, PayrollDbContext db, LeaveService svc, CancellationToken ct) =>
        {
            var type = await db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == leaveTypeId, ct) ?? throw new NotFoundException(nameof(LeaveType), leaveTypeId);
            if (end < start) return Results.Ok(new { days = 0m });
            return Results.Ok(new { days = await svc.CountDaysAsync(type.LegalEntityId, type, start, end, ct) });
        });

        leave.MapPost("/requests", async (LeaveApplication body, LeaveService svc, CancellationToken ct) =>
        {
            var o = await svc.ApplyAsync(body, ct);
            return Results.Created($"/api/payroll/leave/requests/{o.Request.Id}", new { request = o.Request, clashes = o.Clashes, warnings = o.Warnings });
        });
        leave.MapPost("/requests/{id:guid}/approve", async (Guid id, LeaveDecision? d, LeaveService svc, CancellationToken ct) => Results.Ok(await svc.ApproveAsync(id, d?.Comment, ct)));
        leave.MapPost("/requests/{id:guid}/reject", async (Guid id, LeaveDecision d, LeaveService svc, CancellationToken ct) => Results.Ok(await svc.RejectAsync(id, d.Comment ?? "", ct)));
        leave.MapPost("/requests/{id:guid}/cancel", async (Guid id, LeaveService svc, CancellationToken ct) => Results.Ok(await svc.CancelAsync(id, ct)));
        leave.MapGet("/requests/{id:guid}/clashes", async (Guid id, PayrollDbContext db, HrAccess access, LeaveService svc, CancellationToken ct) =>
        {
            var r = await db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(LeaveRequest), id);
            await access.RequireSelfManagerOrAsync(r.EmployeeId, Permissions.LeaveApprove, ct);
            var emp = await db.Employees.FirstAsync(e => e.Id == r.EmployeeId, ct);
            return Results.Ok(await svc.ClashesAsync(emp, r.StartDate, r.EndDate, r.Id, ct));
        });
        leave.MapPost("/requests/{id:guid}/attachment", async (Guid id, IFormFile file, PayrollDbContext db, ITenantContext ctx, FileStore files, AuditService audit, CancellationToken ct) =>
        {
            var r = await db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(LeaveRequest), id);
            if (r.EmployeeId != ctx.EmployeeId) ctx.Require(Permissions.LeaveApprove);
            if (r.Status != LeaveRequestStatus.Pending) throw new InvalidStateException("Evidence can only be attached while the request is pending.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var stored = await files.SaveAsync(file.FileName, ms.ToArray(), nameof(LeaveRequest), r.Id, r.EmployeeId, ct);
            r.AttachmentFileId = stored.Id;
            audit.Record("leave.attachment", nameof(LeaveRequest), id, new { stored.FileName, stored.Size }, r.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { stored.Id, stored.FileName, stored.Size });
        }).DisableAntiforgery();

        // Team calendar: managers see direct reports, HR sees all/filter by department, employees see their department (reason and type hidden).
        leave.MapGet("/calendar", async (DateOnly from, DateOnly to, Guid? departmentId, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            if (to < from || to.DayNumber - from.DayNumber > 120) throw new DomainException("Choose a range of up to 120 days.", "VALIDATION");
            var hr = ctx.Has(Permissions.LeaveApprove);
            var team = await access.DirectReportsAsync(ct);
            Guid? myDept = ctx.EmployeeId is { } me ? await db.Employees.Where(e => e.Id == me).Select(e => e.DepartmentId).FirstOrDefaultAsync(ct) : null;
            var emps = db.Employees.AsQueryable();
            if (hr) { if (departmentId is { } d) emps = emps.Where(e => e.DepartmentId == d); }
            else emps = emps.Where(e => team.Contains(e.Id) || e.Id == ctx.EmployeeId || (myDept != null && e.DepartmentId == myDept));
            var rows = await db.LeaveRequests.Where(r => (r.Status == LeaveRequestStatus.Approved || r.Status == LeaveRequestStatus.Pending) && r.StartDate <= to && r.EndDate >= from)
                .Join(emps, r => r.EmployeeId, e => e.Id, (r, e) => new { r, e })
                .Join(db.LeaveTypes, x => x.r.LeaveTypeId, t => t.Id, (x, t) => new { x.r, x.e, t.Name })
                .ToListAsync(ct);
            var holidays = await db.PublicHolidays.Where(h => h.ObservedDate >= from && h.ObservedDate <= to).Select(h => new { h.ObservedDate, h.Name }).Distinct().ToListAsync(ct);
            return Results.Ok(new
            {
                holidays,
                entries = rows.Select(x =>
                {
                    var full = hr || team.Contains(x.e.Id) || x.e.Id == ctx.EmployeeId;
                    return new { employeeId = x.e.Id, employee = x.e.FirstName + " " + x.e.LastName, x.e.EmployeeNumber, x.r.StartDate, x.r.EndDate, x.r.Days, status = x.r.Status.ToString(), leaveType = full ? x.Name : "Away" };
                }).OrderBy(x => x.StartDate),
            });
        });

        // ---- Public holidays ----
        leave.MapGet("/holidays", async (Guid legalEntityId, int year, PayrollDbContext db, CancellationToken ct) =>
            Results.Ok(await db.PublicHolidays.Where(h => h.LegalEntityId == legalEntityId && h.Date.Year == year).OrderBy(h => h.Date).ToListAsync(ct)));
        leave.MapPost("/holidays/generate", async (GenerateHolidaysBody body, ITenantContext ctx, WorkCalendar cal, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage); ctx.RequireLegalEntity(body.LegalEntityId);
            var added = await cal.SeedKenyaAsync(ctx.RequireTenant(), body.LegalEntityId, body.Year, ct);
            audit.Record("holidays.generated", "PublicHoliday", body.LegalEntityId, new { body.Year, added }, body.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { added, note = "Eid al-Fitr and Eid al-Adha are gazetted each year; add them once announced." });
        });
        leave.MapPost("/holidays", async (HolidayBody body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage); ctx.RequireLegalEntity(body.LegalEntityId);
            if (await db.PublicHolidays.AnyAsync(h => h.LegalEntityId == body.LegalEntityId && h.Date == body.Date, ct)) throw new DomainException("A holiday already exists on that date.", "DUPLICATE");
            var taken = await db.PublicHolidays.Where(h => h.LegalEntityId == body.LegalEntityId && h.Date.Year == body.Date.Year).Select(h => h.ObservedDate).ToListAsync(ct);
            var observed = body.Date;
            if (observed.DayOfWeek == DayOfWeek.Sunday) { observed = observed.AddDays(1); while (taken.Contains(observed) || observed.DayOfWeek == DayOfWeek.Sunday) observed = observed.AddDays(1); }
            var h = new PublicHoliday { LegalEntityId = body.LegalEntityId, Date = body.Date, ObservedDate = observed, Name = body.Name.Trim(), IsGazettedAdHoc = true };
            db.PublicHolidays.Add(h);
            audit.Record("holiday.added", nameof(PublicHoliday), h.Id, new { h.Date, h.ObservedDate, h.Name }, body.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/leave/holidays/{h.Id}", h);
        });
        leave.MapDelete("/holidays/{id:guid}", async (Guid id, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var h = await db.PublicHolidays.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(PublicHoliday), id);
            db.PublicHolidays.Remove(h);
            audit.Record("holiday.removed", nameof(PublicHoliday), id, new { h.Date, h.Name }, h.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- Leave liability ----
        leave.MapGet("/liability", async (Guid legalEntityId, DateOnly? asOf, string? format, LeaveService svc, ITenantContext ctx, CancellationToken ct) =>
        {
            ctx.Require(Permissions.ReportsRead); ctx.RequireLegalEntity(legalEntityId);
            var date = asOf ?? BusinessClock.Today();
            var rows = await svc.LiabilityAsync(legalEntityId, date, ct);
            if (TabularExport.Parse(format) is { } f)
            {
                var file = TabularExport.Build(f, "Leave pay liability", $"As of {date:dd MMM yyyy}", $"leave-liability-{date:yyyyMMdd}", rows,
                    [new("Employee No", x => x.EmployeeNumber), new("Name", x => x.EmployeeName), new("Department", x => x.Department), new("Days Available", x => x.DaysAvailable, true), new("Daily Rate", x => x.DailyRate, true), new("Liability", x => x.Liability, true)],
                    ["Totals", null, null, rows.Sum(r => r.DaysAvailable), null, rows.Sum(r => r.Liability)]);
                return Results.File(file.Content, file.ContentType, file.FileName);
            }
            return Results.Ok(new { asOf = date, rows, totalDays = rows.Sum(r => r.DaysAvailable), total = rows.Sum(r => r.Liability) });
        });
        leave.MapPost("/liability/post", async (ProvisionBody body, LeaveService svc, ITenantContext ctx, CancellationToken ct) =>
        {
            ctx.RequireLegalEntity(body.LegalEntityId);
            var j = await svc.PostProvisionAsync(body.LegalEntityId, body.AsOf, ct);
            return Results.Ok(new { j.Id, j.Reference, j.PostingDate, j.TotalDebit, j.Narrative, lines = j.Lines.Select(l => new { l.AccountCode, l.Description, l.Debit, l.Credit }) });
        });
    }
}
