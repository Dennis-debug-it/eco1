using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecosys.Payroll.Infrastructure.Services.Hr;

public sealed record HireRequest(string EmployeeNumber, Guid PayrollGroupId, Guid? DepartmentId, Guid? BranchId, Guid? CostCentreId, Guid? ReportingManagerId, string? NationalId, int ProbationMonths = 6);
public sealed record CaseStep(CaseStatus To, string Note, DateOnly? ShowCauseIssuedOn = null, DateOnly? ResponseDueOn = null, DateTimeOffset? HearingAt = null, bool? HearingHeld = null, string? Companion = null, CaseOutcome? Outcome = null);

/// <summary>Recruitment hire, disciplinary/grievance case rules and expense claims.</summary>
public sealed class HrOperationsService(PayrollDbContext db, ITenantContext tenant, HrAccess access, AuditService audit, SystemComponents components)
{
    // ------------------------------------------------------------------ Recruitment

    /// <summary>Hires an accepted applicant: creates the employee, salary assignment and onboarding checklist.</summary>
    public async Task<Employee> HireAsync(Guid applicationId, HireRequest req, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrRecruitment); tenant.Require(Permissions.EmployeesWrite);
        var app = await db.JobApplications.FirstOrDefaultAsync(a => a.Id == applicationId, ct) ?? throw new NotFoundException(nameof(JobApplication), applicationId);
        var reqn = await db.JobRequisitions.FirstAsync(r => r.Id == app.JobRequisitionId, ct);
        if (app.Stage != ApplicationStage.Offer || app.OfferStatus != "Accepted") throw new InvalidStateException("Only an applicant who has accepted an offer can be hired.");
        if (app.OfferBasic is not { } basic || app.OfferStartDate is not { } start) throw new InvalidStateException("The offer needs a salary and start date.");
        if (await db.Employees.AnyAsync(e => e.LegalEntityId == reqn.LegalEntityId && e.EmployeeNumber == req.EmployeeNumber, ct)) throw new DomainException($"Employee number {req.EmployeeNumber} already exists.", "DUPLICATE");
        if (!await db.PayrollGroups.AnyAsync(g => g.Id == req.PayrollGroupId && g.LegalEntityId == reqn.LegalEntityId, ct)) throw new DomainException("Payroll group not found.", "VALIDATION");

        var emp = new Employee
        {
            LegalEntityId = reqn.LegalEntityId, EmployeeNumber = req.EmployeeNumber, FirstName = app.FirstName, LastName = app.LastName, Email = app.Email, Phone = app.Phone,
            NationalId = req.NationalId, HireDate = start, JobTitle = app.OfferJobTitle ?? reqn.Title, DepartmentId = req.DepartmentId ?? reqn.DepartmentId, BranchId = req.BranchId ?? reqn.BranchId,
            CostCentreId = req.CostCentreId, ReportingManagerId = req.ReportingManagerId, PayrollGroupId = req.PayrollGroupId, PositionId = reqn.PositionId,
            EmploymentType = req.ProbationMonths > 0 ? EmploymentType.Probation : Enum.TryParse<EmploymentType>(reqn.EmploymentType, out var t) ? t : EmploymentType.Permanent,
            ProbationEndDate = req.ProbationMonths > 0 ? start.AddMonths(req.ProbationMonths) : null,
        };
        db.Employees.Add(emp);
        db.EmploymentRecords.Add(new EmploymentRecord { LegalEntityId = emp.LegalEntityId, EmployeeId = emp.Id, EffectiveFrom = start, EmploymentType = emp.EmploymentType, Status = EmploymentStatus.Active, JobTitle = emp.JobTitle, DepartmentId = emp.DepartmentId, BranchId = emp.BranchId, CostCentreId = emp.CostCentreId, PayrollGroupId = emp.PayrollGroupId, ChangeReason = $"Hired via {reqn.Reference}" });
        db.EmployeeSalaryAssignments.Add(new EmployeeSalaryAssignment { LegalEntityId = emp.LegalEntityId, EmployeeId = emp.Id, BasicSalary = basic, EffectiveFrom = start, ChangeReason = "Offer accepted", ApprovedBy = tenant.UserId });
        db.EmployeeStatutoryDetails.Add(new EmployeeStatutoryDetails { LegalEntityId = emp.LegalEntityId, EmployeeId = emp.Id });
        foreach (var (title, owner, days) in OnboardingTemplate)
            db.OnboardingTasks.Add(new OnboardingTask { LegalEntityId = emp.LegalEntityId, EmployeeId = emp.Id, Title = title, Owner = owner, DueDate = start.AddDays(days) });

