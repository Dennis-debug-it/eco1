using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Hr;

public sealed record SelfAssessment(List<GoalRating> GoalRatings, List<CompetencyRating> CompetencyRatings, string? Comments);
public sealed record ManagerAssessment(List<GoalRating> GoalRatings, List<CompetencyRating> CompetencyRatings, string? Comments);

/// <summary>
/// Performance cycle: goal setting → self assessment → manager review → HR calibration → employee acknowledgement,
/// plus the compensation review that turns final ratings into effective-dated salary changes and bonuses.
/// </summary>
public sealed class PerformanceService(PayrollDbContext db, ITenantContext tenant, HrAccess access, AuditService audit, SystemComponents components)
{
    public async Task<ReviewCycle> AdvanceAsync(Guid cycleId, ReviewCycleStatus to, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrPerformance);
        var c = await db.ReviewCycles.FirstOrDefaultAsync(x => x.Id == cycleId, ct) ?? throw new NotFoundException(nameof(ReviewCycle), cycleId);
        tenant.RequireLegalEntity(c.LegalEntityId);
        var allowed = (c.Status, to) switch
        {
            (ReviewCycleStatus.Draft, ReviewCycleStatus.GoalSetting) => true,
            (ReviewCycleStatus.GoalSetting, ReviewCycleStatus.Review) => true,
            (ReviewCycleStatus.Review, ReviewCycleStatus.Calibration) => true,
            (ReviewCycleStatus.Calibration, ReviewCycleStatus.Closed) => true,
            _ => false,
        };
        if (!allowed) throw new InvalidStateException($"A cycle cannot move from {c.Status} to {to}.");

