using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Ecosys.Payroll.Infrastructure.Services.Hr;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record AdvanceCycle(ReviewCycleStatus To);
public sealed record CalibrateBody(int FinalRating, string? Note);
public sealed record AcknowledgeBody(string? Response);
public sealed record FeedbackAsk(Guid ProviderEmployeeId, string Relationship);
public sealed record FeedbackAnswer(int Rating, string? Strengths, string? Improvements, bool Decline = false);
public sealed record CompReviewCreate(Guid ReviewCycleId, string Name, DateOnly EffectiveDate, Dictionary<int, decimal>? MeritMatrix, Dictionary<int, decimal>? BonusMatrix, decimal? IncreaseBudget, decimal? BonusBudget);
public sealed record CompLineUpdate(decimal IncreasePercent, decimal BonusAmount, bool Excluded, string? Note);
public sealed record GoalProgress(decimal ProgressPercent, GoalStatus? Status);

public static class PerformanceEndpoints
{
    public static void MapPerformance(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/performance").RequireAuthorization();

        // ---- Cycles ----
        g.MapGet("/cycles", async (PayrollDbContext db, CancellationToken ct) => Results.Ok(await db.ReviewCycles.OrderByDescending(c => c.PeriodEnd).ToListAsync(ct)));
        g.MapPost("/cycles", async (ReviewCycle body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrPerformance); ctx.RequireLegalEntity(body.LegalEntityId);
            if (body.PeriodEnd < body.PeriodStart) throw new DomainException("The period ends before it starts.", "VALIDATION");
            if (body.GoalWeightPercent is < 0 or > 100) throw new DomainException("Goal weight must be 0–100%.", "VALIDATION");
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant(); body.Status = ReviewCycleStatus.Draft;
            db.ReviewCycles.Add(body);
            audit.Record("performance.cycle.created", nameof(ReviewCycle), body.Id, new { body.Name, body.Type }, body.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/performance/cycles/{body.Id}", body);
        });
        g.MapPost("/cycles/{id:guid}/advance", async (Guid id, AdvanceCycle body, PerformanceService svc, CancellationToken ct) => Results.Ok(await svc.AdvanceAsync(id, body.To, ct)));
        g.MapGet("/cycles/{id:guid}/distribution", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrPerformance);
            var rows = await db.PerformanceReviews.Where(r => r.ReviewCycleId == id)
                .Join(db.Employees, r => r.EmployeeId, e => e.Id, (r, e) => new { r.FinalRating, r.ManagerScore, r.Status, e.DepartmentId }).ToListAsync(ct);
            var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name, ct);
            return Results.Ok(new
            {
                byStatus = rows.GroupBy(r => r.Status.ToString()).Select(x => new { status = x.Key, count = x.Count() }),
                byRating = Enumerable.Range(1, 5).Select(n => new { rating = n, count = rows.Count(r => r.FinalRating == n), proposed = rows.Count(r => r.ManagerScore is { } s && Math.Round(s, MidpointRounding.AwayFromZero) == n) }),
                byDepartment = rows.Where(r => r.FinalRating != null).GroupBy(r => r.DepartmentId is { } d ? depts.GetValueOrDefault(d) ?? "—" : "Unassigned")
                    .Select(x => new { department = x.Key, count = x.Count(), average = Math.Round(x.Average(r => (decimal)r.FinalRating!.Value), 2) }),
            });
        });

        // ---- Reviews ----
        // scope: mine | team | all
        g.MapGet("/reviews", async (Guid cycleId, string? scope, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.PerformanceReviews.Where(r => r.ReviewCycleId == cycleId);
            scope ??= ctx.Has(Permissions.HrPerformance) ? "all" : "team";
            if (scope == "mine") q = q.Where(r => r.EmployeeId == ctx.EmployeeId);
            else if (scope == "team") { var team = await access.DirectReportsAsync(ct); q = q.Where(r => team.Contains(r.EmployeeId)); }
            else ctx.Require(Permissions.HrPerformance);
            var rows = await q.Join(db.Employees, r => r.EmployeeId, e => e.Id, (r, e) => new { r, e }).ToListAsync(ct);
            var managers = await db.Employees.Where(e => rows.Select(x => x.r.ReviewerEmployeeId).Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.FirstName + " " + e.LastName, ct);
            return Results.Ok(rows.OrderBy(x => x.e.EmployeeNumber).Select(x => new
            {
                x.r.Id, x.r.EmployeeId, x.e.EmployeeNumber, employee = x.e.FullName, x.e.JobTitle, x.r.Status, x.r.ManagerScore, x.r.FinalRating,
                reviewer = x.r.ReviewerEmployeeId is { } m ? managers.GetValueOrDefault(m) : null,
            }));
        });
        g.MapGet("/reviews/{id:guid}", async (Guid id, PerformanceService svc, HrAccess access, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var r = await svc.LoadReviewAsync(id, ct);
            await access.RequireSelfManagerOrAsync(r.EmployeeId, Permissions.HrPerformance, ct);
            var cycle = await db.ReviewCycles.FirstAsync(c => c.Id == r.ReviewCycleId, ct);
            var goals = await db.Goals.Where(x => x.ReviewCycleId == r.ReviewCycleId && x.EmployeeId == r.EmployeeId && x.Status != GoalStatus.Cancelled).OrderBy(x => x.CreatedAt).ToListAsync(ct);
            var emp = await db.Employees.FirstAsync(e => e.Id == r.EmployeeId, ct);
            var isSelf = access.IsSelf(r.EmployeeId);
            var feedback = isSelf && r.Status < ReviewStatus.Completed ? [] // 360 input is visible to the employee only once the review is complete, and never attributed
                : await db.FeedbackRequests.Where(f => f.PerformanceReviewId == id && f.Status == FeedbackStatus.Submitted).Select(f => new { f.Relationship, f.Rating, f.Strengths, f.Improvements }).ToListAsync<object>(ct);
            return Results.Ok(new
            {
                review = r, cycle, goals, employee = new { emp.Id, emp.EmployeeNumber, name = emp.FullName, emp.JobTitle }, feedback,
                canSelf = isSelf && r.Status == ReviewStatus.SelfAssessment,
                canManage = !isSelf && (ctx.Has(Permissions.HrPerformance) || await access.IsLineManagerOfAsync(r.EmployeeId, ct)) && r.Status is ReviewStatus.ManagerReview or ReviewStatus.SelfAssessment,
                canCalibrate = !isSelf && ctx.Has(Permissions.HrPerformance) && r.Status is ReviewStatus.Calibration or ReviewStatus.Completed,
                canAcknowledge = isSelf && r.Status == ReviewStatus.Completed,
            });
        });
        g.MapPost("/reviews/{id:guid}/self", async (Guid id, SelfAssessment body, PerformanceService svc, CancellationToken ct) => Results.Ok(await svc.SubmitSelfAsync(id, body, ct)));
        g.MapPost("/reviews/{id:guid}/manager", async (Guid id, ManagerAssessment body, PerformanceService svc, CancellationToken ct) => Results.Ok(await svc.SubmitManagerAsync(id, body, ct)));
        g.MapPost("/reviews/{id:guid}/calibrate", async (Guid id, CalibrateBody body, PerformanceService svc, CancellationToken ct) => Results.Ok(await svc.CalibrateAsync(id, body.FinalRating, body.Note, ct)));
        g.MapPost("/reviews/{id:guid}/acknowledge", async (Guid id, AcknowledgeBody body, PerformanceService svc, CancellationToken ct) => Results.Ok(await svc.AcknowledgeAsync(id, body.Response, ct)));

        // ---- 360 feedback ----
        g.MapPost("/reviews/{id:guid}/feedback", async (Guid id, FeedbackAsk body, PerformanceService svc, HrAccess access, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var r = await svc.LoadReviewAsync(id, ct);
            await access.RequireManagerOrAsync(r.EmployeeId, Permissions.HrPerformance, ct);
            if (body.ProviderEmployeeId == r.EmployeeId) throw new DomainException("An employee cannot give 360 feedback on themselves.", "VALIDATION");
            if (!await db.Employees.AnyAsync(e => e.Id == body.ProviderEmployeeId && e.LegalEntityId == r.LegalEntityId, ct)) throw new DomainException("Provider not found.", "VALIDATION");
            var f = new FeedbackRequest { LegalEntityId = r.LegalEntityId, PerformanceReviewId = id, SubjectEmployeeId = r.EmployeeId, ProviderEmployeeId = body.ProviderEmployeeId, Relationship = body.Relationship };
            db.FeedbackRequests.Add(f);
            audit.Record("performance.feedback.requested", nameof(FeedbackRequest), f.Id, new { body.Relationship }, r.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/performance/feedback/{f.Id}", new { f.Id });
        });
        g.MapGet("/feedback/mine", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var me = ctx.EmployeeId ?? throw new ForbiddenException("No employee linked to this user.");
            return Results.Ok(await db.FeedbackRequests.Where(f => f.ProviderEmployeeId == me && f.Status == FeedbackStatus.Requested)
                .Join(db.Employees, f => f.SubjectEmployeeId, e => e.Id, (f, e) => new { f.Id, f.Relationship, subject = e.FirstName + " " + e.LastName, e.JobTitle, f.CreatedAt }).ToListAsync(ct));
        });
        g.MapPost("/feedback/{id:guid}", async (Guid id, FeedbackAnswer body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var f = await db.FeedbackRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(FeedbackRequest), id);
            if (f.ProviderEmployeeId != ctx.EmployeeId) throw new ForbiddenException("This feedback request is addressed to someone else.");
            if (f.Status != FeedbackStatus.Requested) throw new InvalidStateException("Feedback already given.");
            if (!body.Decline && body.Rating is < 1 or > 5) throw new DomainException("Rating must be between 1 and 5.", "VALIDATION");
            f.Status = body.Decline ? FeedbackStatus.Declined : FeedbackStatus.Submitted;
            f.Rating = body.Decline ? null : body.Rating; f.Strengths = body.Strengths; f.Improvements = body.Improvements; f.SubmittedAt = DateTimeOffset.UtcNow;
            audit.Record("performance.feedback.submitted", nameof(FeedbackRequest), id, new { declined = body.Decline }, f.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { f.Id, f.Status });
        });

        // ---- Goals ----
        g.MapGet("/goals", async (Guid cycleId, Guid? employeeId, Guid? departmentId, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.Goals.Where(x => x.ReviewCycleId == cycleId && x.Status != GoalStatus.Cancelled);
            if (employeeId is { } e) { await access.RequireSelfManagerOrAsync(e, Permissions.HrPerformance, ct); q = q.Where(x => x.EmployeeId == e); }
            else if (departmentId is { } d) q = q.Where(x => x.EmployeeId == null && x.DepartmentId == d);
            else q = q.Where(x => x.EmployeeId == null); // department goals are visible to everyone for alignment
            return Results.Ok(await q.OrderBy(x => x.CreatedAt).ToListAsync(ct));
        });
        g.MapPost("/goals", async (Goal body, ITenantContext ctx, HrAccess access, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var cycle = await db.ReviewCycles.FirstOrDefaultAsync(c => c.Id == body.ReviewCycleId, ct) ?? throw new NotFoundException(nameof(ReviewCycle), body.ReviewCycleId);
            if (cycle.Status is ReviewCycleStatus.Calibration or ReviewCycleStatus.Closed) throw new InvalidStateException("Goals are locked for this cycle.");
            if (body.EmployeeId is { } e) await access.RequireSelfManagerOrAsync(e, Permissions.HrPerformance, ct);
            else ctx.Require(Permissions.HrPerformance);
            if (string.IsNullOrWhiteSpace(body.Title)) throw new DomainException("A title is required.", "VALIDATION");
            if (body.WeightPercent is < 0 or > 100) throw new DomainException("Weight must be 0–100%.", "VALIDATION");
            if (body.ParentGoalId is { } p && !await db.Goals.AnyAsync(x => x.Id == p && x.ReviewCycleId == cycle.Id && x.EmployeeId == null, ct)) throw new DomainException("Parent must be a department goal in this cycle.", "VALIDATION");
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant(); body.LegalEntityId = cycle.LegalEntityId; body.Status = GoalStatus.Draft; body.ProgressPercent = 0;
            db.Goals.Add(body);
            audit.Record("performance.goal.created", nameof(Goal), body.Id, new { body.Title, body.EmployeeId, body.WeightPercent }, cycle.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/performance/goals/{body.Id}", body);
        });
        g.MapPut("/goals/{id:guid}", async (Guid id, Goal body, ITenantContext ctx, HrAccess access, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var goal = await db.Goals.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Goal), id);
            if (goal.EmployeeId is { } e) await access.RequireSelfManagerOrAsync(e, Permissions.HrPerformance, ct); else ctx.Require(Permissions.HrPerformance);
            if (goal.Status != GoalStatus.Draft) throw new InvalidStateException("Agreed goals can't be redefined; update progress instead.");
            goal.Title = body.Title; goal.Description = body.Description; goal.Measure = body.Measure; goal.Target = body.Target; goal.WeightPercent = body.WeightPercent; goal.DueDate = body.DueDate; goal.ParentGoalId = body.ParentGoalId;
            audit.Record("performance.goal.updated", nameof(Goal), id, null, goal.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(goal);
        });
        g.MapPost("/goals/{id:guid}/progress", async (Guid id, GoalProgress body, ITenantContext ctx, HrAccess access, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var goal = await db.Goals.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Goal), id);
            if (goal.EmployeeId is { } e) await access.RequireSelfManagerOrAsync(e, Permissions.HrPerformance, ct); else ctx.Require(Permissions.HrPerformance);
            if (body.ProgressPercent is < 0 or > 100) throw new DomainException("Progress must be 0–100%.", "VALIDATION");
            goal.ProgressPercent = body.ProgressPercent;
            if (body.Status is { } s) goal.Status = s;
            else if (goal.Status == GoalStatus.Agreed && body.ProgressPercent > 0) goal.Status = GoalStatus.InProgress;
            audit.Record("performance.goal.progress", nameof(Goal), id, new { body.ProgressPercent, goal.Status }, goal.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(goal);
        });
        g.MapDelete("/goals/{id:guid}", async (Guid id, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            var goal = await db.Goals.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Goal), id);
            if (goal.EmployeeId is { } e) await access.RequireSelfManagerOrAsync(e, Permissions.HrPerformance, ct); else ctx.Require(Permissions.HrPerformance);
            goal.Status = GoalStatus.Cancelled;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        g.MapPost("/cycles/{cycleId:guid}/employees/{employeeId:guid}/agree-goals", async (Guid cycleId, Guid employeeId, PerformanceService svc, CancellationToken ct) =>
        {
            await svc.AgreeGoalsAsync(cycleId, employeeId, ct);
            return Results.NoContent();
        });

        // ---- Improvement plans ----
        g.MapGet("/plans", async (Guid? employeeId, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            var q = db.ImprovementPlans.AsQueryable();
            if (employeeId is { } e) { await access.RequireSelfManagerOrAsync(e, Permissions.HrPerformance, ct); q = q.Where(p => p.EmployeeId == e); }
            else if (!ctx.Has(Permissions.HrPerformance)) { var team = await access.DirectReportsAsync(ct); q = q.Where(p => team.Contains(p.EmployeeId) || p.EmployeeId == ctx.EmployeeId); }
            return Results.Ok(await q.OrderByDescending(p => p.StartDate).Join(db.Employees, p => p.EmployeeId, e => e.Id, (p, e) => new { plan = p, employee = e.FirstName + " " + e.LastName, e.EmployeeNumber }).ToListAsync(ct));
        });
        g.MapPost("/plans", async (ImprovementPlan body, ITenantContext ctx, HrAccess access, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            await access.RequireManagerOrAsync(body.EmployeeId, Permissions.HrPerformance, ct);
            var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == body.EmployeeId, ct) ?? throw new NotFoundException("Employee", body.EmployeeId);
            if (body.EndDate <= body.StartDate || body.Objectives.Count == 0) throw new DomainException("A plan needs an end after its start and at least one objective.", "VALIDATION");
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant(); body.LegalEntityId = emp.LegalEntityId; body.ManagerEmployeeId ??= emp.ReportingManagerId; body.Status = PlanStatus.Active;
            db.ImprovementPlans.Add(body);
            audit.Record("performance.plan.created", nameof(ImprovementPlan), body.Id, new { body.EmployeeId, body.EndDate }, emp.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/performance/plans/{body.Id}", body);
        });
        g.MapPut("/plans/{id:guid}", async (Guid id, ImprovementPlan body, HrAccess access, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var p = await db.ImprovementPlans.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(ImprovementPlan), id);
            await access.RequireManagerOrAsync(p.EmployeeId, Permissions.HrPerformance, ct);
            if (p.Status is not (PlanStatus.Active or PlanStatus.Extended)) throw new InvalidStateException("The plan is closed.");
            p.Objectives = body.Objectives; p.Status = body.Status; p.Outcome = body.Outcome;
            if (body.EndDate > p.EndDate) { p.EndDate = body.EndDate; if (p.Status == PlanStatus.Active) p.Status = PlanStatus.Extended; }
            audit.Record("performance.plan.updated", nameof(ImprovementPlan), id, new { p.Status, p.Outcome }, p.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(p);
        });

        // ---- Compensation review ----
        g.MapGet("/compensation-reviews", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.SalaryRead);
            var list = await db.CompensationReviews.Include(c => c.Lines).OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
            return Results.Ok(list.Select(c => new { c.Id, c.Name, c.ReviewCycleId, c.EffectiveDate, c.Status, totals = PerformanceService.Totals(c), c.PreparedBy, c.ApprovedAt }));
        });
        g.MapGet("/compensation-reviews/{id:guid}", async (Guid id, PerformanceService svc, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var cr = await svc.LoadCompAsync(id, ct);
            var ids = cr.Lines.Select(l => l.EmployeeId).ToList();
            var emps = await db.Employees.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
            return Results.Ok(new
            {
                review = new { cr.Id, cr.Name, cr.EffectiveDate, cr.Status, cr.MeritMatrix, cr.BonusMatrix, cr.IncreaseBudget, cr.BonusBudget, cr.PreparedBy },
                totals = PerformanceService.Totals(cr),
                canApprove = ctx.Has(Permissions.PayrollApprove) && cr.PreparedBy != ctx.UserId,
                lines = cr.Lines.OrderBy(l => emps.GetValueOrDefault(l.EmployeeId)?.EmployeeNumber).Select(l => new
                {
                    l.Id, l.EmployeeId, employeeNumber = emps.GetValueOrDefault(l.EmployeeId)?.EmployeeNumber, employee = emps.GetValueOrDefault(l.EmployeeId)?.FullName,
                    l.Rating, l.CurrentBasic, l.IncreasePercent, l.NewBasic, l.BonusAmount, l.Excluded, l.Note,
                }),
            });
        });
        g.MapPost("/compensation-reviews", async (CompReviewCreate b, PerformanceService svc, CancellationToken ct) =>
        {
            var cr = await svc.CreateCompensationReviewAsync(b.ReviewCycleId, b.Name, b.EffectiveDate, b.MeritMatrix, b.BonusMatrix, b.IncreaseBudget, b.BonusBudget, ct);
            return Results.Created($"/api/payroll/performance/compensation-reviews/{cr.Id}", new { cr.Id });
        });
        g.MapPut("/compensation-reviews/{id:guid}/lines/{lineId:guid}", async (Guid id, Guid lineId, CompLineUpdate b, PerformanceService svc, CancellationToken ct) =>
        {
            await svc.UpdateLineAsync(id, lineId, b.IncreasePercent, b.BonusAmount, b.Excluded, b.Note, ct);
            return Results.NoContent();
        });
        g.MapPost("/compensation-reviews/{id:guid}/submit", async (Guid id, PerformanceService svc, CancellationToken ct) => { await svc.SubmitCompAsync(id, ct); return Results.NoContent(); });
        g.MapPost("/compensation-reviews/{id:guid}/approve", async (Guid id, PerformanceService svc, CancellationToken ct) => { await svc.ApproveCompAsync(id, ct); return Results.NoContent(); });
        g.MapPost("/compensation-reviews/{id:guid}/reject", async (Guid id, Reason b, PerformanceService svc, CancellationToken ct) => { await svc.RejectCompAsync(id, b.Text, ct); return Results.NoContent(); });
    }
}
