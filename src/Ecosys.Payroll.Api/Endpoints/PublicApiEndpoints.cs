using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Integration;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Ecosys.Payroll.Infrastructure.Services.Accounting;
using Ecosys.Payroll.Infrastructure.Services.Integration;
using Ecosys.Payroll.Infrastructure.Services.Saas;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

// ---- The published contract. These shapes change only in a new version (/api/v2); fields may be added, never removed or renamed. ----

public sealed record V1Me(string Kind, Guid Id, string Name, Guid OrganisationId, string Organisation, IReadOnlyList<string> Permissions, IReadOnlyList<Guid> CompanyIds);
public sealed record V1PayrollGroup(Guid Id, string Code, string Name, PayFrequency Frequency, int PayDay, bool IsActive);
public sealed record V1Company(Guid Id, string Name, string RegistrationNumber, string KraPin, string CurrencyCode, int FinancialYearStartMonth, bool IsActive, IReadOnlyList<V1PayrollGroup> PayrollGroups);
public sealed record V1OrgUnit(Guid Id, Guid CompanyId, string Code, string Name, bool IsActive);
public sealed record V1PayrollPeriod(Guid Id, Guid CompanyId, Guid PayrollGroupId, string Name, DateOnly Start, DateOnly End, DateOnly PayDate, PeriodStatus Status);
public sealed record V1PayComponent(Guid Id, Guid CompanyId, string Code, string Name, ComponentType Type, bool Taxable, bool IsActive);
public sealed record V1Salary(PayBasis Basis, decimal BasicSalary, decimal? DailyRate, DateOnly EffectiveFrom);
public sealed record V1Employee(Guid Id, string EmployeeNumber, Guid CompanyId, string FirstName, string? MiddleName, string LastName, string? Email, string? Phone,
    Gender Gender, DateOnly? DateOfBirth, string? NationalId, string? PassportNumber, string? KraPin, string? NssfNumber, string? ShifNumber,
    EmploymentType EmploymentType, EmploymentStatus Status, DateOnly HireDate, DateOnly? ContractEndDate, DateOnly? TerminationDate, string? JobTitle,
    Guid? DepartmentId, Guid? BranchId, Guid? CostCentreId, Guid PayrollGroupId, Guid? ReportingManagerId, string? WorkLocation, V1Salary? Salary, DateTimeOffset UpdatedAt);
/// <summary>For create, <c>employeeNumber</c>, <c>companyId</c>, <c>firstName</c>, <c>lastName</c> and <c>hireDate</c> are required. For update, send only what changes.</summary>
public sealed record V1EmployeeInput(string? EmployeeNumber, Guid? CompanyId, string? FirstName, string? MiddleName, string? LastName, string? Email, string? Phone,
    Gender? Gender, DateOnly? DateOfBirth, string? NationalId, string? PassportNumber, string? KraPin, string? NssfNumber, string? ShifNumber,
    EmploymentType? EmploymentType, EmploymentStatus? Status, DateOnly? HireDate, DateOnly? ContractEndDate, DateOnly? TerminationDate, string? JobTitle,
    Guid? DepartmentId, Guid? BranchId, Guid? CostCentreId, Guid? PayrollGroupId, Guid? ReportingManagerId, string? WorkLocation);
public sealed record V1RunTotals(decimal Gross, decimal Paye, decimal Nssf, decimal Shif, decimal HousingLevy, decimal OtherDeductions, decimal Net, decimal EmployerCost);
public sealed record V1PayrollRun(Guid Id, string Reference, Guid CompanyId, Guid PayrollGroupId, Guid PayrollPeriodId, string Period, DateOnly PeriodStart, DateOnly PeriodEnd,
    DateOnly PayDate, PayrollRunType RunType, PayrollRunStatus Status, int EmployeeCount, DateTimeOffset? ApprovedAt, DateTimeOffset? FinalisedAt, V1RunTotals? Totals);
public sealed record V1RunResult(Guid EmployeeId, string EmployeeNumber, string EmployeeName, decimal BasicPay, decimal GrossPay, decimal TaxablePay, decimal Paye,
    decimal Nssf, decimal Shif, decimal HousingLevy, decimal OtherDeductions, decimal NetPay, decimal EmployerContributions, decimal EmployerCost, bool HasErrors);