        if (to == ReviewCycleStatus.GoalSetting)
        {
            // One review per employee in scope, routed to their line manager.
            var employees = await db.Employees.Where(e => e.LegalEntityId == c.LegalEntityId && e.HireDate <= c.PeriodEnd && (e.TerminationDate == null || e.TerminationDate >= c.PeriodEnd)).ToListAsync(ct);
            if (c.Type == ReviewCycleType.Probation)
                employees = employees.Where(e => e.EmploymentType == EmploymentType.Probation || (e.ProbationEndDate is { } p && p >= c.PeriodStart && p <= c.PeriodEnd)).ToList();
            var existing = await db.PerformanceReviews.Where(r => r.ReviewCycleId == c.Id).Select(r => r.EmployeeId).ToListAsync(ct);
            foreach (var e in employees.Where(e => !existing.Contains(e.Id)))
                db.PerformanceReviews.Add(new PerformanceReview
                {
                    LegalEntityId = c.LegalEntityId, ReviewCycleId = c.Id, EmployeeId = e.Id, ReviewerEmployeeId = e.ReportingManagerId,
                    CompetencyRatings = c.Competencies.Select(x => new CompetencyRating { Competency = x }).ToList(),
                });
        }
        if (to == ReviewCycleStatus.Review)
            await db.PerformanceReviews.Where(r => r.ReviewCycleId == c.Id && r.Status == ReviewStatus.NotStarted)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewStatus.SelfAssessment), ct);
        if (to == ReviewCycleStatus.Closed)
        {
            var uncalibrated = await db.PerformanceReviews.CountAsync(r => r.ReviewCycleId == c.Id && r.FinalRating == null, ct);
            if (uncalibrated > 0) throw new InvalidStateException($"{uncalibrated} review(s) still need a final rating.");
        }
        c.Status = to;
        audit.Record("performance.cycle.advanced", nameof(ReviewCycle), c.Id, new { to }, c.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>Manager (or HR) agrees an employee's goals; weights must total 100%.</summary>
    public async Task AgreeGoalsAsync(Guid cycleId, Guid employeeId, CancellationToken ct = default)
    {
        await access.RequireManagerOrAsync(employeeId, Permissions.HrPerformance, ct);
        var goals = await db.Goals.Where(g => g.ReviewCycleId == cycleId && g.EmployeeId == employeeId && g.Status != GoalStatus.Cancelled).ToListAsync(ct);
        if (goals.Count == 0) throw new InvalidStateException("The employee has no goals for this cycle.");
        var total = goals.Sum(g => g.WeightPercent);
        if (total != 100m) throw new DomainException($"Goal weights must total 100% (currently {total:0.##}%).", "VALIDATION");
        foreach (var g in goals.Where(g => g.Status == GoalStatus.Draft)) g.Status = GoalStatus.Agreed;
        audit.Record("performance.goals.agreed", "Goal", employeeId, new { cycleId, count = goals.Count }, goals[0].LegalEntityId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PerformanceReview> SubmitSelfAsync(Guid reviewId, SelfAssessment body, CancellationToken ct = default)
    {
        var r = await LoadReviewAsync(reviewId, ct);
        if (!access.IsSelf(r.EmployeeId)) throw new ForbiddenException("Only the employee can submit their self-assessment.");
        if (r.Status != ReviewStatus.SelfAssessment) throw new InvalidStateException("The self-assessment is not open.");
        Validate(body.GoalRatings.Select(x => x.SelfRating), body.CompetencyRatings.Select(x => x.SelfRating));
        Merge(r, body.GoalRatings, body.CompetencyRatings, self: true);
        r.SelfComments = body.Comments;
        r.Status = ReviewStatus.ManagerReview;
        audit.Record("performance.self.submitted", nameof(PerformanceReview), r.Id, null, r.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return r;
    }

    public async Task<PerformanceReview> SubmitManagerAsync(Guid reviewId, ManagerAssessment body, CancellationToken ct = default)
    {
        var r = await LoadReviewAsync(reviewId, ct);
        await access.RequireManagerOrAsync(r.EmployeeId, Permissions.HrPerformance, ct);
        if (r.Status is not (ReviewStatus.ManagerReview or ReviewStatus.SelfAssessment)) throw new InvalidStateException("The manager review is not open.");
        Validate(body.GoalRatings.Select(x => x.ManagerRating), body.CompetencyRatings.Select(x => x.ManagerRating));
        Merge(r, body.GoalRatings, body.CompetencyRatings, self: false);
        r.ManagerComments = body.Comments;
        var cycle = await db.ReviewCycles.FirstAsync(c => c.Id == r.ReviewCycleId, ct);
        var goals = await db.Goals.Where(g => g.ReviewCycleId == r.ReviewCycleId && g.EmployeeId == r.EmployeeId && g.Status != GoalStatus.Cancelled).ToListAsync(ct);
        r.ManagerScore = Score(cycle.GoalWeightPercent, goals.Select(g => (g.WeightPercent, r.GoalRatings.FirstOrDefault(x => x.GoalId == g.Id)?.ManagerRating)).ToList(),
            r.CompetencyRatings.Select(x => x.ManagerRating).ToList());
        if (r.ManagerScore is null) throw new DomainException("Rate at least one goal or competency.", "VALIDATION");
        r.Status = ReviewStatus.Calibration;
        audit.Record("performance.manager.submitted", nameof(PerformanceReview), r.Id, new { r.ManagerScore }, r.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return r;
    }

    /// <summary>Overall 1–5 score: goal component weighted by goal weights, competency component averaged, blended by the cycle's goal weight.</summary>
    public static decimal? Score(decimal goalWeightPercent, IReadOnlyList<(decimal Weight, int? Rating)> goals, IReadOnlyList<int?> competencies)
    {
        var ratedGoals = goals.Where(g => g.Rating is not null && g.Weight > 0).ToList();
        decimal? goalScore = ratedGoals.Count == 0 ? null : ratedGoals.Sum(g => g.Weight * g.Rating!.Value) / ratedGoals.Sum(g => g.Weight);
        var rated = competencies.Where(c => c is not null).Select(c => (decimal)c!.Value).ToList();
        decimal? compScore = rated.Count == 0 ? null : rated.Average();
        if (goalScore is null && compScore is null) return null;
        if (goalScore is null) return Math.Round(compScore!.Value, 2);
        if (compScore is null) return Math.Round(goalScore.Value, 2);
        var w = goalWeightPercent / 100m;
        return Math.Round(goalScore.Value * w + compScore.Value * (1 - w), 2);
    }

    public async Task<PerformanceReview> CalibrateAsync(Guid reviewId, int finalRating, string? note, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrPerformance);
        if (finalRating is < 1 or > 5) throw new DomainException("Final rating must be between 1 and 5.", "VALIDATION");
        var r = await LoadReviewAsync(reviewId, ct);
        if (access.IsSelf(r.EmployeeId)) throw new ForbiddenException("You cannot calibrate your own rating.");
        if (r.Status is not (ReviewStatus.Calibration or ReviewStatus.Completed)) throw new InvalidStateException("The manager review must be submitted first.");
        var proposed = r.ManagerScore is { } s ? (int)Math.Round(s, MidpointRounding.AwayFromZero) : (int?)null;
        if (proposed is not null && proposed != finalRating && string.IsNullOrWhiteSpace(note))
            throw new DomainException("Explain why the calibrated rating differs from the manager's score.", "VALIDATION");
        r.FinalRating = finalRating; r.CalibrationNote = note; r.Status = ReviewStatus.Completed;
        audit.Record("performance.calibrated", nameof(PerformanceReview), r.Id, new { proposed, finalRating, note }, r.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return r;
    }

    public async Task<PerformanceReview> AcknowledgeAsync(Guid reviewId, string? response, CancellationToken ct = default)
    {
        var r = await LoadReviewAsync(reviewId, ct);
        if (!access.IsSelf(r.EmployeeId)) throw new ForbiddenException("Only the employee can acknowledge their review.");
        if (r.Status != ReviewStatus.Completed) throw new InvalidStateException("The review is not complete.");
        r.Status = ReviewStatus.Acknowledged; r.AcknowledgedAt = DateTimeOffset.UtcNow; r.EmployeeResponse = response;
        audit.Record("performance.acknowledged", nameof(PerformanceReview), r.Id, null, r.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return r;
    }

    private static void Validate(IEnumerable<int?> goals, IEnumerable<int?> competencies)
    {
        if (goals.Concat(competencies).Any(x => x is < 1 or > 5)) throw new DomainException("Ratings must be between 1 and 5.", "VALIDATION");
    }

    private static void Merge(PerformanceReview r, List<GoalRating> goals, List<CompetencyRating> comps, bool self)
    {
        var gr = r.GoalRatings.ToDictionary(x => x.GoalId);
        foreach (var g in goals)
        {
            if (!gr.TryGetValue(g.GoalId, out var existing)) { existing = new GoalRating { GoalId = g.GoalId }; gr[g.GoalId] = existing; }
            if (self) existing.SelfRating = g.SelfRating; else existing.ManagerRating = g.ManagerRating;
            existing.Comment = g.Comment ?? existing.Comment;
        }
        r.GoalRatings = gr.Values.ToList();
        var cr = r.CompetencyRatings.ToDictionary(x => x.Competency);
        foreach (var c in comps)
        {
            if (!cr.TryGetValue(c.Competency, out var existing)) { existing = new CompetencyRating { Competency = c.Competency }; cr[c.Competency] = existing; }
            if (self) existing.SelfRating = c.SelfRating; else existing.ManagerRating = c.ManagerRating;
        }
        r.CompetencyRatings = cr.Values.ToList();
    }

    public async Task<PerformanceReview> LoadReviewAsync(Guid id, CancellationToken ct)
    {
        var r = await db.PerformanceReviews.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(PerformanceReview), id);
        tenant.RequireLegalEntity(r.LegalEntityId);
        return r;
    }

    // ---------------------------------------------------------------- Compensation review

    public async Task<CompensationReview> CreateCompensationReviewAsync(Guid cycleId, string name, DateOnly effectiveDate, Dictionary<int, decimal>? merit, Dictionary<int, decimal>? bonus, decimal? increaseBudget, decimal? bonusBudget, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrPerformance); tenant.Require(Permissions.SalaryWrite);
        var cycle = await db.ReviewCycles.FirstOrDefaultAsync(c => c.Id == cycleId, ct) ?? throw new NotFoundException(nameof(ReviewCycle), cycleId);
        if (cycle.Status is not (ReviewCycleStatus.Calibration or ReviewCycleStatus.Closed)) throw new InvalidStateException("Run the compensation review after calibration.");
        var cr = new CompensationReview
        {
            LegalEntityId = cycle.LegalEntityId, ReviewCycleId = cycle.Id, Name = name, EffectiveDate = effectiveDate, IncreaseBudget = increaseBudget, BonusBudget = bonusBudget,
            PreparedBy = tenant.UserId,
        };
        if (merit is not null) cr.MeritMatrix = merit;
        if (bonus is not null) cr.BonusMatrix = bonus;
        var reviews = await db.PerformanceReviews.Where(r => r.ReviewCycleId == cycleId && r.FinalRating != null).ToListAsync(ct);
        var ids = reviews.Select(r => r.EmployeeId).ToList();
        var salaries = (await db.EmployeeSalaryAssignments.Where(a => ids.Contains(a.EmployeeId) && a.EffectiveFrom <= effectiveDate && (a.EffectiveTo == null || a.EffectiveTo >= effectiveDate)).ToListAsync(ct))
            .GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.EffectiveFrom).First().BasicSalary);
        var leavers = await db.Employees.Where(e => ids.Contains(e.Id) && e.TerminationDate != null && e.TerminationDate < effectiveDate).Select(e => e.Id).ToListAsync(ct);
        foreach (var r in reviews.Where(r => salaries.ContainsKey(r.EmployeeId)))
        {
            var basic = salaries[r.EmployeeId];
            var line = new CompensationReviewLine { TenantId = cycle.TenantId, CompensationReviewId = cr.Id, EmployeeId = r.EmployeeId, CurrentBasic = basic, Rating = r.FinalRating };
            ApplyMatrix(cr, line);
            if (leavers.Contains(r.EmployeeId)) { line.Excluded = true; line.Note = "Leaves before the effective date"; }
            cr.Lines.Add(line);
        }
        db.CompensationReviews.Add(cr);
        audit.Record("compensation.review.created", nameof(CompensationReview), cr.Id, new { cycleId, lines = cr.Lines.Count }, cr.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return cr;
    }

    public static void ApplyMatrix(CompensationReview cr, CompensationReviewLine line)
    {
        var rating = line.Rating ?? 0;
        line.IncreasePercent = cr.MeritMatrix.GetValueOrDefault(rating);
        line.NewBasic = Math.Round(line.CurrentBasic * (1 + line.IncreasePercent / 100m), 0, MidpointRounding.AwayFromZero);
        line.BonusAmount = Math.Round(line.CurrentBasic * cr.BonusMatrix.GetValueOrDefault(rating) / 100m, 0, MidpointRounding.AwayFromZero);
    }

    public async Task<CompensationReview> UpdateLineAsync(Guid reviewId, Guid lineId, decimal increasePercent, decimal bonusAmount, bool excluded, string? note, CancellationToken ct = default)
    {
        tenant.Require(Permissions.SalaryWrite);
        var cr = await LoadCompAsync(reviewId, ct);
        if (cr.Status is not (CompensationReviewStatus.Draft or CompensationReviewStatus.Rejected)) throw new InvalidStateException("Only a draft review can be edited.");
        var line = cr.Lines.FirstOrDefault(l => l.Id == lineId) ?? throw new NotFoundException(nameof(CompensationReviewLine), lineId);
        if (increasePercent is < -50 or > 100 || bonusAmount < 0) throw new DomainException("Implausible increase or bonus.", "VALIDATION");
        line.IncreasePercent = increasePercent; line.NewBasic = Math.Round(line.CurrentBasic * (1 + increasePercent / 100m), 0, MidpointRounding.AwayFromZero);
        line.BonusAmount = bonusAmount; line.Excluded = excluded; line.Note = note;
        audit.Record("compensation.line.updated", nameof(CompensationReview), cr.Id, new { line.EmployeeId, increasePercent, bonusAmount, excluded }, cr.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return cr;
    }

    public async Task<CompensationReview> SubmitCompAsync(Guid id, CancellationToken ct = default)
    {
        tenant.Require(Permissions.SalaryWrite);
        var cr = await LoadCompAsync(id, ct);
        if (cr.Status is not (CompensationReviewStatus.Draft or CompensationReviewStatus.Rejected)) throw new InvalidStateException("Only a draft review can be submitted.");
        cr.Status = CompensationReviewStatus.Submitted; cr.PreparedBy = tenant.UserId;
        audit.Record("compensation.review.submitted", nameof(CompensationReview), id, Totals(cr), cr.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return cr;
    }

    public static object Totals(CompensationReview cr)
    {
        var lines = cr.Lines.Where(l => !l.Excluded).ToList();
        var monthlyIncrease = lines.Sum(l => l.NewBasic - l.CurrentBasic);
        return new { employees = lines.Count, monthlyIncrease, annualIncrease = monthlyIncrease * 12, bonuses = lines.Sum(l => l.BonusAmount), cr.IncreaseBudget, cr.BonusBudget };
    }

    /// <summary>Approval (maker-checker) applies new effective-dated salaries and schedules bonuses for payroll.</summary>
    public async Task<CompensationReview> ApproveCompAsync(Guid id, CancellationToken ct = default)
    {
        tenant.Require(Permissions.PayrollApprove);
        var cr = await LoadCompAsync(id, ct);
        if (cr.Status != CompensationReviewStatus.Submitted) throw new InvalidStateException("Only a submitted review can be approved.");
        if (cr.PreparedBy == tenant.UserId && !tenant.IsPlatformAdministrator) throw new ForbiddenException("The preparer cannot approve the compensation review (separation of duties).");
        var lines = cr.Lines.Where(l => !l.Excluded).ToList();
        var increase = lines.Sum(l => (l.NewBasic - l.CurrentBasic) * 12);
        if (cr.IncreaseBudget is { } ib && increase > ib) throw new InvalidStateException($"Annual increase cost {increase:N0} exceeds the budget {ib:N0}.");
        if (cr.BonusBudget is { } bb && lines.Sum(l => l.BonusAmount) > bb) throw new InvalidStateException($"Bonuses {lines.Sum(l => l.BonusAmount):N0} exceed the budget {bb:N0}.");

        var finalisedThrough = await db.PayrollRuns.Where(r => r.LegalEntityId == cr.LegalEntityId && r.Status == PayrollRunStatus.Finalised)
            .Join(db.PayrollPeriods, r => r.PayrollPeriodId, p => p.Id, (r, p) => p.PeriodEnd).OrderByDescending(d => d).FirstOrDefaultAsync(ct);
        if (finalisedThrough != default && cr.EffectiveDate <= finalisedThrough)
            throw new InvalidStateException($"Payroll is finalised through {finalisedThrough:yyyy-MM-dd}; choose an effective date after that.");

        var bonusComponent = lines.Any(l => l.BonusAmount > 0) ? await components.EnsureAsync(cr.TenantId, cr.LegalEntityId, "BONUS", ct) : null;
        var ids = lines.Select(l => l.EmployeeId).ToList();
        var current = await db.EmployeeSalaryAssignments.Include(a => a.Lines).Where(a => ids.Contains(a.EmployeeId) && a.EffectiveTo == null).ToListAsync(ct);
        foreach (var line in lines)
        {
            if (line.NewBasic != line.CurrentBasic)
            {
                var cur = current.Where(a => a.EmployeeId == line.EmployeeId).OrderByDescending(a => a.EffectiveFrom).FirstOrDefault();
                if (cur is not null && cur.EffectiveFrom >= cr.EffectiveDate) throw new InvalidStateException("An employee already has a salary change on or after the effective date.");
                if (cur is not null) cur.EffectiveTo = cr.EffectiveDate.AddDays(-1);
                var next = new EmployeeSalaryAssignment
                {
                    LegalEntityId = cr.LegalEntityId, EmployeeId = line.EmployeeId, SalaryStructureId = cur?.SalaryStructureId, BasicSalary = line.NewBasic,
                    // A daily-paid worker's raise applies to the daily rate, in the same proportion as the monthly equivalent.
                    PayBasis = cur?.PayBasis ?? PayBasis.Monthly,
                    DailyRate = cur is { PayBasis: PayBasis.Daily, DailyRate: { } dr } && line.CurrentBasic > 0 ? Math.Round(dr * line.NewBasic / line.CurrentBasic, 2, MidpointRounding.AwayFromZero) : null,
                    EffectiveFrom = cr.EffectiveDate, ChangeReason = $"{cr.Name}: rating {line.Rating}, +{line.IncreasePercent:0.##}%", ApprovedBy = tenant.UserId,
                };
                foreach (var l in cur?.Lines ?? []) next.Lines.Add(new EmployeeSalaryAssignmentLine { TenantId = cr.TenantId, EmployeeSalaryAssignmentId = next.Id, SalaryComponentId = l.SalaryComponentId, Method = l.Method, Amount = l.Amount, Rate = l.Rate });
                db.EmployeeSalaryAssignments.Add(next);
            }
            if (line.BonusAmount > 0)
                db.ScheduledPayItems.Add(new ScheduledPayItem
                {
                    LegalEntityId = cr.LegalEntityId, EmployeeId = line.EmployeeId, SalaryComponentId = bonusComponent!.Id, Amount = line.BonusAmount, DueDate = cr.EffectiveDate,
                    Description = $"Performance bonus — {cr.Name}", SourceType = nameof(CompensationReview), SourceId = cr.Id,
                });
        }
        cr.Status = CompensationReviewStatus.Applied; cr.ApprovedBy = tenant.UserId; cr.ApprovedAt = DateTimeOffset.UtcNow;
        audit.Record("compensation.review.applied", nameof(CompensationReview), id, Totals(cr), cr.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return cr;
    }

    public async Task<CompensationReview> RejectCompAsync(Guid id, string reason, CancellationToken ct = default)
    {
        tenant.Require(Permissions.PayrollApprove);
        var cr = await LoadCompAsync(id, ct);
        if (cr.Status != CompensationReviewStatus.Submitted) throw new InvalidStateException("Only a submitted review can be rejected.");
        cr.Status = CompensationReviewStatus.Rejected;
        audit.Record("compensation.review.rejected", nameof(CompensationReview), id, new { reason }, cr.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return cr;
    }

    public async Task<CompensationReview> LoadCompAsync(Guid id, CancellationToken ct)
    {
        tenant.Require(Permissions.SalaryRead);
        var cr = await db.CompensationReviews.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(CompensationReview), id);
        tenant.RequireLegalEntity(cr.LegalEntityId);
        return cr;
    }
}
