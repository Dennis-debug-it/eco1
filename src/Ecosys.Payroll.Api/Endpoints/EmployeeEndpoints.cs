using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Ecosys.Payroll.Infrastructure.Services.Saas;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record EmployeeListItem(Guid Id, string EmployeeNumber, string FullName, string? JobTitle, string? Department, string? Branch, EmploymentType EmploymentType, EmploymentStatus Status, DateOnly HireDate, decimal? BasicSalary);

public sealed record EmployeeDetail(Employee Employee, EmployeeStatutoryDetails? Statutory, IReadOnlyList<EmployeeBankAccount> BankAccounts, IReadOnlyList<EmployeeSalaryAssignment>? SalaryAssignments, IReadOnlyList<EmploymentRecord> EmploymentHistory, IReadOnlyList<StaffLoan>? Loans);

public sealed record BankAccountRequest(PaymentMethod Method, string? BankCode, string? BranchCode, string AccountName, string AccountNumber, bool IsPrimary = true, decimal SplitPercent = 100m);

public sealed record SalaryAssignmentRequest(decimal BasicSalary, Guid? SalaryStructureId, DateOnly EffectiveFrom, string? ChangeReason, List<EmployeeSalaryAssignmentLine>? Lines, PayBasis PayBasis = PayBasis.Monthly, decimal? DailyRate = null);

public static class EmployeeEndpoints
{
    private static void ValidateIdentifiers(Employee e) => EmployeeRules.ThrowIfAny(EmployeeRules.IdentifierProblems(e));

    private static void ValidateStatutory(EmployeeStatutoryDetails s) => EmployeeRules.ThrowIfAny(EmployeeRules.StatutoryProblems(s));

