using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Ecosys.Payroll.Infrastructure.Services.Hr;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record StageBody(ApplicationStage Stage, string? Reason);
public sealed record RequisitionStatusBody(RequisitionStatus Status);
public sealed record InterviewBody(DateTimeOffset ScheduledAt, List<string> Panel, int? Score, string? Recommendation, string? Notes);
public sealed record OfferBody(decimal Basic, DateOnly StartDate, string? JobTitle, string Status);
public sealed record DocumentMeta(Guid EmployeeId, DocumentCategory Category, string Title, DateOnly? IssuedOn, DateOnly? ExpiresOn, bool VisibleToEmployee);
public sealed record AssetIssue(Guid EmployeeId, DateOnly IssuedOn, string? Condition, bool ChargeableIfNotReturned = true);
public sealed record AssetReturn(string? Condition, AssetStatus? NewStatus);
public sealed record ExpenseDraft(string Title, List<ExpenseLine> Lines, ReimbursementRoute Route = ReimbursementRoute.Payroll);

public static class HrEndpoints
{
    private static async Task<byte[]> ReadAsync(IFormFile f, CancellationToken ct) { using var ms = new MemoryStream(); await f.CopyToAsync(ms, ct); return ms.ToArray(); }

    public static void MapHr(this RouteGroupBuilder api)
    {
        api.MapGet("/hr/me", async (ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            var team = await access.DirectReportsAsync(ct);
            var me = ctx.EmployeeId;
            return Results.Ok(new
            {
                employeeId = me, directReports = team.Count,
                team = await db.Employees.Where(e => team.Contains(e.Id)).OrderBy(e => e.EmployeeNumber).Select(e => new { e.Id, e.EmployeeNumber, fullName = e.FirstName + " " + e.LastName, e.JobTitle }).ToListAsync(ct),
                pendingLeave = await db.LeaveRequests.CountAsync(r => team.Contains(r.EmployeeId) && r.Status == LeaveRequestStatus.Pending, ct),
                pendingExpenses = await db.ExpenseClaims.CountAsync(c => team.Contains(c.EmployeeId) && c.Status == ExpenseStatus.Submitted, ct),
                pendingNominations = await db.TrainingEnrolments.CountAsync(e => team.Contains(e.EmployeeId) && e.Status == EnrolmentStatus.Nominated, ct),
                reviewsToComplete = await db.PerformanceReviews.CountAsync(r => team.Contains(r.EmployeeId) && (r.Status == ReviewStatus.ManagerReview || r.Status == ReviewStatus.SelfAssessment), ct),
                feedbackRequested = me is null ? 0 : await db.FeedbackRequests.CountAsync(f => f.ProviderEmployeeId == me && f.Status == FeedbackStatus.Requested, ct),
                myReviewOpen = me is not null && await db.PerformanceReviews.AnyAsync(r => r.EmployeeId == me && (r.Status == ReviewStatus.SelfAssessment || r.Status == ReviewStatus.Completed), ct),
            });
        }).RequireAuthorization();
        MapFiles(api); MapRecruitment(api); MapDocuments(api); MapCases(api); MapPositions(api); MapBenefits(api); MapExpenses(api); MapAssets(api); MapAnalytics(api);
    }

    // =====================================================================================
    private static void MapFiles(RouteGroupBuilder api)
    {
        // Access follows the owning record: the employee themself, their line manager, or HR/finance with the relevant permission.
        api.MapGet("/files/{id:guid}", async (Guid id, ITenantContext ctx, HrAccess access, FileStore files, AuditService audit, PayrollDbContext db, CancellationToken ct) =>
        {
            var f = await files.FindAsync(id, ct) ?? throw new NotFoundException("File", id);
            var permission = f.OwnerType switch
            {
                nameof(JobApplication) => Permissions.HrRecruitment,
                nameof(HrCase) => Permissions.HrCases,
                nameof(ExpenseClaim) => Permissions.ExpensesFinance,
                nameof(LeaveRequest) => Permissions.LeaveApprove,
                nameof(TrainingEnrolment) => Permissions.HrLearning,
                nameof(Dependant) => Permissions.HrBenefits,
                nameof(OfferLetter) => Permissions.HrRecruitment,
                nameof(Domain.Organisation.Letterhead) => Permissions.OrganisationManage,
                nameof(ContractDocument) => Permissions.SalaryRead,
                _ => Permissions.HrDocuments,
            };
            var allowed = ctx.Has(permission) || (f.EmployeeId is { } e && (access.IsSelf(e) && f.OwnerType != nameof(HrCase) || await access.IsLineManagerOfAsync(e, ct) && f.OwnerType is nameof(LeaveRequest) or nameof(ExpenseClaim)));
            if (f.OwnerType == nameof(EmployeeDocument) && access.IsSelf(f.EmployeeId ?? Guid.Empty) && !ctx.Has(permission))
                allowed = await db.EmployeeDocuments.AnyAsync(d => d.StoredFileId == f.Id && d.VisibleToEmployee, ct);
            if (f.OwnerType == nameof(OfferLetter) && !ctx.Has(Permissions.SalaryRead)) allowed = false; // the letter states the salary
            // Contracts on an employee's file state their pay: besides the employee, only people with salary access may open them.
            if (allowed && f.OwnerType == nameof(EmployeeDocument) && !access.IsSelf(f.EmployeeId ?? Guid.Empty) && !ctx.Has(Permissions.SalaryRead)
                && await db.EmployeeDocuments.AnyAsync(d => d.StoredFileId == f.Id && d.Category == DocumentCategory.Contract, ct))
                allowed = false;
            if (!allowed) throw new ForbiddenException("You do not have access to this file.");
            audit.Record("file.downloaded", "StoredFile", f.Id, new { f.OwnerType, f.FileName });
            await db.SaveChangesAsync(ct);
            return Results.File(f.Content, f.ContentType, f.FileName);
        }).RequireAuthorization();
    }

