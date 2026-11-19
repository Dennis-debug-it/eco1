using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Hr;

public sealed record EnrolmentResult(Guid EnrolmentId, EnrolmentStatus Status, decimal? Score, string? CertificateNumber);
public sealed record ComplianceRow(Guid EmployeeId, string EmployeeNumber, string EmployeeName, string? JobTitle, string? Department, Guid CourseId, string Course, bool Mandatory, string Status, DateOnly? CompletedOn, DateOnly? ExpiresOn);

/// <summary>
/// Learning: nominations with capacity control, completion and certificates (with expiry), cost allocation and the
/// training-cost journal, bonds, role-based compliance and NITA reimbursement claims.
/// </summary>
public sealed class LearningService(PayrollDbContext db, ITenantContext tenant, HrAccess access, AuditService audit)
{
    public const string TrainingExpenseAccount = "6180";
    public const string TrainingPayableAccount = "2196";

    public async Task<TrainingEnrolment> NominateAsync(Guid sessionId, Guid employeeId, CancellationToken ct = default)
    {
        var session = await db.TrainingSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct) ?? throw new NotFoundException(nameof(TrainingSession), sessionId);
        if (session.Status != SessionStatus.Planned) throw new InvalidStateException("Nominations are closed for this session.");
        await access.RequireSelfManagerOrAsync(employeeId, Permissions.HrLearning, ct);
        if (await db.TrainingEnrolments.AnyAsync(e => e.TrainingSessionId == sessionId && e.EmployeeId == employeeId, ct)) throw new DomainException("Already nominated for this session.", "DUPLICATE");
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId && e.LegalEntityId == session.LegalEntityId, ct) ?? throw new DomainException("Employee not in this legal entity.", "VALIDATION");
        // Managers' and HR's nominations are pre-approved; self-nominations wait for the manager or HR.
        var preApproved = !access.IsSelf(employeeId);
        var e = new TrainingEnrolment { LegalEntityId = session.LegalEntityId, TrainingSessionId = sessionId, EmployeeId = emp.Id, Status = preApproved ? EnrolmentStatus.Approved : EnrolmentStatus.Nominated };
        if (preApproved) await EnsureCapacityAsync(session, ct);
        db.TrainingEnrolments.Add(e);
        audit.Record("learning.nominated", nameof(TrainingEnrolment), e.Id, new { sessionId, employeeId, e.Status }, session.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return e;
    }

    private async Task EnsureCapacityAsync(TrainingSession s, CancellationToken ct)
    {
        var taken = await db.TrainingEnrolments.CountAsync(e => e.TrainingSessionId == s.Id && e.Status != EnrolmentStatus.Withdrawn && e.Status != EnrolmentStatus.Nominated, ct);
        if (taken >= s.Capacity) throw new InvalidStateException($"The session is full ({s.Capacity} places).");
    }

    public async Task<TrainingEnrolment> ApproveNominationAsync(Guid enrolmentId, CancellationToken ct = default)
    {
        var e = await db.TrainingEnrolments.FirstOrDefaultAsync(x => x.Id == enrolmentId, ct) ?? throw new NotFoundException(nameof(TrainingEnrolment), enrolmentId);
        await access.RequireManagerOrAsync(e.EmployeeId, Permissions.HrLearning, ct);
        if (e.Status != EnrolmentStatus.Nominated) throw new InvalidStateException("Only pending nominations can be approved.");
        await EnsureCapacityAsync(await db.TrainingSessions.FirstAsync(s => s.Id == e.TrainingSessionId, ct), ct);
        e.Status = EnrolmentStatus.Approved;
        audit.Record("learning.nomination.approved", nameof(TrainingEnrolment), e.Id, null, e.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return e;
    }

    /// <summary>Records results, issues certificates with expiry, allocates cost and posts the training-cost journal.</summary>
    public async Task<TrainingSession> CompleteSessionAsync(Guid sessionId, IReadOnlyList<EnrolmentResult> results, decimal? actualTotalCost, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrLearning);
        var s = await db.TrainingSessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct) ?? throw new NotFoundException(nameof(TrainingSession), sessionId);
        if (s.Status != SessionStatus.Planned) throw new InvalidStateException("The session is already closed.");
        var course = await db.TrainingCourses.FirstAsync(c => c.Id == s.TrainingCourseId, ct);
        var enrolments = await db.TrainingEnrolments.Where(e => e.TrainingSessionId == sessionId && e.Status != EnrolmentStatus.Withdrawn).ToListAsync(ct);
        foreach (var r in results)
        {
            var e = enrolments.FirstOrDefault(x => x.Id == r.EnrolmentId) ?? throw new DomainException("Enrolment not in this session.", "VALIDATION");
            if (r.Status is not (EnrolmentStatus.Completed or EnrolmentStatus.Failed or EnrolmentStatus.NoShow or EnrolmentStatus.Attended)) throw new DomainException("Results must be Attended, Completed, Failed or NoShow.", "VALIDATION");
            e.Status = r.Status; e.Score = r.Score; e.CertificateNumber = r.CertificateNumber;
            if (r.Status == EnrolmentStatus.Completed)
            {
                e.CertifiedOn = s.EndDate;
                e.ExpiresOn = course.CertificateValidityMonths is { } m ? s.EndDate.AddMonths(m) : null;
            }
        }
        var unrecorded = enrolments.Where(e => e.Status is EnrolmentStatus.Nominated or EnrolmentStatus.Approved).ToList();
        if (unrecorded.Count > 0) throw new DomainException($"Record a result for every participant ({unrecorded.Count} missing).", "VALIDATION");

        // Cost: actual session cost shared across those who attended, otherwise the course's per-participant cost.
        var attendees = enrolments.Where(e => e.Status is EnrolmentStatus.Attended or EnrolmentStatus.Completed or EnrolmentStatus.Failed).ToList();
        s.ActualTotalCost = actualTotalCost ?? s.ActualTotalCost;
        var share = attendees.Count == 0 ? 0 : s.ActualTotalCost is { } total ? Math.Round(total / attendees.Count, 2) : course.CostPerParticipant;
        foreach (var e in attendees) e.CostAllocated = share;
        if (s.ActualTotalCost is { } t2 && attendees.Count > 0) attendees[^1].CostAllocated += t2 - share * attendees.Count; // rounding residue

        var costTotal = attendees.Sum(e => e.CostAllocated);
        if (costTotal > 0)
        {
            var ids = attendees.Select(a => a.EmployeeId).ToList();
            var ccs = await db.Employees.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.CostCentreId, ct);
            var lines = attendees.GroupBy(a => ccs.GetValueOrDefault(a.EmployeeId))
                .Select(gr => (TrainingExpenseAccount, $"Training: {course.Title}", gr.Sum(a => a.CostAllocated), 0m, gr.Key)).ToList();
            lines.Add((TrainingPayableAccount, $"Training payable: {course.Provider ?? course.Title}", 0m, costTotal, (Guid?)null));
            var journal = GeneralJournal.Build(s.TenantId, s.LegalEntityId, "TrainingCost", $"TR-{course.Code}-{s.EndDate:yyyyMMdd}", s.EndDate,
                $"{course.Title} {s.StartDate:dd MMM}–{s.EndDate:dd MMM yyyy}: {attendees.Count} participant(s)", lines);
            db.PayrollJournals.Add(journal);
            s.CostJournalId = journal.Id;
        }
        s.Status = SessionStatus.Completed;
        audit.Record("learning.session.completed", nameof(TrainingSession), s.Id, new { attendees = attendees.Count, costTotal }, s.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return s;
    }

    /// <summary>Role-based compliance: every mandatory course for the employee's job title or department, with certificate status.</summary>
    public async Task<List<ComplianceRow>> ComplianceAsync(Guid legalEntityId, int expiringWithinDays, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrLearning);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var reqs = await db.TrainingRequirements.Where(r => r.LegalEntityId == legalEntityId).ToListAsync(ct);
        var courses = await db.TrainingCourses.Where(c => c.LegalEntityId == legalEntityId).ToDictionaryAsync(c => c.Id, ct);
        var emps = await db.Employees.Where(e => e.LegalEntityId == legalEntityId && e.TerminationDate == null).ToListAsync(ct);
        var depts = await db.Departments.Where(d => d.LegalEntityId == legalEntityId).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var completed = await db.TrainingEnrolments.Where(e => e.LegalEntityId == legalEntityId && e.Status == EnrolmentStatus.Completed)
            .Join(db.TrainingSessions, e => e.TrainingSessionId, s => s.Id, (e, s) => new { e.EmployeeId, s.TrainingCourseId, e.CertifiedOn, e.ExpiresOn }).ToListAsync(ct);
        var rows = new List<ComplianceRow>();
        foreach (var e in emps)
            foreach (var r in reqs.Where(r => (r.JobTitle == null || string.Equals(r.JobTitle, e.JobTitle, StringComparison.OrdinalIgnoreCase)) && (r.DepartmentId == null || r.DepartmentId == e.DepartmentId) && (r.JobTitle != null || r.DepartmentId != null)))
            {
                if (!courses.TryGetValue(r.TrainingCourseId, out var c)) continue;
                var latest = completed.Where(x => x.EmployeeId == e.Id && x.TrainingCourseId == c.Id).OrderByDescending(x => x.CertifiedOn).FirstOrDefault();
                var status = latest is null ? "Missing"
                    : latest.ExpiresOn is { } exp && exp < today ? "Expired"
                    : latest.ExpiresOn is { } exp2 && exp2 <= today.AddDays(expiringWithinDays) ? "Expiring"
                    : "Compliant";
                rows.Add(new ComplianceRow(e.Id, e.EmployeeNumber, e.FullName, e.JobTitle, e.DepartmentId is { } d ? depts.GetValueOrDefault(d) : null, c.Id, c.Title, r.Mandatory, status, latest?.CertifiedOn, latest?.ExpiresOn));
            }
        return rows.DistinctBy(r => (r.EmployeeId, r.CourseId)).OrderBy(r => r.Status == "Compliant").ThenBy(r => r.EmployeeNumber).ToList();
    }

    /// <summary>Builds a NITA reimbursement claim from completed, NITA-approved training not yet claimed.</summary>
    public async Task<NitaClaim> CreateNitaClaimAsync(Guid legalEntityId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrLearning); tenant.RequireLegalEntity(legalEntityId);
        var claimed = await db.NitaClaimLines.Select(l => l.TrainingEnrolmentId).ToListAsync(ct);
        var eligible = await db.TrainingEnrolments.Where(e => e.LegalEntityId == legalEntityId && e.Status == EnrolmentStatus.Completed && e.CostAllocated > 0 && !claimed.Contains(e.Id))
            .Join(db.TrainingSessions.Where(s => s.EndDate >= from && s.EndDate <= to), e => e.TrainingSessionId, s => s.Id, (e, s) => new { e, s })
            .Join(db.TrainingCourses.Where(c => c.NitaApproved), x => x.s.TrainingCourseId, c => c.Id, (x, c) => new { x.e, x.s, c })
            .ToListAsync(ct);
        if (eligible.Count == 0) throw new InvalidStateException("No completed NITA-approved training in this period that hasn't been claimed.");
        var count = await db.NitaClaims.CountAsync(c => c.LegalEntityId == legalEntityId, ct) + 1;
        var claim = new NitaClaim { LegalEntityId = legalEntityId, Reference = $"NITA-{to:yyyyMM}-{count:D3}", PeriodStart = from, PeriodEnd = to };
        foreach (var x in eligible)
            claim.Lines.Add(new NitaClaimLine { TenantId = tenant.RequireTenant(), NitaClaimId = claim.Id, TrainingEnrolmentId = x.e.Id, EmployeeId = x.e.EmployeeId, CourseTitle = x.c.Title, NitaCourseCode = x.c.NitaCourseCode, CompletedOn = x.s.EndDate, Amount = x.e.CostAllocated });
        claim.TotalClaimed = claim.Lines.Sum(l => l.Amount);
        db.NitaClaims.Add(claim);
        audit.Record("learning.nita.claim.created", nameof(NitaClaim), claim.Id, new { claim.Reference, claim.TotalClaimed, lines = claim.Lines.Count }, legalEntityId);
        await db.SaveChangesAsync(ct);
        return claim;
    }

    /// <summary>NITA levy paid through payroll in a period (from finalised runs) compared with amounts claimed back.</summary>
    public async Task<object> NitaPositionAsync(Guid legalEntityId, int year, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrLearning);
        var levy = await db.PayrollRuns.Where(r => r.LegalEntityId == legalEntityId && r.Status == PayrollRunStatus.Finalised)
            .Join(db.PayrollPeriods.Where(p => p.PeriodEnd.Year == year), r => r.PayrollPeriodId, p => p.Id, (r, p) => r.TotalNita).SumAsync(ct);
        var claims = await db.NitaClaims.Where(c => c.LegalEntityId == legalEntityId && c.PeriodEnd.Year == year).ToListAsync(ct);
        return new
        {
            year, levyPaid = levy, claimed = claims.Where(c => c.Status != ClaimStatus.Rejected).Sum(c => c.TotalClaimed),
            approved = claims.Sum(c => c.AmountApproved ?? 0), received = claims.Where(c => c.Status == ClaimStatus.Paid).Sum(c => c.AmountApproved ?? 0),
        };
    }
}