        app.Stage = ApplicationStage.Hired; app.HiredEmployeeId = emp.Id;
        var hired = await db.JobApplications.CountAsync(a => a.JobRequisitionId == reqn.Id && a.Stage == ApplicationStage.Hired, ct) + 1;
        if (hired >= reqn.Openings) reqn.Status = RequisitionStatus.Filled;
        audit.Record("recruitment.hired", nameof(JobApplication), app.Id, new { emp.EmployeeNumber, reqn.Reference, start }, reqn.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return emp;
    }

    public static readonly (string Title, string Owner, int DaysFromStart)[] OnboardingTemplate =
    [
        ("Signed employment contract on file", "HR", -3),
        ("Copy of national ID / passport", "HR", 0),
        ("KRA PIN, NSSF and SHA numbers captured", "HR", 0),
        ("Bank account details captured and verified", "HR", 3),
        ("Workstation, laptop and email ready", "IT", -1),
        ("Welcome and induction session", "Line manager", 1),
        ("Health and safety induction", "HR", 5),
        ("Probation objectives agreed", "Line manager", 14),
    ];

    // ------------------------------------------------------------------ Cases (Employment Act s.41, s.43, s.45)

    public async Task<HrCase> StepCaseAsync(Guid id, CaseStep step, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrCases);
        var c = await db.HrCases.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(HrCase), id);
        if (access.IsSelf(c.EmployeeId)) throw new ForbiddenException("You cannot handle a case about yourself.");
        if (c.Status == CaseStatus.Closed) throw new InvalidStateException("The case is closed.");
        if (string.IsNullOrWhiteSpace(step.Note)) throw new DomainException("Record a note for every step.", "VALIDATION");

        switch (step.To)
        {
            case CaseStatus.ShowCauseIssued:
                if (c.Type != CaseType.Disciplinary) throw new InvalidStateException("Show-cause letters apply to disciplinary cases.");
                c.ShowCauseIssuedOn = step.ShowCauseIssuedOn ?? BusinessClock.Today();
                c.ResponseDueOn = step.ResponseDueOn ?? c.ShowCauseIssuedOn.Value.AddDays(7);
                break;
            case CaseStatus.HearingScheduled:
                if (c.Type == CaseType.Disciplinary && c.ShowCauseIssuedOn is null) throw new InvalidStateException("Issue a show-cause letter stating the grounds before scheduling a hearing (s.41(1)).");
                c.HearingAt = step.HearingAt ?? throw new DomainException("Set the hearing date and time.", "VALIDATION");
                c.Companion = step.Companion;
                break;
            case CaseStatus.Decided:
                var outcome = step.Outcome ?? throw new DomainException("Record the outcome.", "VALIDATION");
                if (step.HearingHeld is { } held) c.HearingHeld = held;
                if (outcome is CaseOutcome.Dismissal or CaseOutcome.FinalWarning or CaseOutcome.Suspension)
                {
                    // s.41: before dismissal (or a sanction short of it) the employee must be told the reason and heard,
                    // in the presence of a colleague or union representative of their choice if they wish.
                    if (c.ShowCauseIssuedOn is null || !c.HearingHeld)
                        throw new InvalidStateException("A hearing must have been held after a show-cause letter before this sanction (Employment Act s.41).");
                    if (c.ResponseDueOn is { } due && due > BusinessClock.Today() && outcome == CaseOutcome.Dismissal)
                        throw new InvalidStateException("The employee's response period has not ended.");
                }
                if (c.Type == CaseType.Grievance && outcome is not (CaseOutcome.GrievanceUpheld or CaseOutcome.GrievanceNotUpheld or CaseOutcome.NoAction))
                    throw new DomainException("A grievance is upheld, not upheld, or closed with no action.", "VALIDATION");
                c.Outcome = outcome; c.OutcomeReason = step.Note;
                c.AppealDeadline = BusinessClock.Today().AddDays(14);
                break;
            case CaseStatus.Appealed:
                if (c.Status != CaseStatus.Decided) throw new InvalidStateException("Only a decided case can be appealed.");
                break;
            case CaseStatus.Investigation or CaseStatus.Closed:
                break;
            default:
                throw new DomainException($"Unsupported step {step.To}.", "VALIDATION");
        }
        c.Status = step.To;
        c.Events = [.. c.Events, new CaseEvent { At = DateTimeOffset.UtcNow, By = tenant.UserName ?? "unknown", Kind = step.To.ToString(), Note = step.Note }];
        audit.Record("case.step", nameof(HrCase), c.Id, new { step.To, step.Outcome }, c.LegalEntityId); // no case content in the audit log
        await db.SaveChangesAsync(ct);
        return c;
    }

    // ------------------------------------------------------------------ Expenses

    public static (decimal Total, decimal Taxable) ExpenseTotals(IEnumerable<ExpenseLine> lines, IReadOnlyDictionary<Guid, ExpenseCategory> categories)
    {
        decimal total = 0, taxable = 0;
        foreach (var l in lines)
        {
            total += l.Amount;
            if (categories.TryGetValue(l.CategoryId, out var c) && c.DailyTaxFreeCap is { } cap)
                taxable += Math.Max(0, l.Amount - cap * Math.Max(1, l.Days));
        }
        return (total, taxable);
    }

    public async Task<ExpenseClaim> SubmitExpenseAsync(Guid id, CancellationToken ct = default)
    {
        var c = await db.ExpenseClaims.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(ExpenseClaim), id);
        if (!access.IsSelf(c.EmployeeId)) throw new ForbiddenException("Only the claimant can submit.");
        if (c.Status is not (ExpenseStatus.Draft or ExpenseStatus.Rejected)) throw new InvalidStateException("The claim has already been submitted.");
        if (c.Lines.Count == 0) throw new DomainException("Add at least one expense.", "VALIDATION");
        var cats = await db.ExpenseCategories.Where(x => x.LegalEntityId == c.LegalEntityId).ToDictionaryAsync(x => x.Id, ct);
        foreach (var l in c.Lines)
        {
            if (!cats.TryGetValue(l.CategoryId, out var cat)) throw new DomainException("Unknown expense category.", "VALIDATION");
            if (cat.ReceiptRequired && l.ReceiptFileId is null) throw new DomainException($"Attach a receipt for '{l.Description}' ({cat.Name}).", "VALIDATION");
        }
        if (c.Lines.GroupBy(l => l.CategoryId).Any(gr => cats[gr.Key].MaxPerClaim is { } max && gr.Sum(l => l.Amount) > max))
            throw new DomainException("A category exceeds its per-claim limit.", "VALIDATION");
        (c.Total, c.TaxableAmount) = ExpenseTotals(c.Lines, cats);
        var emp = await db.Employees.FirstAsync(e => e.Id == c.EmployeeId, ct);
        // No line manager: goes straight to finance.
        c.Status = emp.ReportingManagerId is null ? ExpenseStatus.ManagerApproved : ExpenseStatus.Submitted;
        c.SubmittedAt = DateTimeOffset.UtcNow; c.RejectionReason = null;
        audit.Record("expense.submitted", nameof(ExpenseClaim), id, new { c.Total, c.TaxableAmount }, c.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>Two-step approval: line manager, then finance. Finance approval schedules payroll reimbursement.</summary>
    public async Task<ExpenseClaim> ApproveExpenseAsync(Guid id, CancellationToken ct = default)
    {
        var c = await db.ExpenseClaims.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(ExpenseClaim), id);
        if (access.IsSelf(c.EmployeeId)) throw new ForbiddenException("You cannot approve your own claim.");
        if (c.Status == ExpenseStatus.Submitted)
        {
            if (!await access.IsLineManagerOfAsync(c.EmployeeId, ct)) tenant.Require(Permissions.ExpensesFinance);
            c.Status = ExpenseStatus.ManagerApproved; c.ManagerApprovedBy = tenant.UserId;
            // Finance users approving as stand-in manager still need a separate finance approval.
        }
        else if (c.Status == ExpenseStatus.ManagerApproved)
        {
            tenant.Require(Permissions.ExpensesFinance);
            if (c.ManagerApprovedBy == tenant.UserId) throw new ForbiddenException("Manager and finance approval must be by different people.");
            c.Status = ExpenseStatus.Approved; c.FinanceApprovedBy = tenant.UserId;
            if (c.Route == ReimbursementRoute.Payroll)
            {
                var nonTaxable = c.Total - c.TaxableAmount;
                var today = BusinessClock.Today();
                if (nonTaxable > 0)
                {
                    var comp = await components.EnsureAsync(c.TenantId, c.LegalEntityId, "REIMB", ct);
                    db.ScheduledPayItems.Add(new ScheduledPayItem { LegalEntityId = c.LegalEntityId, EmployeeId = c.EmployeeId, SalaryComponentId = comp.Id, Amount = nonTaxable, DueDate = today, Description = $"Expense claim {c.Reference}", SourceType = nameof(ExpenseClaim), SourceId = c.Id });
                }
                if (c.TaxableAmount > 0)
                {
                    var comp = await components.EnsureAsync(c.TenantId, c.LegalEntityId, "REIMB_TAXABLE", ct);
                    db.ScheduledPayItems.Add(new ScheduledPayItem { LegalEntityId = c.LegalEntityId, EmployeeId = c.EmployeeId, SalaryComponentId = comp.Id, Amount = c.TaxableAmount, DueDate = today, Description = $"Expense claim {c.Reference} (taxable excess)", SourceType = nameof(ExpenseClaim), SourceId = c.Id });
                }
            }
        }
        else throw new InvalidStateException($"A {c.Status} claim cannot be approved.");
        audit.Record("expense.approved", nameof(ExpenseClaim), id, new { c.Status }, c.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return c;
    }
}