public sealed record V1Attendance(string EmployeeNumber, decimal DaysWorked, decimal AbsentDays, decimal OvertimeHours, decimal RestDayOvertimeHours, string? Note);
/// <summary>Name the employee by <c>employeeNumber</c> or <c>employeeId</c>, and the pay item by <c>componentCode</c> or <c>componentId</c>.</summary>
public sealed record V1VariableInput(string? EmployeeNumber, Guid? EmployeeId, string? ComponentCode, Guid? ComponentId, decimal Amount, string? Note);
public sealed record V1LeaveRequest(Guid Id, Guid EmployeeId, Guid CompanyId, string LeaveType, string LeaveTypeName, DateOnly StartDate, DateOnly EndDate, decimal Days,
    LeaveRequestStatus Status, DateTimeOffset? DecidedAt, DateTimeOffset UpdatedAt);
public sealed record V1JournalLine(int Line, string Account, string Description, decimal Debit, decimal Credit, Guid? CostCentreId, Guid? DepartmentId, Guid? BranchId);
public sealed record V1Journal(Guid Id, string Reference, Guid CompanyId, Guid? PayrollRunId, string Source, DateOnly PostingDate, string Currency, decimal TotalDebit, decimal TotalCredit,
    string? ExternalReference, IReadOnlyList<V1JournalLine> Lines);
public sealed record V1EventData(string Object, Guid? Id, string? Url);
public sealed record V1Event(Guid Id, string Type, long Sequence, DateTimeOffset OccurredAt, Guid? CompanyId, V1EventData Data);
public sealed record V1EventPage(IReadOnlyList<V1Event> Items, long Next, bool HasMore);
public sealed record V1AuditEntry(Guid Id, DateTimeOffset OccurredAt, string Action, string EntityType, Guid? EntityId, Guid? CompanyId, string? Actor, string? Details);
public sealed record V1Saved(int Saved);

/// <summary>
/// The published API under <c>/api/v1</c>: stable shapes for other systems, described at <c>/api/v1/openapi.json</c>.
/// It runs the same checks as the screens (shared methods on the screen endpoints) and the same permissions.
/// </summary>
public static class PublicApiEndpoints
{
    private const string Prefix = ApiClientGateMiddleware.ApiPrefix;

    private static void RequireAny(this ITenantContext ctx, params string[] permissions)
    {
        if (!permissions.Any(ctx.Has)) throw new ForbiddenException($"One of these permissions is required: {string.Join(", ", permissions)}.");
    }

    private static IQueryable<T> InScope<T>(this IQueryable<T> q, ITenantContext ctx, Guid? companyId) where T : LegalEntityScopedEntity
    {
        if (companyId is { } c) { ctx.RequireLegalEntity(c); q = q.Where(x => x.LegalEntityId == c); }
        if (ctx.LegalEntityIds.Count > 0) q = q.Where(x => ctx.LegalEntityIds.Contains(x.LegalEntityId));
        return q;
    }