    /// <summary>Organisational references must exist within the employee's own legal entity (and therefore tenant).</summary>
    private static async Task ValidateReferencesAsync(Employee e, PayrollDbContext db, CancellationToken ct)
    {
        if (!await db.PayrollGroups.AnyAsync(x => x.Id == e.PayrollGroupId && x.LegalEntityId == e.LegalEntityId, ct)) throw new DomainException("Payroll group is not valid for this legal entity.", "VALIDATION");
        if (e.DepartmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d && x.LegalEntityId == e.LegalEntityId, ct)) throw new DomainException("Department is not valid for this legal entity.", "VALIDATION");
        if (e.BranchId is { } b && !await db.Branches.AnyAsync(x => x.Id == b && x.LegalEntityId == e.LegalEntityId, ct)) throw new DomainException("Branch is not valid for this legal entity.", "VALIDATION");
        if (e.CostCentreId is { } c && !await db.CostCentres.AnyAsync(x => x.Id == c && x.LegalEntityId == e.LegalEntityId, ct)) throw new DomainException("Cost centre is not valid for this legal entity.", "VALIDATION");
        if (e.ReportingManagerId is { } m && !await db.Employees.AnyAsync(x => x.Id == m, ct)) throw new DomainException("Reporting manager not found.", "VALIDATION");
        if (e.PositionId is { } p && !await db.Positions.AnyAsync(x => x.Id == p && x.LegalEntityId == e.LegalEntityId, ct)) throw new DomainException("Position is not valid for this legal entity.", "VALIDATION");
        if (e.NoticePeriodDays < 0) throw new DomainException("Notice period cannot be negative.", "VALIDATION");
        if (e.HireDate == default) throw new DomainException("Hire date is required.", "VALIDATION");
    }

    /// <summary>Adds an employee. Shared by the screens and the public API, so both apply the same checks.</summary>
    internal static async Task<Employee> CreateAsync(Employee body, ITenantContext ctx, PayrollDbContext db, AuditService audit, SubscriptionService plan, CancellationToken ct)
    {
        ctx.Require(Permissions.EmployeesWrite); ctx.RequireLegalEntity(body.LegalEntityId);
        if (SubscriptionService.CountedStatuses.Contains(body.Status)) await plan.EnsureCanAddEmployeesAsync(1, ct);
        if (string.IsNullOrWhiteSpace(body.EmployeeNumber) || string.IsNullOrWhiteSpace(body.FirstName) || string.IsNullOrWhiteSpace(body.LastName))
            throw new DomainException("Employee number, first name and last name are required.", "VALIDATION");
        if (await db.Employees.AnyAsync(e => e.LegalEntityId == body.LegalEntityId && e.EmployeeNumber == body.EmployeeNumber, ct))
            throw new DomainException($"Employee number {body.EmployeeNumber} already exists.", "DUPLICATE");
        body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant(); body.UserId = null;
        ValidateIdentifiers(body);
        await ValidateReferencesAsync(body, db, ct);
        db.Employees.Add(body);
        db.EmploymentRecords.Add(new EmploymentRecord { LegalEntityId = body.LegalEntityId, EmployeeId = body.Id, EffectiveFrom = body.HireDate, EmploymentType = body.EmploymentType, Status = body.Status, JobTitle = body.JobTitle, DepartmentId = body.DepartmentId, BranchId = body.BranchId, CostCentreId = body.CostCentreId, PayrollGroupId = body.PayrollGroupId, ChangeReason = "Hired" });
        audit.Record("employee.created", nameof(Employee), body.Id, new { body.EmployeeNumber, body.FirstName, body.LastName }, body.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return body;
    }

    /// <summary>Replaces an employee's details, recording a new employment record when the job changes.</summary>
    internal static async Task<Employee> UpdateAsync(Guid id, Employee body, ITenantContext ctx, PayrollDbContext db, AuditService audit, SubscriptionService plan, CancellationToken ct)
    {
        ctx.Require(Permissions.EmployeesWrite);
        var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Employee), id);
        ctx.RequireLegalEntity(e.LegalEntityId);
        // Returning a leaver to the books counts against the plan's employee limit.
        if (!SubscriptionService.CountedStatuses.Contains(e.Status) && SubscriptionService.CountedStatuses.Contains(body.Status)) await plan.EnsureCanAddEmployeesAsync(1, ct);
        var employmentChanged = e.EmploymentType != body.EmploymentType || e.Status != body.Status || e.JobTitle != body.JobTitle || e.DepartmentId != body.DepartmentId
                                || e.BranchId != body.BranchId || e.CostCentreId != body.CostCentreId || e.PayrollGroupId != body.PayrollGroupId;
        e.FirstName = body.FirstName; e.MiddleName = body.MiddleName; e.LastName = body.LastName; e.NationalId = body.NationalId; e.PassportNumber = body.PassportNumber;
        e.DateOfBirth = body.DateOfBirth; e.Gender = body.Gender; e.Email = body.Email; e.Phone = body.Phone; e.PhysicalAddress = body.PhysicalAddress;
        e.EmergencyContactName = body.EmergencyContactName; e.EmergencyContactPhone = body.EmergencyContactPhone;
        e.EmploymentType = body.EmploymentType; e.Status = body.Status; e.HireDate = body.HireDate; e.ContractEndDate = body.ContractEndDate; e.TerminationDate = body.TerminationDate;
        e.JobTitle = body.JobTitle; e.DepartmentId = body.DepartmentId; e.BranchId = body.BranchId; e.CostCentreId = body.CostCentreId; e.PayrollGroupId = body.PayrollGroupId;
        e.ReportingManagerId = body.ReportingManagerId; e.WorkLocation = body.WorkLocation;
        e.PositionId = body.PositionId; e.NoticePeriodDays = body.NoticePeriodDays; e.ProbationEndDate = body.ProbationEndDate;
        if (e.ReportingManagerId == e.Id) throw new DomainException("An employee cannot report to themselves.", "VALIDATION");
        ValidateIdentifiers(e);
        await ValidateReferencesAsync(e, db, ct);
        if (employmentChanged)
        {
            var today = BusinessClock.Today();
            var open = await db.EmploymentRecords.Where(r => r.EmployeeId == id && r.EffectiveTo == null).ToListAsync(ct);
            foreach (var r in open) r.EffectiveTo = today.AddDays(-1);
            db.EmploymentRecords.Add(new EmploymentRecord { LegalEntityId = e.LegalEntityId, EmployeeId = id, EffectiveFrom = today, EmploymentType = e.EmploymentType, Status = e.Status, JobTitle = e.JobTitle, DepartmentId = e.DepartmentId, BranchId = e.BranchId, CostCentreId = e.CostCentreId, PayrollGroupId = e.PayrollGroupId, ChangeReason = "Updated" });
        }
        audit.Record("employee.updated", nameof(Employee), id, new { e.EmployeeNumber, employmentChanged }, e.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return e;
    }

    /// <summary>Replaces an employee's statutory numbers and exemptions.</summary>
    internal static async Task<EmployeeStatutoryDetails> SaveStatutoryAsync(Guid id, EmployeeStatutoryDetails body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct)
    {
        ctx.Require(Permissions.EmployeesWrite);
        var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Employee), id);
        ctx.RequireLegalEntity(e.LegalEntityId);
        var s = await db.EmployeeStatutoryDetails.FirstOrDefaultAsync(x => x.EmployeeId == id, ct);
        if (s is null) { s = new EmployeeStatutoryDetails { LegalEntityId = e.LegalEntityId, EmployeeId = id }; db.EmployeeStatutoryDetails.Add(s); }
        ValidateStatutory(body);
        if (body.KraPin is not null && await db.EmployeeStatutoryDetails.AnyAsync(x => x.EmployeeId != id && x.LegalEntityId == e.LegalEntityId && x.KraPin == body.KraPin, ct))
            throw new DomainException("Another employee in this legal entity already has that KRA PIN.", "DUPLICATE");
        s.KraPin = body.KraPin; s.NssfNumber = body.NssfNumber; s.ShifNumber = body.ShifNumber; s.NhifNumber = body.NhifNumber;
        s.HasDisabilityExemption = body.HasDisabilityExemption; s.DisabilityExemptionCertificate = body.DisabilityExemptionCertificate; s.DisabilityExemptAmount = body.DisabilityExemptAmount;
        s.DisabilityExemptionExpiry = body.DisabilityExemptionExpiry; s.StatutoryExemptions = body.StatutoryExemptions;
        if (body.NssfOpeningBalance < 0) throw new DomainException("NSSF contributions before Ecosys cannot be negative.", "VALIDATION");
        var openingChanged = s.NssfOpeningBalance != body.NssfOpeningBalance;
        s.NssfOpeningBalance = body.NssfOpeningBalance;
        audit.Record("employee.statutory.updated", nameof(EmployeeStatutoryDetails), s.Id, new { employeeId = id, pin = FieldEncryptor.Mask(s.KraPin), s.StatutoryExemptions, nssfOpeningBalanceChanged = openingChanged }, e.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return s;
    }

    public static void MapEmployees(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/employees").RequireAuthorization();

        g.MapGet("", async (Guid? legalEntityId, Guid? payrollGroupId, Guid? departmentId, string? search, EmploymentStatus? status, int? offset, int? limit,
            ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.EmployeesRead);
            var (off, lim) = Paging.Normalise(offset, limit);
            var q = db.Employees.AsQueryable();
            if (legalEntityId is { } le) q = q.Where(e => e.LegalEntityId == le);
            if (ctx.LegalEntityIds.Count > 0) q = q.Where(e => ctx.LegalEntityIds.Contains(e.LegalEntityId));
            if (payrollGroupId is { } pg) q = q.Where(e => e.PayrollGroupId == pg);
            if (departmentId is { } d) q = q.Where(e => e.DepartmentId == d);
            if (status is { } s) q = q.Where(e => e.Status == s);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = $"%{search.Trim()}%";
                q = q.Where(e => EF.Functions.ILike(e.FirstName, term) || EF.Functions.ILike(e.LastName, term) || EF.Functions.ILike(e.EmployeeNumber, term) || (e.JobTitle != null && EF.Functions.ILike(e.JobTitle, term)));
            }
            var total = await q.CountAsync(ct);
            var rows = await q.OrderBy(e => e.EmployeeNumber).Skip(off).Take(lim).ToListAsync(ct);
            var ids = rows.Select(r => r.Id).ToList();
            var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name, ct);
            var branches = await db.Branches.ToDictionaryAsync(b => b.Id, b => b.Name, ct);
            var today = BusinessClock.Today();
            var salaries = ctx.Has(Permissions.SalaryRead)
                ? (await db.EmployeeSalaryAssignments.Where(a => ids.Contains(a.EmployeeId) && a.EffectiveFrom <= today && (a.EffectiveTo == null || a.EffectiveTo >= today)).ToListAsync(ct))
                    .GroupBy(a => a.EmployeeId).ToDictionary(x => x.Key, x => x.OrderByDescending(a => a.EffectiveFrom).First().BasicSalary)
                : new Dictionary<Guid, decimal>();
            var items = rows.Select(e => new EmployeeListItem(e.Id, e.EmployeeNumber, e.FullName, e.JobTitle,
                e.DepartmentId is { } di ? depts.GetValueOrDefault(di) : null, e.BranchId is { } bi ? branches.GetValueOrDefault(bi) : null,
                e.EmploymentType, e.Status, e.HireDate, salaries.TryGetValue(e.Id, out var bs) ? bs : null)).ToList();
            return Results.Ok(new Page<EmployeeListItem>(items, total, off, lim));
        });

        g.MapGet("/{id:guid}", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var self = ctx.EmployeeId == id;
            if (!self) ctx.Require(Permissions.EmployeesRead);
            var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Employee), id);
            ctx.RequireLegalEntity(e.LegalEntityId);
            var stat = await db.EmployeeStatutoryDetails.FirstOrDefaultAsync(s => s.EmployeeId == id, ct);
            var banks = await db.EmployeeBankAccounts.Where(b => b.EmployeeId == id).ToListAsync(ct);
            var canSeeFull = self || ctx.Has(Permissions.EmployeesWrite);
            if (!canSeeFull)
            {
                foreach (var b in banks) b.AccountNumber = FieldEncryptor.Mask(b.AccountNumber);
                if (stat is not null) stat.KraPin = FieldEncryptor.Mask(stat.KraPin);
                e.NationalId = FieldEncryptor.Mask(e.NationalId);
                e.PassportNumber = FieldEncryptor.Mask(e.PassportNumber);
            }
            var showSalary = self || ctx.Has(Permissions.SalaryRead);
            var salary = showSalary ? await db.EmployeeSalaryAssignments.Include(a => a.Lines).Where(a => a.EmployeeId == id).OrderByDescending(a => a.EffectiveFrom).ToListAsync(ct) : null;
            var loans = showSalary ? await db.StaffLoans.Where(l => l.EmployeeId == id).OrderByDescending(l => l.StartDate).ToListAsync(ct) : null;
            var history = await db.EmploymentRecords.Where(r => r.EmployeeId == id).OrderByDescending(r => r.EffectiveFrom).ToListAsync(ct);
            db.ChangeTracker.Clear(); // masking must never be persisted
            return Results.Ok(new EmployeeDetail(e, stat, banks, salary, history, loans));
        });

        g.MapPost("", async (Employee body, ITenantContext ctx, PayrollDbContext db, AuditService audit, SubscriptionService plan, CancellationToken ct) =>
        {
            var e = await CreateAsync(body, ctx, db, audit, plan, ct);
            return Results.Created($"/api/payroll/employees/{e.Id}", e);
        });

        g.MapPut("/{id:guid}", async (Guid id, Employee body, ITenantContext ctx, PayrollDbContext db, AuditService audit, SubscriptionService plan, CancellationToken ct) =>
            Results.Ok(await UpdateAsync(id, body, ctx, db, audit, plan, ct)));

        g.MapPut("/{id:guid}/statutory", async (Guid id, EmployeeStatutoryDetails body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
            Results.Ok(await SaveStatutoryAsync(id, body, ctx, db, audit, ct)));

        // Maker-checker: a new account is never paid until a second person verifies it, and the employee is told
        // about every change through the contact details already on file.
        g.MapPost("/{id:guid}/bank-accounts", async (Guid id, BankAccountRequest req, ITenantContext ctx, PayrollDbContext db, BankDirectoryService directory, NotificationService notify, AuditService audit, CancellationToken ct) =>
        {
            var self = ctx.EmployeeId == id;
            if (!self) ctx.Require(Permissions.EmployeesWrite);
            var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Employee), id);
            ctx.RequireLegalEntity(e.LegalEntityId);
            if (string.IsNullOrWhiteSpace(req.AccountNumber) || string.IsNullOrWhiteSpace(req.AccountName)) throw new DomainException("Account name and number are required.", "VALIDATION");
            if (req.SplitPercent is <= 0 or > 100) throw new DomainException("Split percent should be more than 0 and at most 100.", "VALIDATION");
            var details = await directory.ResolveAsync(req.Method, req.BankCode, req.BranchCode, req.AccountNumber, ct);
            var account = new EmployeeBankAccount
            {
                TenantId = ctx.RequireTenant(), LegalEntityId = e.LegalEntityId, EmployeeId = id, Method = req.Method,
                BankCode = details.BankCode, BankName = details.BankName, BranchCode = details.BranchCode, BranchName = details.BranchName,
                AccountName = req.AccountName.Trim(), AccountNumber = details.AccountNumber, IsPrimary = req.IsPrimary, SplitPercent = req.SplitPercent,
                IsVerified = false, // the current verified account keeps being paid until this one is verified
            };
            db.EmployeeBankAccounts.Add(account);
            audit.Record("employee.bank.added", nameof(EmployeeBankAccount), account.Id, new { employeeId = id, account.Method, account.BankCode, account.BranchCode, account = FieldEncryptor.Mask(account.AccountNumber), self }, e.LegalEntityId);
            notify.BankDetailsChanged(e, account, verified: false);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/employees/{id}/bank-accounts/{account.Id}", account);
        });

        g.MapPost("/{id:guid}/bank-accounts/{accountId:guid}/verify", async (Guid id, Guid accountId, ITenantContext ctx, PayrollDbContext db, NotificationService notify, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.EmployeesWrite);
            var b = await db.EmployeeBankAccounts.FirstOrDefaultAsync(x => x.Id == accountId && x.EmployeeId == id, ct) ?? throw new NotFoundException(nameof(EmployeeBankAccount), accountId);
            ctx.RequireLegalEntity(b.LegalEntityId);
            if (b.IsVerified) return Results.Ok(b);
            if (ctx.EmployeeId == id) throw new ForbiddenException("You can't verify your own bank details. Ask a colleague in HR or payroll.");
            var entity = await db.LegalEntities.FirstAsync(x => x.Id == b.LegalEntityId, ct);
            if (b.CreatedBy is { } maker && maker == ctx.UserId && !entity.Settings.AllowBankDetailSelfVerification)
                throw new ForbiddenException("A different person must verify bank details you entered.");
            var e = await db.Employees.FirstAsync(x => x.Id == id, ct);

            b.IsVerified = true;
            b.VerifiedBy = ctx.RequireUser();
            b.VerifiedAt = DateTimeOffset.UtcNow;
            if (b.IsPrimary)
                foreach (var other in await db.EmployeeBankAccounts.Where(x => x.EmployeeId == id && x.Id != b.Id && x.IsPrimary).ToListAsync(ct)) other.IsPrimary = false;
            audit.Record("employee.bank.verified", nameof(EmployeeBankAccount), b.Id, new { employeeId = id, account = FieldEncryptor.Mask(b.AccountNumber), enteredBy = b.CreatedBy }, b.LegalEntityId);
            notify.BankDetailsChanged(e, b, verified: true);
            await db.SaveChangesAsync(ct);
            return Results.Ok(b);
        });

        g.MapPost("/{id:guid}/salary", async (Guid id, SalaryAssignmentRequest req, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.SalaryWrite);
            var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Employee), id);
            ctx.RequireLegalEntity(e.LegalEntityId);
            var entity = await db.LegalEntities.FirstAsync(x => x.Id == e.LegalEntityId, ct);
            // Effective-dated: close the current assignment the day before the new one starts; reject backdating into a finalised period.
            var finalisedThrough = await db.PayrollRuns.Where(r => r.LegalEntityId == e.LegalEntityId && r.Status == Domain.Payroll.PayrollRunStatus.Finalised)
                .Join(db.PayrollPeriods, r => r.PayrollPeriodId, p => p.Id, (r, p) => p.PeriodEnd).OrderByDescending(d => d).FirstOrDefaultAsync(ct);
            if (finalisedThrough != default && req.EffectiveFrom <= finalisedThrough)
                throw new InvalidStateException($"Payroll is finalised through {finalisedThrough:yyyy-MM-dd}; salary changes before that date must be processed as adjustments.");
            var current = await db.EmployeeSalaryAssignments.Where(a => a.EmployeeId == id && a.EffectiveTo == null).ToListAsync(ct);
            foreach (var c in current)
            {
                if (c.EffectiveFrom >= req.EffectiveFrom) throw new InvalidStateException("A later or same-dated assignment already exists.");
                c.EffectiveTo = req.EffectiveFrom.AddDays(-1);
            }
            var a = new EmployeeSalaryAssignment { LegalEntityId = e.LegalEntityId, EmployeeId = id, SalaryStructureId = req.SalaryStructureId, EffectiveFrom = req.EffectiveFrom, ChangeReason = req.ChangeReason, ApprovedBy = ctx.UserId };
            PayBasisRules.Apply(a, req.PayBasis, req.BasicSalary, req.DailyRate, entity);
            foreach (var l in req.Lines ?? []) a.Lines.Add(new EmployeeSalaryAssignmentLine { EmployeeSalaryAssignmentId = a.Id, SalaryComponentId = l.SalaryComponentId, Method = l.Method, Amount = l.Amount, Rate = l.Rate });
            db.EmployeeSalaryAssignments.Add(a);
            audit.Record("employee.salary.assigned", nameof(EmployeeSalaryAssignment), a.Id, new { employeeId = id, previous = current.FirstOrDefault()?.BasicSalary, a.BasicSalary, payBasis = a.PayBasis.ToString(), a.DailyRate, a.EffectiveFrom, req.ChangeReason }, e.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/employees/{id}/salary/{a.Id}", a);
        });

        g.MapPost("/{id:guid}/loans", async (Guid id, StaffLoan body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.SalaryWrite);
            var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Employee), id);
            ctx.RequireLegalEntity(e.LegalEntityId);
            if (body.Principal <= 0 || body.InstalmentAmount <= 0) throw new DomainException("Principal and instalment must be positive.", "VALIDATION");
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant(); body.LegalEntityId = e.LegalEntityId; body.EmployeeId = id; body.Balance = body.Principal; body.Status = LoanStatus.Active;
            db.StaffLoans.Add(body);
            audit.Record("employee.loan.created", nameof(StaffLoan), body.Id, new { employeeId = id, body.Reference, body.Principal, body.InstalmentAmount }, e.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/employees/{id}/loans/{body.Id}", body);
        });
    }
}