/// <summary>Daily HR reminders (certificate, document and contract expiry; probation end) through the notification outbox.</summary>
public sealed class HrReminderJob(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopes, Microsoft.Extensions.Logging.ILogger<HrReminderJob> log) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(BusinessClock.Today(), stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "HR reminder job failed"); }
            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Queues reminders for items reaching 30 days (and 7 days) before expiry. Idempotent per item per day.</summary>
    public async Task<int> RunOnceAsync(DateOnly today, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PayrollDbContext>();
        db.IgnoreTenantFilter = true;
        var targets = new[] { today.AddDays(30), today.AddDays(7) };
        var messages = new List<NotificationMessage>();
        void Add(Guid tenantId, string? email, string name, string subject, string body, string type, Guid id)
        {
            if (string.IsNullOrWhiteSpace(email)) return;
            messages.Add(new NotificationMessage { TenantId = tenantId, Channel = NotificationChannel.Email, Recipient = email, RecipientName = name, Template = "hr.reminder", Subject = subject, Body = body, EntityType = type, EntityId = id, NextAttemptAt = DateTimeOffset.UtcNow });
        }
        var hrUsers = (await db.Users.Where(u => u.IsActive).ToListAsync(ct)).Where(u => u.Roles.Contains(Roles.HrAdministrator)).ToList();
        void ToHr(Guid tenantId, string subject, string body, string type, Guid id) { foreach (var u in hrUsers.Where(u => u.TenantId == tenantId)) Add(tenantId, u.Email, u.DisplayName, subject, body, type, id); }

        var certs = await db.TrainingEnrolments.Where(e => e.ExpiresOn != null && targets.Contains(e.ExpiresOn.Value))
            .Join(db.Employees, e => e.EmployeeId, x => x.Id, (e, x) => new { e.Id, e.TenantId, e.ExpiresOn, x.Email, x.FirstName, x.LastName, e.TrainingSessionId })
            .Join(db.TrainingSessions, x => x.TrainingSessionId, s => s.Id, (x, s) => new { x, s.TrainingCourseId })
            .Join(db.TrainingCourses, x => x.TrainingCourseId, c => c.Id, (x, c) => new { x.x, c.Title }).ToListAsync(ct);
        foreach (var c in certs)
        {
            var subject = $"Certificate expiring {c.x.ExpiresOn:dd MMM yyyy}: {c.Title}";
            Add(c.x.TenantId, c.x.Email, c.x.FirstName, subject, $"Hello {c.x.FirstName}, your {c.Title} certificate expires on {c.x.ExpiresOn:dd MMM yyyy}. Please arrange refresher training.", "TrainingEnrolment", c.x.Id);
            ToHr(c.x.TenantId, subject, $"{c.x.FirstName} {c.x.LastName}'s {c.Title} certificate expires on {c.x.ExpiresOn:dd MMM yyyy}.", "TrainingEnrolment", c.x.Id);
        }
        var docs = await db.EmployeeDocuments.Where(d => d.ExpiresOn != null && targets.Contains(d.ExpiresOn.Value))
            .Join(db.Employees, d => d.EmployeeId, e => e.Id, (d, e) => new { d.Id, d.TenantId, d.Title, d.ExpiresOn, e.FirstName, e.LastName }).ToListAsync(ct);
        foreach (var d in docs) ToHr(d.TenantId, $"Document expiring: {d.Title}", $"{d.FirstName} {d.LastName}: '{d.Title}' expires on {d.ExpiresOn:dd MMM yyyy}.", "EmployeeDocument", d.Id);
        var contracts = await db.Employees.Where(e => e.TerminationDate == null && e.ContractEndDate != null && targets.Contains(e.ContractEndDate.Value)).ToListAsync(ct);
        foreach (var e in contracts) ToHr(e.TenantId, $"Contract ending: {e.FirstName} {e.LastName}", $"{e.FirstName} {e.LastName}'s contract ends on {e.ContractEndDate:dd MMM yyyy}. Renew, convert or start the exit process.", "Employee", e.Id);
        var probation = await db.Employees.Where(e => e.TerminationDate == null && e.ProbationEndDate != null && targets.Contains(e.ProbationEndDate.Value)).ToListAsync(ct);
        foreach (var e in probation) ToHr(e.TenantId, $"Probation ending: {e.FirstName} {e.LastName}", $"{e.FirstName} {e.LastName}'s probation ends on {e.ProbationEndDate:dd MMM yyyy}. Complete the probation review (s.42: probation may not exceed 12 months in total).", "Employee", e.Id);

        // De-duplicate against today's run.
        var since = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var already = (await db.NotificationMessages.Where(m => m.Template == "hr.reminder" && m.CreatedAt >= since).Select(m => new { m.EntityId, m.Subject }).ToListAsync(ct)).Select(x => (x.EntityId, x.Subject)).ToHashSet();
        var fresh = messages.Where(m => !already.Contains((m.EntityId, m.Subject))).ToList();
        db.NotificationMessages.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        return fresh.Count;
    }
}