    public static void MapPublicApi(this WebApplication app)
    {
        var v1 = app.MapGroup(Prefix).RequireAuthorization();

        // ---- Who am I ----
        v1.MapGet("/me", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var tenantId = ctx.RequireTenant();
            var org = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).FirstAsync(ct);
            return new V1Me(ctx.ApiClientId is null ? "user" : "api_key", ctx.RequireUser(), ctx.UserName ?? "", tenantId, org, ctx.Permissions.Order().ToList(), ctx.LegalEntityIds.ToList());
        }).WithTags("Account").WithSummary("The key (or person) calling, its permissions and companies");

        // ---- Organisation ----
        v1.MapGet("/companies", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.RequireAny(Permissions.EmployeesRead, Permissions.ReportsRead, Permissions.SalaryRead, Permissions.JournalsRead, Permissions.PayrollPrepare);
            var q = db.LegalEntities.AsQueryable();
            if (ctx.LegalEntityIds.Count > 0) q = q.Where(l => ctx.LegalEntityIds.Contains(l.Id));
            var companies = await q.OrderBy(l => l.Name).ToListAsync(ct);
            var ids = companies.Select(c => c.Id).ToList();
            var groups = (await db.PayrollGroups.Where(g => ids.Contains(g.LegalEntityId)).OrderBy(g => g.Code).ToListAsync(ct)).ToLookup(g => g.LegalEntityId);
            return companies.Select(c => new V1Company(c.Id, c.Name, c.RegistrationNumber, c.TaxPin, c.CurrencyCode, c.FinancialYearStartMonth, c.IsActive,
                groups[c.Id].Select(g => new V1PayrollGroup(g.Id, g.Code, g.Name, g.Frequency, g.PayDay, g.IsActive)).ToList())).ToList();
        }).WithTags("Organisation").WithSummary("Companies (legal entities) and their payroll groups");

        v1.MapGet("/departments", (Guid? companyId, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
            Units(db.Departments.InScope(ctx, companyId).Select(d => new V1OrgUnit(d.Id, d.LegalEntityId, d.Code, d.Name, d.IsActive)), ctx, ct))
            .WithTags("Organisation").WithSummary("Departments");
        v1.MapGet("/branches", (Guid? companyId, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
            Units(db.Branches.InScope(ctx, companyId).Select(d => new V1OrgUnit(d.Id, d.LegalEntityId, d.Code, d.Name, d.IsActive)), ctx, ct))
            .WithTags("Organisation").WithSummary("Branches");
        v1.MapGet("/cost-centres", (Guid? companyId, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
            Units(db.CostCentres.InScope(ctx, companyId).Select(d => new V1OrgUnit(d.Id, d.LegalEntityId, d.Code, d.Name, d.IsActive)), ctx, ct))
            .WithTags("Organisation").WithSummary("Cost centres");

        v1.MapGet("/payroll-periods", async (Guid? companyId, Guid? payrollGroupId, DateOnly? from, DateOnly? to, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.RequireAny(Permissions.EmployeesRead, Permissions.ReportsRead, Permissions.SalaryRead, Permissions.PayrollPrepare);
            var q = db.PayrollPeriods.InScope(ctx, companyId);
            if (payrollGroupId is { } g) q = q.Where(p => p.PayrollGroupId == g);
            if (from is { } f) q = q.Where(p => p.PeriodEnd >= f);
            if (to is { } t) q = q.Where(p => p.PeriodStart <= t);
            return await q.OrderBy(p => p.PeriodStart).Take(500)
                .Select(p => new V1PayrollPeriod(p.Id, p.LegalEntityId, p.PayrollGroupId, p.Name, p.PeriodStart, p.PeriodEnd, p.PayDate, p.Status)).ToListAsync(ct);
        }).WithTags("Organisation").WithSummary("Payroll periods (at most 500; filter by date)");

        v1.MapGet("/pay-components", async (Guid? companyId, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.RequireAny(Permissions.PayrollPrepare, Permissions.SalaryRead, Permissions.ReportsRead);
            return await db.SalaryComponents.InScope(ctx, companyId).OrderBy(c => c.LegalEntityId).ThenBy(c => c.SortOrder).ThenBy(c => c.Code)
                .Select(c => new V1PayComponent(c.Id, c.LegalEntityId, c.Code, c.Name, c.Type, c.Taxable, c.IsActive)).ToListAsync(ct);
        }).WithTags("Organisation").WithSummary("Pay components (earnings and deductions), for one-off pay items");

        // ---- Employees ----
        v1.MapGet("/employees", async (Guid? companyId, EmploymentStatus? status, string? employeeNumber, string? search, DateTimeOffset? updatedSince, int? offset, int? limit,
            ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.EmployeesRead);
            var (off, lim) = Paging.Normalise(offset, limit);
            var q = db.Employees.InScope(ctx, companyId);
            if (status is { } s) q = q.Where(e => e.Status == s);
            if (!string.IsNullOrWhiteSpace(employeeNumber)) q = q.Where(e => e.EmployeeNumber == employeeNumber.Trim());
            if (updatedSince is { } since) { var u = since.ToUniversalTime(); q = q.Where(e => e.UpdatedAt >= u); }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = $"%{search.Trim()}%";
                q = q.Where(e => EF.Functions.ILike(e.FirstName, term) || EF.Functions.ILike(e.LastName, term) || EF.Functions.ILike(e.EmployeeNumber, term));
            }
            var total = await q.CountAsync(ct);
            var rows = await q.OrderBy(e => e.LegalEntityId).ThenBy(e => e.EmployeeNumber).Skip(off).Take(lim).ToListAsync(ct);
            return new Page<V1Employee>(await ToV1Async(rows, ctx, db, ct), total, off, lim);
        }).WithTags("Employees").WithSummary("List employees, paged (limit up to 500). Use updatedSince to sync changes.");

        v1.MapGet("/employees/{id:guid}", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.EmployeesRead);
            return (await ToV1Async([await LoadEmployeeAsync(id, ctx, db, ct)], ctx, db, ct))[0];
        }).WithTags("Employees").WithSummary("One employee");

        v1.MapPost("/employees", async Task<Created<V1Employee>> (V1EmployeeInput input, ITenantContext ctx, PayrollDbContext db, AuditService audit, SubscriptionService plan, CancellationToken ct) =>
        {
            ctx.Require(Permissions.EmployeesWrite);
            if (input.CompanyId is not { } company) throw new DomainException("companyId is required.", "VALIDATION");
            ctx.RequireLegalEntity(company);
            if (input.HireDate is not { } hired) throw new DomainException("hireDate is required.", "VALIDATION");
            var group = input.PayrollGroupId ?? await OnlyPayrollGroupAsync(db, company, ct);
            var e = new Employee
            {
                LegalEntityId = company, EmployeeNumber = input.EmployeeNumber?.Trim() ?? "", FirstName = input.FirstName?.Trim() ?? "", LastName = input.LastName?.Trim() ?? "",
                HireDate = hired, PayrollGroupId = group,
            };
            Apply(e, input);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            e = await EmployeeEndpoints.CreateAsync(e, ctx, db, audit, plan, ct);
            await SaveStatutoryAsync(e, input, ctx, db, audit, ct);
            await tx.CommitAsync(ct);
            return TypedResults.Created($"{Prefix}/employees/{e.Id}", (await ToV1Async([e], ctx, db, ct))[0]);
        }).WithTags("Employees").WithSummary("Add an employee. Send an Idempotency-Key header so a retry can't add them twice.");

        v1.MapPatch("/employees/{id:guid}", async (Guid id, V1EmployeeInput input, ITenantContext ctx, PayrollDbContext db, AuditService audit, SubscriptionService plan, CancellationToken ct) =>
        {
            ctx.Require(Permissions.EmployeesWrite);
            var current = await LoadEmployeeAsync(id, ctx, db, ct);
            if (input.CompanyId is { } c && c != current.LegalEntityId) throw new DomainException("An employee can't move company through the API. Record a transfer in Ecosys.", "VALIDATION");
            if (input.EmployeeNumber is { } n && n.Trim() != current.EmployeeNumber) throw new DomainException("The employee number can't be changed.", "VALIDATION");
            // The shared update replaces every field, so start from the stored employee and lay the changes over it.
            var body = new Employee
            {
                LegalEntityId = current.LegalEntityId, EmployeeNumber = current.EmployeeNumber, FirstName = current.FirstName, MiddleName = current.MiddleName, LastName = current.LastName,
                NationalId = current.NationalId, PassportNumber = current.PassportNumber, DateOfBirth = current.DateOfBirth, Gender = current.Gender, Email = current.Email, Phone = current.Phone,
                PhysicalAddress = current.PhysicalAddress, EmergencyContactName = current.EmergencyContactName, EmergencyContactPhone = current.EmergencyContactPhone,
                EmploymentType = current.EmploymentType, Status = current.Status, HireDate = current.HireDate, ContractEndDate = current.ContractEndDate, TerminationDate = current.TerminationDate,
                JobTitle = current.JobTitle, DepartmentId = current.DepartmentId, BranchId = current.BranchId, CostCentreId = current.CostCentreId, PayrollGroupId = current.PayrollGroupId,
                ReportingManagerId = current.ReportingManagerId, WorkLocation = current.WorkLocation, PositionId = current.PositionId, NoticePeriodDays = current.NoticePeriodDays,
                ProbationEndDate = current.ProbationEndDate,
            };
            Apply(body, input);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var e = await EmployeeEndpoints.UpdateAsync(id, body, ctx, db, audit, plan, ct);
            await SaveStatutoryAsync(e, input, ctx, db, audit, ct);
            await tx.CommitAsync(ct);
            return (await ToV1Async([e], ctx, db, ct))[0];
        }).WithTags("Employees").WithSummary("Change an employee. Send only the fields that change; a field can't be cleared this way.");

        // ---- Payroll runs ----
        v1.MapGet("/payroll-runs", async (Guid? companyId, PayrollRunStatus? status, Guid? payrollPeriodId, int? offset, int? limit, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.RequireAny(Permissions.SalaryRead, Permissions.ReportsRead, Permissions.PayrollPrepare);
            var (off, lim) = Paging.Normalise(offset, limit);
            var q = db.PayrollRuns.InScope(ctx, companyId);
            if (status is { } s) q = q.Where(r => r.Status == s);
            if (payrollPeriodId is { } p) q = q.Where(r => r.PayrollPeriodId == p);
            var total = await q.CountAsync(ct);
            var runs = await q.OrderByDescending(r => r.CreatedAt).Skip(off).Take(lim).ToListAsync(ct);
            return new Page<V1PayrollRun>(await ToV1Async(runs, ctx, db, ct), total, off, lim);
        }).WithTags("Payroll runs").WithSummary("List payroll runs, newest first. Totals need salary.read.");

        v1.MapGet("/payroll-runs/{id:guid}", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.RequireAny(Permissions.SalaryRead, Permissions.ReportsRead, Permissions.PayrollPrepare);
            var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("PayrollRun", id);
            ctx.RequireLegalEntity(run.LegalEntityId);
            return (await ToV1Async([run], ctx, db, ct))[0];
        }).WithTags("Payroll runs").WithSummary("One payroll run");

        v1.MapGet("/payroll-runs/{id:guid}/results", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.SalaryRead);
            var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("PayrollRun", id);
            ctx.RequireLegalEntity(run.LegalEntityId);
            return await db.PayrollRunEmployees.Where(e => e.PayrollRunId == id).OrderBy(e => e.EmployeeNumber)
                .Select(e => new V1RunResult(e.EmployeeId, e.EmployeeNumber, e.EmployeeName, e.BasicPay, e.GrossPay, e.TaxablePay, e.Paye, e.Nssf, e.Shif, e.HousingLevy,
                    e.TotalOtherDeductions, e.NetPay, e.TotalEmployerContributions, e.TotalEmployerCost, e.HasErrors)).ToListAsync(ct);
        }).WithTags("Payroll runs").WithSummary("Each employee's pay in a run (needs salary.read)");

        v1.MapPut("/payroll-runs/{id:guid}/attendance", async (Guid id, List<V1Attendance> rows, ITenantContext ctx, PayrollRunService svc, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var result = await PayrollEndpoints.SaveAttendanceAsync(id, rows.Select(r => new AttendanceRow(r.EmployeeNumber, r.DaysWorked, r.AbsentDays, r.OvertimeHours, r.RestDayOvertimeHours, r.Note)).ToList(),
                "api", ctx, svc, db, audit, ct);
            return new V1Saved(rows.Count);
        }).WithTags("Payroll runs").WithSummary("Set days worked, absences and overtime for employees in a run that is still open. Rows replace what was there for those employees.");

        v1.MapPost("/payroll-runs/{id:guid}/variable-inputs", async (Guid id, List<V1VariableInput> items, ITenantContext ctx, PayrollRunService svc, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.PayrollPrepare);
            var run = await svc.GetAsync(id, includeEmployees: false, ct);
            var numbers = items.Where(i => i.EmployeeId is null && !string.IsNullOrWhiteSpace(i.EmployeeNumber)).Select(i => i.EmployeeNumber!.Trim()).Distinct().ToList();
            var byNumber = await db.Employees.Where(e => e.LegalEntityId == run.LegalEntityId && numbers.Contains(e.EmployeeNumber)).ToDictionaryAsync(e => e.EmployeeNumber, e => e.Id, ct);
            var codes = items.Where(i => i.ComponentId is null && !string.IsNullOrWhiteSpace(i.ComponentCode)).Select(i => i.ComponentCode!.Trim().ToUpperInvariant()).Distinct().ToList();
            var byCode = await db.SalaryComponents.Where(c => c.LegalEntityId == run.LegalEntityId && codes.Contains(c.Code.ToUpper())).ToDictionaryAsync(c => c.Code.ToUpperInvariant(), c => c.Id, ct);
            var requests = items.Select((i, n) => new VariableInputRequest(
                i.EmployeeId ?? (i.EmployeeNumber is { } en && byNumber.TryGetValue(en.Trim(), out var eid) ? eid : throw new DomainException($"Item {n + 1}: employee '{i.EmployeeNumber}' was not found in this run's company.", "VALIDATION")),
                i.ComponentId ?? (i.ComponentCode is { } cc && byCode.TryGetValue(cc.Trim().ToUpperInvariant(), out var cid) ? cid : throw new DomainException($"Item {n + 1}: pay component '{i.ComponentCode}' was not found.", "VALIDATION")),
                i.Amount, i.Note)).ToList();
            await PayrollEndpoints.AddVariableInputsAsync(id, requests, ctx, svc, db, audit, ct);
            return new V1Saved(requests.Count);
        }).WithTags("Payroll runs").WithSummary("Add one-off pay items (overtime, bonus, deductions) to a run that is still open");

        // ---- Leave ----
        v1.MapGet("/leave-requests", async (Guid? companyId, Guid? employeeId, LeaveRequestStatus? status, DateOnly? from, DateOnly? to, DateTimeOffset? updatedSince, int? offset, int? limit,
            ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.EmployeesRead);
            var (off, lim) = Paging.Normalise(offset, limit);
            var q = db.LeaveRequests.InScope(ctx, companyId);
            if (employeeId is { } e) q = q.Where(r => r.EmployeeId == e);
            if (status is { } s) q = q.Where(r => r.Status == s);
            if (from is { } f) q = q.Where(r => r.EndDate >= f);
            if (to is { } t) q = q.Where(r => r.StartDate <= t);
            if (updatedSince is { } since) { var u = since.ToUniversalTime(); q = q.Where(r => r.UpdatedAt >= u); }
            var total = await q.CountAsync(ct);
            var rows = await q.OrderBy(r => r.StartDate).Skip(off).Take(lim)
                .Join(db.LeaveTypes, r => r.LeaveTypeId, lt => lt.Id, (r, lt) => new V1LeaveRequest(r.Id, r.EmployeeId, r.LegalEntityId, lt.Code, lt.Name, r.StartDate, r.EndDate, r.Days, r.Status, r.DecidedAt, r.UpdatedAt))
                .ToListAsync(ct);
            return new Page<V1LeaveRequest>(rows, total, off, lim);
        }).WithTags("Leave").WithSummary("Leave requests (dates, type and status; never the reason given)");

        v1.MapGet("/leave-requests/{id:guid}", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.EmployeesRead);
            var r = await db.LeaveRequests.InScope(ctx, null).Where(x => x.Id == id)
                .Join(db.LeaveTypes, x => x.LeaveTypeId, lt => lt.Id, (x, lt) => new V1LeaveRequest(x.Id, x.EmployeeId, x.LegalEntityId, lt.Code, lt.Name, x.StartDate, x.EndDate, x.Days, x.Status, x.DecidedAt, x.UpdatedAt))
                .FirstOrDefaultAsync(ct);
            return r ?? throw new NotFoundException(nameof(LeaveRequest), id);
        }).WithTags("Leave").WithSummary("One leave request");

        // ---- Accounting ----
        v1.MapGet("/journals", async (Guid? companyId, Guid? payrollRunId, DateOnly? from, DateOnly? to, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.JournalsRead);
            if (payrollRunId is null && (from is null || to is null)) throw new DomainException("Give payrollRunId, or from and to dates.", "VALIDATION");
            var q = db.PayrollJournals.Include(j => j.Lines).InScope(ctx, companyId).Where(j => j.Status != Domain.Finance.JournalStatus.Draft);
            if (payrollRunId is { } r) q = q.Where(j => j.PayrollRunId == r);
            if (from is { } f) q = q.Where(j => j.PostingDate >= f);
            if (to is { } t) q = q.Where(j => j.PostingDate <= t);
            var journals = await q.OrderBy(j => j.PostingDate).ThenBy(j => j.Reference).Take(500).ToListAsync(ct);
            return journals.Select(j => new V1Journal(j.Id, j.Reference, j.LegalEntityId, j.PayrollRunId, j.Source, j.PostingDate, j.CurrencyCode, j.TotalDebit, j.TotalCredit, j.ExternalReference,
                j.Lines.OrderBy(l => l.Sequence).Select(l => new V1JournalLine(l.Sequence, l.AccountCode, l.Description, l.Debit, l.Credit, l.CostCentreId, l.DepartmentId, l.BranchId)).ToList())).ToList();
        }).WithTags("Accounting").WithSummary("Posted payroll journals with their lines, for a run or a date range");

        v1.MapGet("/journals/export", async (string? format, Guid? payrollRunId, Guid? companyId, DateOnly? from, DateOnly? to, JournalExportService svc, CancellationToken ct) =>
        {
            try
            {
                var file = await svc.ExportAsync(JournalExportService.Parse(format), payrollRunId, companyId, from, to, ct);
                return Results.File(file.Content, file.ContentType, file.FileName);
            }
            catch (JournalExportNotReadyException ex)
            {
                return Results.Json(new { title = "The journals have problems", status = 422, code = ex.Code, detail = "Fix the items listed, then export again.", check = ex.Check }, statusCode: 422);
            }
        }).WithTags("Accounting").WithSummary("Journals as an import file: format = quickbooks-online, quickbooks-desktop, sage-pastel, sage-evolution or csv")
          .Produces(StatusCodes.Status200OK, contentType: "text/csv");

        // ---- Events ----
        v1.MapGet("/events", async (long? after, int? limit, string? type, ITenantContext ctx, PayrollDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var visible = IntegrationEventTypes.All.Where(t => CanSee(ctx, t)).ToList();
            if (visible.Count == 0) throw new ForbiddenException("This key can't see any events. Give it employees.read, reports.read or salary.read.");
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var q = db.IntegrationEvents.Where(e => e.Sequence > (after ?? 0) && visible.Contains(e.Type));
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(e => e.Type == type);
            if (ctx.LegalEntityIds.Count > 0) q = q.Where(e => e.LegalEntityId == null || ctx.LegalEntityIds.Contains(e.LegalEntityId.Value));
            var rows = await q.OrderBy(e => e.Sequence).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).Select(e => new V1Event(e.Id, e.Type, e.Sequence, e.CreatedAt, e.LegalEntityId,
                new V1EventData(e.EntityType, e.EntityId, IntegrationEventTypes.Resources.Url(e.EntityType, e.EntityId) is { } path ? $"{config["Payroll:PortalUrl"]?.TrimEnd('/')}{path}" : null))).ToList();
            return new V1EventPage(page, page.Count > 0 ? page[^1].Sequence : after ?? 0, rows.Count > take);
        }).WithTags("Events").WithSummary("Events after a cursor, oldest first: the same events webhooks send. Pass the returned next as after.");

        // ---- Audit ----
        v1.MapGet("/audit-log", async (DateTimeOffset? from, DateTimeOffset? to, string? action, int? offset, int? limit, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.AuditRead);
            var (off, lim) = Paging.Normalise(offset, limit);
            var q = db.PayrollAuditLogs.AsQueryable();
            if (ctx.LegalEntityIds.Count > 0) q = q.Where(a => a.LegalEntityId != null && ctx.LegalEntityIds.Contains(a.LegalEntityId.Value));
            if (from is { } f) { var u = f.ToUniversalTime(); q = q.Where(a => a.OccurredAt >= u); }
            if (to is { } t) { var u = t.ToUniversalTime(); q = q.Where(a => a.OccurredAt < u); }
            if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));
            var total = await q.CountAsync(ct);
            var rows = await q.OrderBy(a => a.OccurredAt).ThenBy(a => a.Id).Skip(off).Take(lim)
                .Select(a => new V1AuditEntry(a.Id, a.OccurredAt, a.Action, a.EntityType, a.EntityId, a.LegalEntityId, a.ActorName, a.Details)).ToListAsync(ct);
            return new Page<V1AuditEntry>(rows, total, off, lim);
        }).WithTags("Audit").WithSummary("The audit log, oldest first, for a security or compliance system");

        app.MapOpenApi(Prefix + "/openapi.json").AllowAnonymous();
    }

    /// <summary>Who may see which events in the feed: employee and leave events need employees.read; run events need reports.read or salary.read.</summary>
    public static bool CanSee(ITenantContext ctx, string type) => type.StartsWith("payroll_run.")
        ? ctx.Has(Permissions.ReportsRead) || ctx.Has(Permissions.SalaryRead)
        : ctx.Has(Permissions.EmployeesRead);

    private static async Task<List<V1OrgUnit>> Units(IQueryable<V1OrgUnit> q, ITenantContext ctx, CancellationToken ct)
    {
        ctx.RequireAny(Permissions.EmployeesRead, Permissions.ReportsRead, Permissions.SalaryRead, Permissions.JournalsRead);
        return await q.OrderBy(u => u.CompanyId).ThenBy(u => u.Code).ToListAsync(ct);
    }

    private static async Task<Employee> LoadEmployeeAsync(Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct)
    {
        var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Employee), id);
        ctx.RequireLegalEntity(e.LegalEntityId);
        return e;
    }

    private static async Task<Guid> OnlyPayrollGroupAsync(PayrollDbContext db, Guid company, CancellationToken ct)
    {
        var groups = await db.PayrollGroups.Where(g => g.LegalEntityId == company && g.IsActive).Select(g => g.Id).Take(2).ToListAsync(ct);
        return groups.Count == 1 ? groups[0] : throw new DomainException("payrollGroupId is required: this company has more than one payroll group (see GET /api/v1/companies).", "VALIDATION");
    }

    private static void Apply(Employee e, V1EmployeeInput i)
    {
        if (i.FirstName is { } fn) e.FirstName = fn.Trim();
        if (i.MiddleName is { } mn) e.MiddleName = mn.Trim();
        if (i.LastName is { } ln) e.LastName = ln.Trim();
        if (i.Email is { } em) e.Email = em.Trim();
        if (i.Phone is { } ph) e.Phone = ph.Trim();
        if (i.Gender is { } g) e.Gender = g;
        if (i.DateOfBirth is { } dob) e.DateOfBirth = dob;
        if (i.NationalId is { } nid) e.NationalId = nid.Trim();
        if (i.PassportNumber is { } pp) e.PassportNumber = pp.Trim();
        if (i.EmploymentType is { } et) e.EmploymentType = et;
        if (i.Status is { } st) e.Status = st;
        if (i.HireDate is { } hd) e.HireDate = hd;
        if (i.ContractEndDate is { } ce) e.ContractEndDate = ce;
        if (i.TerminationDate is { } td) e.TerminationDate = td;
        if (i.JobTitle is { } jt) e.JobTitle = jt.Trim();
        if (i.DepartmentId is { } d) e.DepartmentId = d;
        if (i.BranchId is { } b) e.BranchId = b;
        if (i.CostCentreId is { } cc) e.CostCentreId = cc;
        if (i.PayrollGroupId is { } pg) e.PayrollGroupId = pg;
        if (i.ReportingManagerId is { } rm) e.ReportingManagerId = rm;
        if (i.WorkLocation is { } wl) e.WorkLocation = wl.Trim();
    }

    /// <summary>KRA PIN, NSSF and SHIF numbers, when sent; other statutory settings (exemptions, opening balances) stay as they are.</summary>
    private static async Task SaveStatutoryAsync(Employee e, V1EmployeeInput i, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct)
    {
        if (i.KraPin is null && i.NssfNumber is null && i.ShifNumber is null) return;
        var s = await db.EmployeeStatutoryDetails.AsNoTracking().FirstOrDefaultAsync(x => x.EmployeeId == e.Id, ct);
        var body = new EmployeeStatutoryDetails
        {
            LegalEntityId = e.LegalEntityId, EmployeeId = e.Id,
            KraPin = i.KraPin?.Trim().ToUpperInvariant() ?? s?.KraPin, NssfNumber = i.NssfNumber?.Trim() ?? s?.NssfNumber, ShifNumber = i.ShifNumber?.Trim() ?? s?.ShifNumber,
            NhifNumber = s?.NhifNumber, HasDisabilityExemption = s?.HasDisabilityExemption ?? false, DisabilityExemptionCertificate = s?.DisabilityExemptionCertificate,
            DisabilityExemptAmount = s?.DisabilityExemptAmount ?? 0, DisabilityExemptionExpiry = s?.DisabilityExemptionExpiry, StatutoryExemptions = s?.StatutoryExemptions ?? [],
            NssfOpeningBalance = s?.NssfOpeningBalance ?? 0,
        };
        await EmployeeEndpoints.SaveStatutoryAsync(e.Id, body, ctx, db, audit, ct);
    }

    private static async Task<List<V1Employee>> ToV1Async(List<Employee> rows, ITenantContext ctx, PayrollDbContext db, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var statutory = await db.EmployeeStatutoryDetails.AsNoTracking().Where(s => ids.Contains(s.EmployeeId)).ToDictionaryAsync(s => s.EmployeeId, ct);
        var today = BusinessClock.Today();
        var salaries = ctx.Has(Permissions.SalaryRead)
            ? (await db.EmployeeSalaryAssignments.AsNoTracking().Where(a => ids.Contains(a.EmployeeId) && a.EffectiveFrom <= today && (a.EffectiveTo == null || a.EffectiveTo >= today)).ToListAsync(ct))
                .GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.EffectiveFrom).First())
            : [];
        // Identity numbers in full only for keys that may also change them, as on the screens.
        var full = ctx.Has(Permissions.EmployeesWrite);
        string? Show(string? v) => full ? v : FieldEncryptor.Mask(v);
        return rows.Select(e =>
        {
            var s = statutory.GetValueOrDefault(e.Id);
            var pay = salaries.GetValueOrDefault(e.Id);
            return new V1Employee(e.Id, e.EmployeeNumber, e.LegalEntityId, e.FirstName, e.MiddleName, e.LastName, e.Email, e.Phone, e.Gender, e.DateOfBirth,
                Show(e.NationalId), Show(e.PassportNumber), Show(s?.KraPin), s?.NssfNumber, s?.ShifNumber, e.EmploymentType, e.Status, e.HireDate, e.ContractEndDate, e.TerminationDate,
                e.JobTitle, e.DepartmentId, e.BranchId, e.CostCentreId, e.PayrollGroupId, e.ReportingManagerId, e.WorkLocation,
                pay is null ? null : new V1Salary(pay.PayBasis, pay.BasicSalary, pay.DailyRate, pay.EffectiveFrom), e.UpdatedAt);
        }).ToList();
    }

    private static async Task<List<V1PayrollRun>> ToV1Async(List<PayrollRun> runs, ITenantContext ctx, PayrollDbContext db, CancellationToken ct)
    {
        var periodIds = runs.Select(r => r.PayrollPeriodId).Distinct().ToList();
        var periods = await db.PayrollPeriods.Where(p => periodIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var pay = ctx.Has(Permissions.SalaryRead);
        return runs.Select(r =>
        {
            var p = periods[r.PayrollPeriodId];
            return new V1PayrollRun(r.Id, r.Reference, r.LegalEntityId, r.PayrollGroupId, r.PayrollPeriodId, p.Name, p.PeriodStart, p.PeriodEnd, p.PayDate, r.RunType, r.Status,
                r.EmployeeCount, r.ApprovedAt, r.FinalisedAt,
                pay ? new V1RunTotals(r.TotalGross, r.TotalPaye, r.TotalNssfEmployee, r.TotalShif, r.TotalHousingLevyEmployee, r.TotalOtherDeductions, r.TotalNet, r.TotalEmployerCost) : null);
        }).ToList();
    }
}