    // =====================================================================================
    private static void MapRecruitment(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/recruitment").RequireAuthorization();
        g.MapGet("/requisitions", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrRecruitment);
            var reqs = await db.JobRequisitions.OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
            var counts = await db.JobApplications.GroupBy(a => new { a.JobRequisitionId, a.Stage }).Select(x => new { x.Key.JobRequisitionId, x.Key.Stage, n = x.Count() }).ToListAsync(ct);
            return Results.Ok(reqs.Select(r => new { requisition = r, pipeline = counts.Where(c => c.JobRequisitionId == r.Id).ToDictionary(c => c.Stage.ToString(), c => c.n) }));
        });
        g.MapPost("/requisitions", async (JobRequisition body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrRecruitment); ctx.RequireLegalEntity(body.LegalEntityId);
            if (string.IsNullOrWhiteSpace(body.Title) || body.Openings < 1) throw new DomainException("Title and at least one opening are required.", "VALIDATION");
            if (body.SalaryMin > body.SalaryMax) throw new DomainException("Salary range is inverted.", "VALIDATION");
            if (body.PositionId is { } p)
            {
                var pos = await db.Positions.FirstOrDefaultAsync(x => x.Id == p && x.LegalEntityId == body.LegalEntityId, ct) ?? throw new DomainException("Position not found.", "VALIDATION");
                var filled = await db.Employees.CountAsync(e => e.PositionId == p && e.TerminationDate == null, ct);
                var open = await db.JobRequisitions.Where(r => r.PositionId == p && (r.Status == RequisitionStatus.Open || r.Status == RequisitionStatus.PendingApproval)).SumAsync(r => r.Openings, ct);
                if (filled + open + body.Openings > pos.BudgetedHeadcount)
                    body.Justification = $"[Over budget: {filled} filled + {open} open + {body.Openings} requested vs {pos.BudgetedHeadcount} budgeted] {body.Justification}";
            }
            var count = await db.JobRequisitions.CountAsync(r => r.LegalEntityId == body.LegalEntityId, ct) + 1;
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant(); body.Reference = $"REQ-{DateTime.UtcNow:yyyy}-{count:D3}"; body.Status = RequisitionStatus.PendingApproval; body.RequestedBy = ctx.RequireUser();
            db.JobRequisitions.Add(body);
            audit.Record("recruitment.requisition.created", nameof(JobRequisition), body.Id, new { body.Reference, body.Title, body.Openings }, body.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/recruitment/requisitions/{body.Id}", body);
        });
        g.MapPost("/requisitions/{id:guid}/approve", async (Guid id, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.PayrollApprove); // budget holder sign-off
            var r = await db.JobRequisitions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(JobRequisition), id);
            if (r.Status != RequisitionStatus.PendingApproval) throw new InvalidStateException("Not awaiting approval.");
            if (r.RequestedBy == ctx.UserId) throw new ForbiddenException("The requester cannot approve their own requisition.");
            r.Status = RequisitionStatus.Open; r.ApprovedBy = ctx.UserId;
            audit.Record("recruitment.requisition.approved", nameof(JobRequisition), id, null, r.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(r);
        });
        g.MapPost("/requisitions/{id:guid}/status", async (Guid id, RequisitionStatusBody body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrRecruitment);
            var r = await db.JobRequisitions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(JobRequisition), id);
            var ok = (r.Status, body.Status) switch
            {
                (RequisitionStatus.Open, RequisitionStatus.OnHold or RequisitionStatus.Cancelled or RequisitionStatus.Filled) => true,
                (RequisitionStatus.OnHold, RequisitionStatus.Open or RequisitionStatus.Cancelled) => true,
                (RequisitionStatus.Draft or RequisitionStatus.PendingApproval, RequisitionStatus.Cancelled) => true,
                _ => false,
            };
            if (!ok) throw new InvalidStateException($"A requisition cannot move from {r.Status} to {body.Status}.");
            r.Status = body.Status;
            audit.Record("recruitment.requisition.status", nameof(JobRequisition), id, new { body.Status }, r.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(r);
        });
        g.MapGet("/requisitions/{id:guid}/applications", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrRecruitment);
            return Results.Ok(await db.JobApplications.Where(a => a.JobRequisitionId == id).OrderBy(a => a.CreatedAt).ToListAsync(ct));
        });
        g.MapPost("/requisitions/{id:guid}/applications", async (Guid id, HttpRequest http, ITenantContext ctx, PayrollDbContext db, FileStore files, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrRecruitment);
            var r = await db.JobRequisitions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(JobRequisition), id);
            if (r.Status != RequisitionStatus.Open) throw new InvalidStateException("The requisition is not open.");
            var form = await http.ReadFormAsync(ct);
            string F(string k) => form[k].ToString().Trim();
            if (F("firstName") == "" || F("lastName") == "" || !F("email").Contains('@')) throw new DomainException("Name and a valid email are required.", "VALIDATION");
            var a = new JobApplication { LegalEntityId = r.LegalEntityId, JobRequisitionId = id, FirstName = F("firstName"), LastName = F("lastName"), Email = F("email").ToLowerInvariant(), Phone = F("phone"), Source = F("source"), ConsentToRetain = F("consent") == "true" };
            if (form.Files.GetFile("cv") is { } cv) a.CvFileId = (await files.SaveAsync(cv.FileName, await ReadAsync(cv, ct), nameof(JobApplication), a.Id, null, ct)).Id;
            db.JobApplications.Add(a);
            audit.Record("recruitment.application.added", nameof(JobApplication), a.Id, new { r.Reference }, r.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/recruitment/applications/{a.Id}", a);
        }).DisableAntiforgery();
        g.MapPost("/applications/{id:guid}/stage", async (Guid id, StageBody body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrRecruitment);
            var a = await db.JobApplications.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(JobApplication), id);
            if (a.Stage is ApplicationStage.Hired) throw new InvalidStateException("The applicant has been hired.");
            if (body.Stage == ApplicationStage.Hired) throw new DomainException("Use the hire action.", "VALIDATION");
            if (body.Stage == ApplicationStage.Rejected && string.IsNullOrWhiteSpace(body.Reason)) throw new DomainException("Record a reason for rejecting.", "VALIDATION");
            a.Stage = body.Stage; if (body.Stage == ApplicationStage.Rejected) a.RejectionReason = body.Reason;
            audit.Record("recruitment.application.stage", nameof(JobApplication), id, new { body.Stage }, a.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(a);
        });
        g.MapPost("/applications/{id:guid}/interviews", async (Guid id, InterviewBody body, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrRecruitment);
            var a = await db.JobApplications.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(JobApplication), id);
            if (body.Score is < 1 or > 5) throw new DomainException("Score must be 1–5.", "VALIDATION");
            a.Interviews = [.. a.Interviews, new InterviewRecord { ScheduledAt = body.ScheduledAt, Panel = body.Panel, Score = body.Score, Recommendation = body.Recommendation, Notes = body.Notes }];
            if (a.Stage is ApplicationStage.Applied or ApplicationStage.Screening) a.Stage = ApplicationStage.Interview;
            await db.SaveChangesAsync(ct);
            return Results.Ok(a);
        });
        g.MapPost("/applications/{id:guid}/offer", async (Guid id, OfferBody body, ITenantContext ctx, PayrollDbContext db, AuditService audit, OfferLetterService letters, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrRecruitment); ctx.Require(Permissions.SalaryWrite);
            var a = await db.JobApplications.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(JobApplication), id);
            var r = await db.JobRequisitions.FirstAsync(x => x.Id == a.JobRequisitionId, ct);
            if (body.Status is not ("Draft" or "Sent" or "Accepted" or "Declined")) throw new DomainException("Offer status must be Draft, Sent, Accepted or Declined.", "VALIDATION");
            if (r.SalaryMax is { } max && body.Basic > max) throw new DomainException($"Offer exceeds the approved range (max {max:N0}).", "VALIDATION");
            // A letter already with the candidate fixes the terms: changing them needs the letter withdrawn first.
            var outLetter = await db.OfferLetters.Where(l => l.JobApplicationId == id && (l.Status == OfferLetterStatus.Sent || l.Status == OfferLetterStatus.Accepted)).OrderByDescending(l => l.Version).FirstOrDefaultAsync(ct);
            if (outLetter is not null && (outLetter.Basic != body.Basic || outLetter.StartDate != body.StartDate || outLetter.JobTitle != (body.JobTitle ?? r.Title)))
                throw new InvalidStateException($"Offer letter {outLetter.Reference} with different terms has been {outLetter.Status.ToString().ToLowerInvariant()}. Withdraw it before changing the offer.");
            if (outLetter is { Status: OfferLetterStatus.Sent } && body.Status is "Accepted" or "Declined") await letters.RecordHrResponseAsync(a, body.Status == "Accepted", ct);
            a.OfferBasic = body.Basic; a.OfferStartDate = body.StartDate; a.OfferJobTitle = body.JobTitle; a.OfferStatus = body.Status; a.Stage = ApplicationStage.Offer;
            audit.Record("recruitment.offer", nameof(JobApplication), id, new { body.Status, body.StartDate }, a.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(a);
        });
        MapOfferLetters(g);
        g.MapPost("/applications/{id:guid}/hire", async (Guid id, HireRequest body, HrOperationsService svc, CancellationToken ct) =>
        {
            var e = await svc.HireAsync(id, body, ct);
            return Results.Ok(new { employeeId = e.Id, e.EmployeeNumber });
        });
        g.MapGet("/onboarding", async (Guid? employeeId, bool? openOnly, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.OnboardingTasks.AsQueryable();
            if (employeeId is { } e) { await access.RequireSelfManagerOrAsync(e, Permissions.HrRecruitment, ct); q = q.Where(t => t.EmployeeId == e); }
            else ctx.Require(Permissions.HrRecruitment);
            if (openOnly == true) q = q.Where(t => !t.Done);
            return Results.Ok(await q.Join(db.Employees, t => t.EmployeeId, x => x.Id, (t, x) => new { t.Id, t.EmployeeId, employee = x.FirstName + " " + x.LastName, x.EmployeeNumber, t.Title, t.Owner, t.DueDate, t.Done, t.DoneAt, t.DoneBy })
                .OrderBy(t => t.Done).ThenBy(t => t.DueDate).ToListAsync(ct));
        });
        g.MapPost("/onboarding/{id:guid}/done", async (Guid id, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            var t = await db.OnboardingTasks.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(OnboardingTask), id);
            if (!ctx.Has(Permissions.HrRecruitment) && !await access.IsLineManagerOfAsync(t.EmployeeId, ct)) throw new ForbiddenException("HR or the line manager completes onboarding tasks.");
            t.Done = !t.Done; t.DoneAt = t.Done ? DateTimeOffset.UtcNow : null; t.DoneBy = t.Done ? ctx.UserName : null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(t);
        });
    }

    // =====================================================================================
    private static void MapDocuments(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/documents").RequireAuthorization();
        g.MapGet("", async (Guid? employeeId, int? expiringWithinDays, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.EmployeeDocuments.AsQueryable();
            if (employeeId is { } e)
            {
                if (access.IsSelf(e) && !ctx.Has(Permissions.HrDocuments)) q = q.Where(d => d.EmployeeId == e && d.VisibleToEmployee);
                else { ctx.Require(Permissions.HrDocuments); q = q.Where(d => d.EmployeeId == e); }
            }
            else ctx.Require(Permissions.HrDocuments);
            if (expiringWithinDays is { } days) { var limit = BusinessClock.Today().AddDays(days); q = q.Where(d => d.ExpiresOn != null && d.ExpiresOn <= limit); }
            return Results.Ok(await q.Join(db.Employees, d => d.EmployeeId, x => x.Id, (d, x) => new { d.Id, d.EmployeeId, employee = x.FirstName + " " + x.LastName, x.EmployeeNumber, d.Category, d.Title, d.StoredFileId, d.IssuedOn, d.ExpiresOn, d.Version, d.VisibleToEmployee, d.CreatedAt })
                .OrderBy(d => d.ExpiresOn ?? DateOnly.MaxValue).ToListAsync(ct));
        });
        g.MapPost("", async (HttpRequest http, ITenantContext ctx, PayrollDbContext db, FileStore files, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrDocuments);
            var form = await http.ReadFormAsync(ct);
            var meta = System.Text.Json.JsonSerializer.Deserialize<DocumentMeta>(form["meta"].ToString(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })
                       ?? throw new DomainException("Missing document details.", "VALIDATION");
            var file = form.Files.GetFile("file") ?? throw new DomainException("Choose a file.", "VALIDATION");
            var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == meta.EmployeeId, ct) ?? throw new NotFoundException("Employee", meta.EmployeeId);
            var version = await db.EmployeeDocuments.Where(d => d.EmployeeId == emp.Id && d.Category == meta.Category && d.Title == meta.Title).MaxAsync(d => (int?)d.Version, ct) ?? 0;
            var doc = new EmployeeDocument { LegalEntityId = emp.LegalEntityId, EmployeeId = emp.Id, Category = meta.Category, Title = meta.Title, IssuedOn = meta.IssuedOn, ExpiresOn = meta.ExpiresOn, VisibleToEmployee = meta.Category != DocumentCategory.Disciplinary && meta.VisibleToEmployee, Version = version + 1 };
            doc.StoredFileId = (await files.SaveAsync(file.FileName, await ReadAsync(file, ct), nameof(EmployeeDocument), doc.Id, emp.Id, ct)).Id;
            db.EmployeeDocuments.Add(doc);
            audit.Record("document.uploaded", nameof(EmployeeDocument), doc.Id, new { emp.EmployeeNumber, doc.Category, doc.Version }, emp.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/documents/{doc.Id}", doc);
        }).DisableAntiforgery();
        g.MapDelete("/{id:guid}", async (Guid id, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrDocuments);
            var d = await db.EmployeeDocuments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(EmployeeDocument), id);
            db.EmployeeDocuments.Remove(d); // soft delete keeps history
            audit.Record("document.removed", nameof(EmployeeDocument), id, new { d.Title }, d.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // =====================================================================================
    private static void MapCases(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/cases").RequireAuthorization();
        g.MapGet("", async (CaseStatus? status, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrCases);
            var q = db.HrCases.Where(c => c.EmployeeId != ctx.EmployeeId); // never show a handler cases about themselves
            if (status is { } s) q = q.Where(c => c.Status == s);
            return Results.Ok(await q.Join(db.Employees, c => c.EmployeeId, e => e.Id, (c, e) => new { c.Id, c.Reference, c.Type, c.Category, c.Status, c.Outcome, c.IncidentDate, c.HearingAt, employee = e.FirstName + " " + e.LastName, e.EmployeeNumber, c.CreatedAt })
                .OrderByDescending(c => c.CreatedAt).ToListAsync(ct));
        });
        g.MapGet("/{id:guid}", async (Guid id, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrCases);
            var c = await db.HrCases.FirstOrDefaultAsync(x => x.Id == id && x.EmployeeId != ctx.EmployeeId, ct) ?? throw new NotFoundException(nameof(HrCase), id);
            var e = await db.Employees.FirstAsync(x => x.Id == c.EmployeeId, ct);
            audit.Record("case.viewed", nameof(HrCase), id, null, c.LegalEntityId); // reads of confidential cases are audited
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { @case = c, employee = new { e.Id, e.EmployeeNumber, name = e.FullName, e.JobTitle } });
        });
        g.MapPost("", async (HrCase body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrCases);
            if (body.EmployeeId == ctx.EmployeeId) throw new ForbiddenException("You cannot open a case about yourself.");
            var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == body.EmployeeId, ct) ?? throw new NotFoundException("Employee", body.EmployeeId);
            if (string.IsNullOrWhiteSpace(body.Summary)) throw new DomainException("Describe the matter.", "VALIDATION");
            var count = await db.HrCases.CountAsync(c => c.LegalEntityId == emp.LegalEntityId, ct) + 1;
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant(); body.LegalEntityId = emp.LegalEntityId; body.Status = CaseStatus.Reported; body.Outcome = CaseOutcome.None;
            body.Reference = $"{(body.Type == CaseType.Disciplinary ? "DC" : "GR")}-{DateTime.UtcNow:yyyy}-{count:D3}";
            body.Events = [new CaseEvent { At = DateTimeOffset.UtcNow, By = ctx.UserName ?? "unknown", Kind = "Reported", Note = body.Summary }];
            db.HrCases.Add(body);
            audit.Record("case.opened", nameof(HrCase), body.Id, new { body.Reference, body.Type }, emp.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/cases/{body.Id}", body);
        });
        g.MapPost("/{id:guid}/step", async (Guid id, CaseStep body, HrOperationsService svc, CancellationToken ct) => Results.Ok(await svc.StepCaseAsync(id, body, ct)));
    }

    // =====================================================================================
    private static void MapPositions(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/positions").RequireAuthorization();
        g.MapGet("", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.EmployeesRead);
            var positions = await db.Positions.OrderBy(p => p.Code).ToListAsync(ct);
            var filled = await db.Employees.Where(e => e.PositionId != null && e.TerminationDate == null).GroupBy(e => e.PositionId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key!.Value, x => x.n, ct);
            var open = await db.JobRequisitions.Where(r => r.PositionId != null && (r.Status == RequisitionStatus.Open || r.Status == RequisitionStatus.PendingApproval)).GroupBy(r => r.PositionId).Select(x => new { x.Key, n = x.Sum(r => r.Openings) }).ToDictionaryAsync(x => x.Key!.Value, x => x.n, ct);
            var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name, ct);
            var showPay = ctx.Has(Permissions.SalaryRead);
            return Results.Ok(positions.Select(p => new
            {
                p.Id, p.Code, p.Title, p.Grade, department = p.DepartmentId is { } d ? depts.GetValueOrDefault(d) : null, p.DepartmentId, p.BudgetedHeadcount,
                filled = filled.GetValueOrDefault(p.Id), openRequisitions = open.GetValueOrDefault(p.Id), vacancies = Math.Max(0, p.BudgetedHeadcount - filled.GetValueOrDefault(p.Id)),
                overBudget = filled.GetValueOrDefault(p.Id) > p.BudgetedHeadcount, salaryMin = showPay ? p.SalaryMin : null, salaryMax = showPay ? p.SalaryMax : null, p.ReportsToPositionId, p.IsActive,
            }));
        });
        g.MapPost("", async (Position body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage); ctx.RequireLegalEntity(body.LegalEntityId);
            if (string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.Title) || body.BudgetedHeadcount < 0) throw new DomainException("Code, title and a non-negative headcount are required.", "VALIDATION");
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant(); body.Code = body.Code.Trim().ToUpperInvariant();
            await ValidateReportsToAsync(db, body.Id, body.ReportsToPositionId, ct);
            db.Positions.Add(body);
            audit.Record("position.created", nameof(Position), body.Id, new { body.Code, body.BudgetedHeadcount }, body.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/positions/{body.Id}", body);
        });
        g.MapPut("/{id:guid}", async (Guid id, Position body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var p = await db.Positions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Position), id);
            await ValidateReportsToAsync(db, id, body.ReportsToPositionId, ct);
            p.Title = body.Title; p.Grade = body.Grade; p.DepartmentId = body.DepartmentId; p.BudgetedHeadcount = body.BudgetedHeadcount; p.SalaryMin = body.SalaryMin; p.SalaryMax = body.SalaryMax; p.ReportsToPositionId = body.ReportsToPositionId; p.IsActive = body.IsActive;
            audit.Record("position.updated", nameof(Position), id, new { p.BudgetedHeadcount, p.Grade, p.ReportsToPositionId }, p.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(p);
        });
        g.MapPost("/{id:guid}/assign/{employeeId:guid}", async (Guid id, Guid employeeId, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.EmployeesWrite);
            var p = await db.Positions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Position), id);
            var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == employeeId && x.LegalEntityId == p.LegalEntityId, ct) ?? throw new NotFoundException("Employee", employeeId);
            e.PositionId = p.Id;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        // Organisation chart from reporting lines.
        g.MapGet("/org-chart", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.EmployeesRead);
            return Results.Ok(await db.Employees.Where(e => e.TerminationDate == null).Select(e => new { e.Id, name = e.FirstName + " " + e.LastName, e.JobTitle, e.ReportingManagerId, e.DepartmentId }).ToListAsync(ct));
        });
    }

    /// <summary>"Reports to" must be another position in this organisation, and must not loop back to this one.</summary>
    private static async Task ValidateReportsToAsync(PayrollDbContext db, Guid positionId, Guid? reportsTo, CancellationToken ct)
    {
        if (reportsTo is null) return;
        if (reportsTo == positionId) throw new DomainException("A position cannot report to itself.", "VALIDATION");
        var parents = await db.Positions.Select(p => new { p.Id, p.ReportsToPositionId }).ToDictionaryAsync(p => p.Id, p => p.ReportsToPositionId, ct);
        if (!parents.ContainsKey(reportsTo.Value)) throw new DomainException("The position it reports to was not found.", "VALIDATION");
        var seen = new HashSet<Guid>();
        for (Guid? at = reportsTo; at is { } cur && seen.Add(cur); at = parents.GetValueOrDefault(cur))
            if (cur == positionId) throw new DomainException("That would make a loop: the chosen position already reports, directly or indirectly, to this one.", "VALIDATION");
    }

    // =====================================================================================
    private static void MapBenefits(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/benefits").RequireAuthorization();
        MapDependants(g);
        g.MapGet("/schemes", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrBenefits); // premiums are the employer's cost
            return Results.Ok(await db.MedicalSchemes.OrderBy(s => s.Name).ToListAsync(ct));
        });
        g.MapPost("/schemes", async (MedicalScheme body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrBenefits); ctx.RequireLegalEntity(body.LegalEntityId);
            if (body.EmployeeSharePercent is < 0 or > 100 || body.PremiumByCover.Count == 0 || body.PremiumByCover.Values.Any(v => v < 0)) throw new DomainException("Set premiums per cover level and an employee share of 0–100%.", "VALIDATION");
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant();
            db.MedicalSchemes.Add(body);
            audit.Record("benefits.scheme.created", nameof(MedicalScheme), body.Id, new { body.Name, body.EmployeeSharePercent }, body.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/benefits/schemes/{body.Id}", body);
        });
        g.MapGet("/enrolments", async (Guid? employeeId, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.BenefitEnrolments.AsQueryable();
            if (employeeId is { } e) { await access.RequireSelfManagerOrAsync(e, Permissions.HrBenefits, ct); q = q.Where(x => x.EmployeeId == e); }
            else ctx.Require(Permissions.HrBenefits);
            var rows = await q.Join(db.MedicalSchemes, b => b.MedicalSchemeId, s => s.Id, (b, s) => new { b, s })
                .Join(db.Employees, x => x.b.EmployeeId, e => e.Id, (x, e) => new { x.b.Id, x.b.EmployeeId, employee = e.FirstName + " " + e.LastName, e.EmployeeNumber, scheme = x.s.Name, x.s.Provider, x.b.Cover, x.b.StartDate, x.b.EndDate, x.b.MembershipNumber, x.b.CoveredDependantIds, premiumByCover = x.s.PremiumByCover, x.s.EmployeeSharePercent })
                .ToListAsync(ct);
            // Premiums are what the cover costs the employer: for HR only. Employees and managers see the cover itself.
            if (ctx.Has(Permissions.HrBenefits)) return Results.Ok(rows);
            return Results.Ok(rows.Select(r => new { r.Id, r.EmployeeId, r.employee, r.EmployeeNumber, r.scheme, r.Provider, r.Cover, r.StartDate, r.EndDate, r.MembershipNumber, r.CoveredDependantIds }));
        });
        g.MapPost("/enrolments", async (BenefitEnrolment body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrBenefits);
            var scheme = await db.MedicalSchemes.FirstOrDefaultAsync(s => s.Id == body.MedicalSchemeId, ct) ?? throw new NotFoundException(nameof(MedicalScheme), body.MedicalSchemeId);
            var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == body.EmployeeId && e.LegalEntityId == scheme.LegalEntityId, ct) ?? throw new NotFoundException("Employee", body.EmployeeId);
            if (!scheme.PremiumByCover.ContainsKey(body.Cover)) throw new DomainException($"Cover must be one of: {string.Join(", ", scheme.PremiumByCover.Keys)}.", "VALIDATION");
            var deps = await db.Dependants.Where(d => d.EmployeeId == emp.Id && d.Status == DependantStatus.Approved).Select(d => d.Id).ToListAsync(ct);
            if (body.CoveredDependantIds.Except(deps).Any()) throw new DomainException("Covered dependants must be registered to the employee and approved.", "VALIDATION");
            if (await db.BenefitEnrolments.AnyAsync(b => b.EmployeeId == emp.Id && b.MedicalSchemeId == scheme.Id && (b.EndDate == null || b.EndDate >= body.StartDate), ct)) throw new DomainException("Already enrolled; end the current enrolment first.", "DUPLICATE");
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant(); body.LegalEntityId = emp.LegalEntityId;
            db.BenefitEnrolments.Add(body);
            audit.Record("benefits.enrolled", nameof(BenefitEnrolment), body.Id, new { emp.EmployeeNumber, scheme.Name, body.Cover, body.StartDate }, emp.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/benefits/enrolments/{body.Id}", body);
        });
        g.MapPost("/enrolments/{id:guid}/end", async (Guid id, DateOnly endDate, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrBenefits);
            var b = await db.BenefitEnrolments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(BenefitEnrolment), id);
            if (endDate < b.StartDate) throw new DomainException("End date precedes the start.", "VALIDATION");
            b.EndDate = endDate;
            await db.SaveChangesAsync(ct);
            return Results.Ok(b);
        });
    }

    public sealed record DraftOfferBody(bool Restart = false);
    public sealed record SendOfferBody(bool EmailLink = true);
    public sealed record WithdrawOfferBody(string? Reason);

    private static async Task<object> LetterView(OfferLetter l, OfferLetterService svc, CancellationToken ct)
    {
        var (subject, body, unfilled) = await svc.PreviewAsync(l, ct);
        return new
        {
            l.Id, l.JobApplicationId, l.Version, l.Reference, status = l.Status.ToString(), l.Subject, l.Body, l.SignatoryName, l.SignatoryTitle, l.Basic, l.StartDate, l.JobTitle, l.AcceptBy,
            l.SentAt, l.SentBy, l.SentFileId, l.AcceptedFileId, linkSent = l.ResponseTokenHash != null, l.RespondedAt, l.ResponseName, l.RecordedByHr, l.DeclineReason,
            preview = new { subject, body, unfilled },
        };
    }

    /// <summary>Offer letters: template, draft, edit, preview PDF, send (with an optional accept/decline link) and withdraw.</summary>
    private static void MapOfferLetters(RouteGroupBuilder g)
    {
        g.MapGet("/offer-template/{legalEntityId:guid}", async (Guid legalEntityId, OfferLetterService svc, CancellationToken ct) =>
        {
            var t = await svc.TemplateAsync(legalEntityId, ct);
            return Results.Ok(new { template = t, placeholders = OfferLetterService.Placeholders.Select(p => new { key = p.Key, meaning = p.Meaning }), standardSubject = OfferLetterService.DefaultSubject, standardBody = OfferLetterService.StandardBody });
        });
        g.MapPut("/offer-template/{legalEntityId:guid}", async (Guid legalEntityId, OfferTemplateRequest body, OfferLetterService svc, CancellationToken ct) =>
            Results.Ok(await svc.SaveTemplateAsync(legalEntityId, body, ct)));
        g.MapGet("/applications/{id:guid}/offer-letters", async (Guid id, OfferLetterService svc, CancellationToken ct) =>
        {
            var list = new List<object>();
            foreach (var l in await svc.ForApplicationAsync(id, ct)) list.Add(await LetterView(l, svc, ct));
            return Results.Ok(list);
        });
        g.MapPost("/applications/{id:guid}/offer-letter", async (Guid id, DraftOfferBody body, OfferLetterService svc, CancellationToken ct) =>
            Results.Ok(await LetterView(await svc.DraftAsync(id, body.Restart, ct), svc, ct)));
        g.MapPut("/offer-letters/{id:guid}", async (Guid id, OfferDraftEdit body, OfferLetterService svc, CancellationToken ct) =>
            Results.Ok(await LetterView(await svc.UpdateDraftAsync(id, body, ct), svc, ct)));
        g.MapGet("/offer-letters/{id:guid}/pdf", async (Guid id, ITenantContext ctx, PayrollDbContext db, OfferLetterService svc, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrRecruitment); ctx.Require(Permissions.SalaryRead);
            var l = await db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(OfferLetter), id);
            ctx.RequireLegalEntity(l.LegalEntityId);
            // Sent letters are served exactly as sent (or as accepted); drafts are rendered fresh.
            var stored = l.AcceptedFileId ?? l.SentFileId;
            if (stored is { } fid && await db.StoredFiles.FirstOrDefaultAsync(f => f.Id == fid, ct) is { } f) return Results.File(f.Content, "application/pdf", f.FileName);
            return Results.File(await svc.PdfAsync(l, ct), "application/pdf", $"{l.Reference}-draft.pdf");
        });
        g.MapPost("/offer-letters/{id:guid}/send", async (Guid id, SendOfferBody body, OfferLetterService svc, CancellationToken ct) =>
        {
            var (l, link) = await svc.SendAsync(id, body.EmailLink, ct);
            return Results.Ok(new { letter = await LetterView(l, svc, ct), link });
        });
        g.MapPost("/offer-letters/{id:guid}/withdraw", async (Guid id, WithdrawOfferBody body, OfferLetterService svc, CancellationToken ct) =>
            Results.Ok(await LetterView(await svc.WithdrawAsync(id, body.Reason ?? "", ct), svc, ct)));
    }

    public sealed record DependantDecision(string? Reason);

    /// <summary>Dependants and beneficiaries: submit with documents → HR verifies → a second person approves.</summary>
    private static void MapDependants(RouteGroupBuilder g)
    {
        static object View(Dependant d, IReadOnlyDictionary<Guid, string> fileNames, IReadOnlyDictionary<Guid, string> users) => new
        {
            d.Id, d.EmployeeId, d.FullName, d.Relationship, d.DateOfBirth, idNumber = FieldEncryptor.Mask(d.IdNumber), phone = FieldEncryptor.Mask(d.Phone), d.IsBeneficiary, d.BeneficiaryPercent,
            status = d.Status.ToString(), submittedAt = d.CreatedAt, d.VerifiedAt, verifiedBy = d.VerifiedBy is { } v ? users.GetValueOrDefault(v) : null,
            d.ApprovedAt, approvedBy = d.ApprovedBy is { } a ? users.GetValueOrDefault(a) : null, d.RejectedAt, d.RejectionReason,
            submittedBy = d.CreatedBy is { } c ? users.GetValueOrDefault(c) : null,
            documents = d.DocumentFileIds.Select(id => new { id, fileName = fileNames.GetValueOrDefault(id) ?? "Document" }),
        };
        static async Task<(Dictionary<Guid, string> Files, Dictionary<Guid, string> Users)> NamesAsync(PayrollDbContext db, IReadOnlyCollection<Dependant> ds, CancellationToken ct)
        {
            var fileIds = ds.SelectMany(d => d.DocumentFileIds).Distinct().ToList();
            var userIds = ds.SelectMany(d => new[] { d.CreatedBy, d.VerifiedBy, d.ApprovedBy }).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
            return (await db.StoredFiles.Where(f => fileIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.FileName, ct),
                    await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct));
        }
        static async Task<(DependantRequest Meta, List<UploadedFile> Files)> ReadFormAsync(HttpRequest http, CancellationToken ct)
        {
            if (!http.HasFormContentType) throw new DomainException("Send the details as a form with the supporting documents attached.", "VALIDATION");
            var form = await http.ReadFormAsync(ct);
            var meta = form.ContainsKey("meta")
                ? System.Text.Json.JsonSerializer.Deserialize<DependantRequest>(form["meta"].ToString(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                : null;
            var files = new List<UploadedFile>();
            foreach (var f in form.Files) files.Add(new UploadedFile(f.FileName, await ReadAsync(f, ct)));
            return (meta!, files);
        }

        // One employee's records: the employee themself or HR (not line managers: family details are private).
        g.MapGet("/dependants/{employeeId:guid}", async (Guid employeeId, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            if (!access.IsSelf(employeeId)) ctx.Require(Permissions.HrBenefits);
            var ds = await db.Dependants.Where(d => d.EmployeeId == employeeId).OrderBy(d => d.CreatedAt).ToListAsync(ct);
            var (files, users) = await NamesAsync(db, ds, ct);
            return Results.Ok(ds.Select(d => View(d, files, users)));
        });

        // HR queue. status: pending (default: awaiting verification or approval), approved, rejected or all.
        g.MapGet("/dependants", async (string? status, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrBenefits);
            var q = db.Dependants.AsQueryable();
            if (ctx.LegalEntityIds.Count > 0) q = q.Where(d => ctx.LegalEntityIds.Contains(d.LegalEntityId));
            q = (status ?? "pending").ToLowerInvariant() switch
            {
                "approved" => q.Where(d => d.Status == DependantStatus.Approved),
                "rejected" => q.Where(d => d.Status == DependantStatus.Rejected),
                "all" => q,
                _ => q.Where(d => d.Status == DependantStatus.Submitted || d.Status == DependantStatus.Verified),
            };
            var ds = await q.OrderBy(d => d.CreatedAt).Take(500).ToListAsync(ct);
            var (files, users) = await NamesAsync(db, ds, ct);
            var empIds = ds.Select(d => d.EmployeeId).Distinct().ToList();
            var emps = await db.Employees.Where(e => empIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => new { name = e.FirstName + " " + e.LastName, e.EmployeeNumber }, ct);
            return Results.Ok(ds.Select(d => new { dependant = View(d, files, users), employee = emps.GetValueOrDefault(d.EmployeeId)?.name, employeeNumber = emps.GetValueOrDefault(d.EmployeeId)?.EmployeeNumber }));
        });

        g.MapPost("/dependants", async (HttpRequest http, DependantService svc, PayrollDbContext db, CancellationToken ct) =>
        {
            var (meta, files) = await ReadFormAsync(http, ct);
            if (meta is null) throw new DomainException("Missing the dependant's details.", "VALIDATION");
            var d = await svc.SubmitAsync(meta, files, ct);
            var (f, u) = await NamesAsync(db, [d], ct);
            return Results.Created($"/api/payroll/benefits/dependants/{d.Id}", View(d, f, u));
        }).DisableAntiforgery();

        g.MapPost("/dependants/{id:guid}/documents", async (Guid id, HttpRequest http, DependantService svc, PayrollDbContext db, CancellationToken ct) =>
        {
            var (_, files) = await ReadFormAsync(http, ct);
            var d = await svc.AddDocumentsAsync(id, files, ct);
            var (f, u) = await NamesAsync(db, [d], ct);
            return Results.Ok(View(d, f, u));
        }).DisableAntiforgery();

        g.MapPost("/dependants/{id:guid}/verify", async (Guid id, DependantService svc, CancellationToken ct) => Results.Ok(new { (await svc.VerifyAsync(id, ct)).Status }));
        g.MapPost("/dependants/{id:guid}/approve", async (Guid id, DependantService svc, CancellationToken ct) => Results.Ok(new { (await svc.ApproveAsync(id, ct)).Status }));
        g.MapPost("/dependants/{id:guid}/reject", async (Guid id, DependantDecision body, DependantService svc, CancellationToken ct) => Results.Ok(new { (await svc.RejectAsync(id, body.Reason ?? "", ct)).Status }));
        g.MapDelete("/dependants/{id:guid}", async (Guid id, DependantService svc, CancellationToken ct) => { await svc.RemoveAsync(id, ct); return Results.NoContent(); });
    }

    // =====================================================================================
    private static void MapExpenses(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/expenses").RequireAuthorization();
        g.MapGet("/categories", async (PayrollDbContext db, CancellationToken ct) => Results.Ok(await db.ExpenseCategories.Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync(ct)));
        g.MapPost("/categories", async (ExpenseCategory body, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.ExpensesFinance); ctx.RequireLegalEntity(body.LegalEntityId);
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant(); body.Code = body.Code.Trim().ToUpperInvariant();
            db.ExpenseCategories.Add(body);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/expenses/categories/{body.Id}", body);
        });
        // scope: mine | team (awaiting my approval) | finance (awaiting finance) | all
        g.MapGet("/claims", async (string? scope, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.ExpenseClaims.AsQueryable();
            switch (scope ?? "mine")
            {
                case "team": var team = await access.DirectReportsAsync(ct); q = q.Where(c => team.Contains(c.EmployeeId) && c.Status == ExpenseStatus.Submitted); break;
                case "finance": ctx.Require(Permissions.ExpensesFinance); q = q.Where(c => c.Status == ExpenseStatus.ManagerApproved || (c.Status == ExpenseStatus.Submitted)); break;
                case "all": ctx.Require(Permissions.ExpensesFinance); break;
                default: var me = ctx.EmployeeId ?? throw new ForbiddenException("No employee linked to this user."); q = q.Where(c => c.EmployeeId == me); break;
            }
            return Results.Ok(await q.Join(db.Employees, c => c.EmployeeId, e => e.Id, (c, e) => new { claim = c, employee = e.FirstName + " " + e.LastName, e.EmployeeNumber })
                .OrderByDescending(x => x.claim.CreatedAt).Take(300).ToListAsync(ct));
        });
        g.MapPost("/claims", async (ExpenseDraft body, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var me = ctx.EmployeeId ?? throw new ForbiddenException("No employee linked to this user.");
            var emp = await db.Employees.FirstAsync(e => e.Id == me, ct);
            if (body.Lines.Any(l => l.Amount <= 0 || l.Days <= 0 || string.IsNullOrWhiteSpace(l.Description))) throw new DomainException("Each expense needs a description, a positive amount and days ≥ 1.", "VALIDATION");
            var cats = await db.ExpenseCategories.Where(c => c.LegalEntityId == emp.LegalEntityId).ToDictionaryAsync(c => c.Id, ct);
            if (body.Lines.Any(l => !cats.ContainsKey(l.CategoryId))) throw new DomainException("Unknown expense category.", "VALIDATION");
            var count = await db.ExpenseClaims.CountAsync(c => c.LegalEntityId == emp.LegalEntityId, ct) + 1;
            var c = new ExpenseClaim { LegalEntityId = emp.LegalEntityId, EmployeeId = me, Reference = $"EXP-{DateTime.UtcNow:yyyyMM}-{count:D4}", Title = body.Title, Lines = body.Lines, Route = body.Route };
            (c.Total, c.TaxableAmount) = HrOperationsService.ExpenseTotals(c.Lines, cats);
            db.ExpenseClaims.Add(c);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/expenses/claims/{c.Id}", c);
        });
        g.MapPost("/claims/{id:guid}/lines/{index:int}/receipt", async (Guid id, int index, IFormFile file, ITenantContext ctx, PayrollDbContext db, FileStore files, CancellationToken ct) =>
        {
            var c = await db.ExpenseClaims.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(ExpenseClaim), id);
            if (c.EmployeeId != ctx.EmployeeId) throw new ForbiddenException("Only the claimant attaches receipts.");
            if (c.Status is not (ExpenseStatus.Draft or ExpenseStatus.Rejected)) throw new InvalidStateException("The claim is already submitted.");
            if (index < 0 || index >= c.Lines.Count) throw new DomainException("No such expense line.", "VALIDATION");
            var f = await files.SaveAsync(file.FileName, await ReadAsync(file, ct), nameof(ExpenseClaim), c.Id, c.EmployeeId, ct);
            var lines = c.Lines.ToList();
            lines[index] = new ExpenseLine { CategoryId = lines[index].CategoryId, Date = lines[index].Date, Amount = lines[index].Amount, Days = lines[index].Days, Description = lines[index].Description, ReceiptFileId = f.Id };
            c.Lines = lines;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { f.Id, f.FileName });
        }).DisableAntiforgery();
        g.MapPost("/claims/{id:guid}/submit", async (Guid id, HrOperationsService svc, CancellationToken ct) => Results.Ok(await svc.SubmitExpenseAsync(id, ct)));
        g.MapPost("/claims/{id:guid}/approve", async (Guid id, HrOperationsService svc, CancellationToken ct) => Results.Ok(await svc.ApproveExpenseAsync(id, ct)));
        g.MapPost("/claims/{id:guid}/reject", async (Guid id, Reason body, ITenantContext ctx, HrAccess access, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var c = await db.ExpenseClaims.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(ExpenseClaim), id);
            if (access.IsSelf(c.EmployeeId)) throw new ForbiddenException("You cannot reject your own claim.");
            if (!ctx.Has(Permissions.ExpensesFinance) && !await access.IsLineManagerOfAsync(c.EmployeeId, ct)) throw new ForbiddenException("Line manager or finance only.");
            if (c.Status is not (ExpenseStatus.Submitted or ExpenseStatus.ManagerApproved)) throw new InvalidStateException("Only a claim awaiting approval can be rejected.");
            if (string.IsNullOrWhiteSpace(body.Text)) throw new DomainException("Give a reason.", "VALIDATION");
            c.Status = ExpenseStatus.Rejected; c.RejectionReason = body.Text;
            audit.Record("expense.rejected", nameof(ExpenseClaim), id, new { body.Text }, c.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(c);
        });
        g.MapPost("/claims/{id:guid}/paid", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.ExpensesFinance);
            var c = await db.ExpenseClaims.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(ExpenseClaim), id);
            if (c.Status != ExpenseStatus.Approved || c.Route != ReimbursementRoute.Direct) throw new InvalidStateException("Only approved direct-payment claims are marked paid here; payroll claims are settled by the run.");
            c.Status = ExpenseStatus.Paid;
            await db.SaveChangesAsync(ct);
            return Results.Ok(c);
        });
    }

    // =====================================================================================
    private static void MapAssets(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/assets").RequireAuthorization();
        g.MapGet("", async (Guid? employeeId, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            if (employeeId is { } e)
            {
                await access.RequireSelfManagerOrAsync(e, Permissions.HrAssets, ct);
                return Results.Ok(await db.AssetAssignments.Where(a => a.EmployeeId == e).Join(db.Assets, a => a.AssetId, x => x.Id, (a, x) => new { assignmentId = a.Id, x.Tag, x.Name, x.Category, x.SerialNumber, a.IssuedOn, a.ReturnedOn, a.ConditionOut, a.ConditionIn }).OrderByDescending(a => a.IssuedOn).ToListAsync(ct));
            }
            ctx.Require(Permissions.HrAssets);
            var assets = await db.Assets.OrderBy(a => a.Tag).ToListAsync(ct);
            var current = await db.AssetAssignments.Where(a => a.ReturnedOn == null).Join(db.Employees, a => a.EmployeeId, x => x.Id, (a, x) => new { a.Id, a.AssetId, holder = x.FirstName + " " + x.LastName, x.EmployeeNumber, a.IssuedOn }).ToListAsync(ct);
            return Results.Ok(assets.Select(a => new { asset = a, assignment = current.FirstOrDefault(c => c.AssetId == a.Id) }));
        });
        g.MapPost("", async (Asset body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrAssets); ctx.RequireLegalEntity(body.LegalEntityId);
            if (string.IsNullOrWhiteSpace(body.Tag) || string.IsNullOrWhiteSpace(body.Name) || body.Value < 0) throw new DomainException("Tag, name and a non-negative value are required.", "VALIDATION");
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant(); body.Status = AssetStatus.InStock; body.Tag = body.Tag.Trim().ToUpperInvariant();
            db.Assets.Add(body);
            audit.Record("asset.created", nameof(Asset), body.Id, new { body.Tag, body.Value }, body.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/assets/{body.Id}", body);
        });
        g.MapPost("/{id:guid}/issue", async (Guid id, AssetIssue body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrAssets);
            var a = await db.Assets.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Asset), id);
            if (a.Status != AssetStatus.InStock) throw new InvalidStateException($"The asset is {a.Status}.");
            var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == body.EmployeeId && e.LegalEntityId == a.LegalEntityId && e.TerminationDate == null, ct) ?? throw new DomainException("Employee not found or has left.", "VALIDATION");
            db.AssetAssignments.Add(new AssetAssignment { LegalEntityId = a.LegalEntityId, AssetId = a.Id, EmployeeId = emp.Id, IssuedOn = body.IssuedOn, ConditionOut = body.Condition, ChargeableIfNotReturned = body.ChargeableIfNotReturned });
            a.Status = AssetStatus.Issued;
            audit.Record("asset.issued", nameof(Asset), id, new { a.Tag, emp.EmployeeNumber }, a.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(a);
        });
        g.MapPost("/{id:guid}/return", async (Guid id, AssetReturn body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrAssets);
            var a = await db.Assets.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Asset), id);
            var assignment = await db.AssetAssignments.FirstOrDefaultAsync(x => x.AssetId == id && x.ReturnedOn == null, ct) ?? throw new InvalidStateException("The asset is not issued.");
            assignment.ReturnedOn = BusinessClock.Today(); assignment.ConditionIn = body.Condition;
            a.Status = body.NewStatus ?? AssetStatus.InStock;
            // Keep any open exit checklist in step.
            var sep = await db.Separations.FirstOrDefaultAsync(s => s.EmployeeId == assignment.EmployeeId && s.Status != SeparationStatus.Cancelled && s.Status != SeparationStatus.Completed, ct);
            if (sep?.Checklist.FirstOrDefault(i => i.Key == $"asset:{assignment.Id}") is { } item)
            {
                item.Done = true; item.DoneAt = DateTimeOffset.UtcNow; item.DoneBy = ctx.UserName; item.Note = body.Condition;
                sep.Checklist = [.. sep.Checklist];
            }
            audit.Record("asset.returned", nameof(Asset), id, new { a.Tag, a.Status }, a.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(a);
        });
    }

    // =====================================================================================
    private static void MapAnalytics(RouteGroupBuilder api)
    {
        api.MapGet("/hr-analytics", async (Guid? legalEntityId, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.ReportsRead);
            var today = BusinessClock.Today();
            var emps = await db.Employees.Where(e => legalEntityId == null || e.LegalEntityId == legalEntityId).ToListAsync(ct);
            int Headcount(DateOnly d) => emps.Count(e => e.HireDate <= d && (e.TerminationDate == null || e.TerminationDate >= d));
            var months = Enumerable.Range(0, 12).Select(i => new DateOnly(today.Year, today.Month, 1).AddMonths(i - 11)).ToList();
            var trend = months.Select(m =>
            {
                var end = m.AddMonths(1).AddDays(-1);
                return new { month = m.ToString("yyyy-MM"), headcount = Headcount(end), hires = emps.Count(e => e.HireDate >= m && e.HireDate <= end), leavers = emps.Count(e => e.TerminationDate >= m && e.TerminationDate <= end) };
            }).ToList();
            var yearAgo = today.AddYears(-1);
            var leavers12 = emps.Count(e => e.TerminationDate > yearAgo && e.TerminationDate <= today);
            var avgHeadcount = trend.Average(t => (decimal)t.headcount);
            var active = emps.Where(e => e.TerminationDate == null || e.TerminationDate >= today).ToList();
            var activeIds = active.Select(e => e.Id).ToList();

            var leaveTypes = await db.LeaveTypes.ToDictionaryAsync(t => t.Id, ct);
            var leave12 = await db.LeaveRequests.Where(r => r.Status == LeaveRequestStatus.Approved && r.EndDate > yearAgo && r.StartDate <= today).ToListAsync(ct);
            var absenceDays = leave12.Where(r => leaveTypes.TryGetValue(r.LeaveTypeId, out var t) && (t.Code == "SICK" || !t.IsPaid)).Sum(r => r.Days);
            var workingDays = avgHeadcount * 22m * 12m;
            var training = await db.TrainingEnrolments.Where(e => e.Status == EnrolmentStatus.Completed && e.CertifiedOn >= new DateOnly(today.Year, 1, 1))
                .Join(db.TrainingSessions, e => e.TrainingSessionId, s => s.Id, (e, s) => s.TrainingCourseId).Join(db.TrainingCourses, c => c, c => c.Id, (_, c) => c.DurationHours).ToListAsync(ct);
            var lastCycle = await db.ReviewCycles.Where(c => c.Status == ReviewCycleStatus.Closed || c.Status == ReviewCycleStatus.Calibration).OrderByDescending(c => c.PeriodEnd).FirstOrDefaultAsync(ct);
            var ratings = lastCycle is null ? [] : await db.PerformanceReviews.Where(r => r.ReviewCycleId == lastCycle.Id && r.FinalRating != null).Select(r => r.FinalRating!.Value).ToListAsync(ct);
            var lastRun = await db.PayrollRuns.Where(r => r.Status == PayrollRunStatus.Finalised && (legalEntityId == null || r.LegalEntityId == legalEntityId)).OrderByDescending(r => r.FinalisedAt).FirstOrDefaultAsync(ct);
            var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name, ct);
            decimal Tenure(Employee e) => (today.DayNumber - e.HireDate.DayNumber) / 365.25m;

            return Results.Ok(new
            {
                headcount = active.Count, trend,
                turnoverPercent = avgHeadcount == 0 ? 0 : Math.Round(leavers12 / avgHeadcount * 100, 1),
                leaversLast12Months = leavers12,
                absenteeismPercent = workingDays == 0 ? 0 : Math.Round(absenceDays / workingDays * 100, 2),
                trainingHoursPerEmployee = active.Count == 0 ? 0 : Math.Round(training.Sum() / active.Count, 1),
                employerCostPerHead = lastRun is null || lastRun.EmployeeCount == 0 ? (decimal?)null : ctx.Has(Permissions.SalaryRead) ? Math.Round(lastRun.TotalEmployerCost / lastRun.EmployeeCount, 0) : null,
                byDepartment = active.GroupBy(e => e.DepartmentId is { } d ? depts.GetValueOrDefault(d) ?? "—" : "Unassigned").Select(x => new { department = x.Key, headcount = x.Count() }).OrderByDescending(x => x.headcount),
                byGender = active.GroupBy(e => e.Gender.ToString()).Select(x => new { gender = x.Key, count = x.Count() }),
                byEmploymentType = active.GroupBy(e => e.EmploymentType.ToString()).Select(x => new { type = x.Key, count = x.Count() }),
                tenure = new[] { ("< 1 year", 0m, 1m), ("1–3 years", 1m, 3m), ("3–5 years", 3m, 5m), ("5–10 years", 5m, 10m), ("10+ years", 10m, 100m) }
                    .Select(b => new { band = b.Item1, count = active.Count(e => Tenure(e) >= b.Item2 && Tenure(e) < b.Item3) }),
                ratings = lastCycle is null ? null : new { cycle = lastCycle.Name, distribution = Enumerable.Range(1, 5).Select(n => new { rating = n, count = ratings.Count(r => r == n) }) },
                open = new
                {
                    requisitions = await db.JobRequisitions.CountAsync(r => r.Status == RequisitionStatus.Open, ct),
                    onboardingTasks = await db.OnboardingTasks.CountAsync(t => !t.Done && activeIds.Contains(t.EmployeeId), ct),
                    exits = await db.Separations.CountAsync(s => s.Status != SeparationStatus.Completed && s.Status != SeparationStatus.Cancelled, ct),
                    cases = ctx.Has(Permissions.HrCases) ? await db.HrCases.CountAsync(c => c.Status != CaseStatus.Closed, ct) : (int?)null,
                    pendingLeave = await db.LeaveRequests.CountAsync(r => r.Status == LeaveRequestStatus.Pending, ct),
                    expenseClaims = await db.ExpenseClaims.CountAsync(c => c.Status == ExpenseStatus.Submitted || c.Status == ExpenseStatus.ManagerApproved, ct),
                },
            });
        }).RequireAuthorization();
    }
}
