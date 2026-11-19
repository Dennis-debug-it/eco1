using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Ecosys.Payroll.Infrastructure.Services.Hr;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record NominateBody(Guid EmployeeId);
public sealed record CompleteSessionBody(List<EnrolmentResult> Results, decimal? ActualTotalCost);
public sealed record BondBody(int Months, decimal? Amount, string? Notes);
public sealed record NitaClaimCreate(Guid LegalEntityId, DateOnly From, DateOnly To);
public sealed record NitaClaimOutcome(ClaimStatus Status, decimal? AmountApproved, string? NitaReference);

public static class LearningEndpoints
{
    public static void MapLearning(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/learning").RequireAuthorization();

        // ---- Catalogue ----
        g.MapGet("/courses", async (PayrollDbContext db, CancellationToken ct) => Results.Ok(await db.TrainingCourses.OrderBy(c => c.Title).ToListAsync(ct)));
        g.MapPost("/courses", async (TrainingCourse body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrLearning); ctx.RequireLegalEntity(body.LegalEntityId);
            if (string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.Title)) throw new DomainException("Code and title are required.", "VALIDATION");
            if (body.CostPerParticipant < 0 || body.DurationHours < 0) throw new DomainException("Cost and duration cannot be negative.", "VALIDATION");
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant(); body.Code = body.Code.Trim().ToUpperInvariant();
            db.TrainingCourses.Add(body);
            audit.Record("learning.course.created", nameof(TrainingCourse), body.Id, new { body.Code, body.NitaApproved }, body.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/learning/courses/{body.Id}", body);
        });
        g.MapPut("/courses/{id:guid}", async (Guid id, TrainingCourse body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrLearning);
            var c = await db.TrainingCourses.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(TrainingCourse), id);
            c.Title = body.Title; c.Provider = body.Provider; c.Delivery = body.Delivery; c.DurationHours = body.DurationHours; c.CostPerParticipant = body.CostPerParticipant;
            c.NitaApproved = body.NitaApproved; c.NitaCourseCode = body.NitaCourseCode; c.CertificateValidityMonths = body.CertificateValidityMonths; c.Description = body.Description; c.IsActive = body.IsActive;
            audit.Record("learning.course.updated", nameof(TrainingCourse), id, null, c.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(c);
        });
        g.MapGet("/requirements", async (PayrollDbContext db, CancellationToken ct) =>
            Results.Ok(await db.TrainingRequirements.Join(db.TrainingCourses, r => r.TrainingCourseId, c => c.Id, (r, c) => new { r.Id, r.TrainingCourseId, course = c.Title, r.JobTitle, r.DepartmentId, r.Mandatory }).ToListAsync(ct)));
        g.MapPost("/requirements", async (TrainingRequirement body, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrLearning); ctx.RequireLegalEntity(body.LegalEntityId);
            if (body.JobTitle is null && body.DepartmentId is null) throw new DomainException("Target a job title, a department, or both.", "VALIDATION");
            if (!await db.TrainingCourses.AnyAsync(c => c.Id == body.TrainingCourseId && c.LegalEntityId == body.LegalEntityId, ct)) throw new DomainException("Course not found.", "VALIDATION");
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant();
            db.TrainingRequirements.Add(body);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/learning/requirements/{body.Id}", body);
        });
        g.MapDelete("/requirements/{id:guid}", async (Guid id, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrLearning);
            var r = await db.TrainingRequirements.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(TrainingRequirement), id);
            db.TrainingRequirements.Remove(r);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- Sessions ----
        g.MapGet("/sessions", async (PayrollDbContext db, CancellationToken ct) =>
        {
            var rows = await db.TrainingSessions.Join(db.TrainingCourses, s => s.TrainingCourseId, c => c.Id, (s, c) => new { s, c }).OrderByDescending(x => x.s.StartDate).ToListAsync(ct);
            var counts = await db.TrainingEnrolments.Where(e => e.Status != EnrolmentStatus.Withdrawn).GroupBy(e => e.TrainingSessionId).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
            return Results.Ok(rows.Select(x => new
            {
                x.s.Id, x.s.TrainingCourseId, course = x.c.Title, x.c.Code, x.c.NitaApproved, x.s.StartDate, x.s.EndDate, x.s.Venue, x.s.Trainer, x.s.Capacity,
                enrolled = counts.GetValueOrDefault(x.s.Id), x.s.Status, x.s.ActualTotalCost, x.c.CostPerParticipant, x.s.CostJournalId,
            }));
        });
        g.MapPost("/sessions", async (TrainingSession body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrLearning);
            var course = await db.TrainingCourses.FirstOrDefaultAsync(c => c.Id == body.TrainingCourseId, ct) ?? throw new NotFoundException(nameof(TrainingCourse), body.TrainingCourseId);
            if (body.EndDate < body.StartDate || body.Capacity < 1) throw new DomainException("Check the dates and capacity.", "VALIDATION");
            body.Id = Guid.NewGuid(); body.TenantId = ctx.RequireTenant(); body.LegalEntityId = course.LegalEntityId; body.Status = SessionStatus.Planned;
            db.TrainingSessions.Add(body);
            audit.Record("learning.session.created", nameof(TrainingSession), body.Id, new { course.Code, body.StartDate }, course.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/learning/sessions/{body.Id}", body);
        });
        g.MapGet("/sessions/{id:guid}/enrolments", async (Guid id, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            var team = await access.DirectReportsAsync(ct);
            var q = db.TrainingEnrolments.Where(e => e.TrainingSessionId == id);
            if (!ctx.Has(Permissions.HrLearning)) q = q.Where(e => team.Contains(e.EmployeeId) || e.EmployeeId == ctx.EmployeeId);
            return Results.Ok(await q.Join(db.Employees, e => e.EmployeeId, x => x.Id, (e, x) => new
            {
                e.Id, e.EmployeeId, x.EmployeeNumber, employee = x.FirstName + " " + x.LastName, x.JobTitle, e.Status, e.Score, e.CertificateNumber, e.CertifiedOn, e.ExpiresOn, e.CostAllocated, e.CertificateFileId,
            }).OrderBy(x => x.EmployeeNumber).ToListAsync(ct));
        });
        g.MapPost("/sessions/{id:guid}/nominate", async (Guid id, NominateBody body, LearningService svc, CancellationToken ct) => Results.Ok(await svc.NominateAsync(id, body.EmployeeId, ct)));
        g.MapPost("/enrolments/{id:guid}/approve", async (Guid id, LearningService svc, CancellationToken ct) => Results.Ok(await svc.ApproveNominationAsync(id, ct)));
        g.MapPost("/enrolments/{id:guid}/withdraw", async (Guid id, ITenantContext ctx, HrAccess access, PayrollDbContext db, CancellationToken ct) =>
        {
            var e = await db.TrainingEnrolments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(TrainingEnrolment), id);
            await access.RequireSelfManagerOrAsync(e.EmployeeId, Permissions.HrLearning, ct);
            if (e.Status is not (EnrolmentStatus.Nominated or EnrolmentStatus.Approved)) throw new InvalidStateException("Results are already recorded.");
            e.Status = EnrolmentStatus.Withdrawn;
            await db.SaveChangesAsync(ct);
            return Results.Ok(e);
        });
        g.MapPost("/sessions/{id:guid}/complete", async (Guid id, CompleteSessionBody body, LearningService svc, CancellationToken ct) => Results.Ok(await svc.CompleteSessionAsync(id, body.Results, body.ActualTotalCost, ct)));
        g.MapPost("/sessions/{id:guid}/cancel", async (Guid id, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrLearning);
            var s = await db.TrainingSessions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(TrainingSession), id);
            if (s.Status != SessionStatus.Planned) throw new InvalidStateException("Only a planned session can be cancelled.");
            s.Status = SessionStatus.Cancelled;
            await db.TrainingEnrolments.Where(e => e.TrainingSessionId == id).ExecuteUpdateAsync(x => x.SetProperty(e => e.Status, EnrolmentStatus.Withdrawn), ct);
            audit.Record("learning.session.cancelled", nameof(TrainingSession), id, null, s.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(s);
        });
        g.MapPost("/enrolments/{id:guid}/certificate", async (Guid id, IFormFile file, ITenantContext ctx, PayrollDbContext db, FileStore files, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrLearning);
            var e = await db.TrainingEnrolments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(TrainingEnrolment), id);
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var f = await files.SaveAsync(file.FileName, ms.ToArray(), nameof(TrainingEnrolment), e.Id, e.EmployeeId, ct);
            e.CertificateFileId = f.Id;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { f.Id, f.FileName });
        }).DisableAntiforgery();

        // ---- Bonds ----
        g.MapPost("/enrolments/{id:guid}/bond", async (Guid id, BondBody body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrLearning);
            var e = await db.TrainingEnrolments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(TrainingEnrolment), id);
            if (e.Status != EnrolmentStatus.Completed) throw new InvalidStateException("A bond applies to completed training.");
            var amount = body.Amount ?? e.CostAllocated;
            if (body.Months is < 1 or > 60 || amount <= 0) throw new DomainException("A bond needs 1–60 months and a positive amount.", "VALIDATION");
            var bond = new TrainingBond { LegalEntityId = e.LegalEntityId, EmployeeId = e.EmployeeId, TrainingEnrolmentId = e.Id, Amount = amount, StartDate = e.CertifiedOn ?? BusinessClock.Today(), Months = body.Months, Notes = body.Notes };
            db.TrainingBonds.Add(bond);
            audit.Record("learning.bond.created", nameof(TrainingBond), bond.Id, new { e.EmployeeId, amount, body.Months }, e.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/payroll/learning/bonds/{bond.Id}", bond);
        });
        g.MapGet("/bonds", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrLearning);
            var today = BusinessClock.Today();
            var rows = await db.TrainingBonds.Join(db.Employees, b => b.EmployeeId, e => e.Id, (b, e) => new { b, employee = e.FirstName + " " + e.LastName, e.EmployeeNumber }).ToListAsync(ct);
            return Results.Ok(rows.Select(x => new { x.b.Id, x.employee, x.EmployeeNumber, x.b.Amount, x.b.StartDate, x.b.Months, endsOn = x.b.StartDate.AddMonths(x.b.Months), x.b.Status, outstanding = x.b.OutstandingOn(today) }));
        });

        // ---- My learning ----
        g.MapGet("/mine", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            var me = ctx.EmployeeId ?? throw new ForbiddenException("No employee linked to this user.");
            return Results.Ok(await db.TrainingEnrolments.Where(e => e.EmployeeId == me)
                .Join(db.TrainingSessions, e => e.TrainingSessionId, s => s.Id, (e, s) => new { e, s })
                .Join(db.TrainingCourses, x => x.s.TrainingCourseId, c => c.Id, (x, c) => new { x.e.Id, course = c.Title, c.DurationHours, x.s.StartDate, x.s.EndDate, x.e.Status, x.e.CertificateNumber, x.e.CertifiedOn, x.e.ExpiresOn })
                .OrderByDescending(x => x.StartDate).ToListAsync(ct));
        });

        // ---- Compliance ----
        g.MapGet("/compliance", async (Guid legalEntityId, int? expiringWithinDays, string? format, LearningService svc, CancellationToken ct) =>
        {
            var rows = await svc.ComplianceAsync(legalEntityId, expiringWithinDays ?? 60, ct);
            if (TabularExport.Parse(format) is { } f)
            {
                var file = TabularExport.Build(f, "Training compliance", $"Expiring within {expiringWithinDays ?? 60} days flagged", "training-compliance", rows,
                    [new("Employee No", x => x.EmployeeNumber), new("Name", x => x.EmployeeName), new("Job Title", x => x.JobTitle), new("Department", x => x.Department), new("Course", x => x.Course), new("Mandatory", x => x.Mandatory ? "Yes" : "No"), new("Status", x => x.Status), new("Completed", x => x.CompletedOn), new("Expires", x => x.ExpiresOn)]);
                return Results.File(file.Content, file.ContentType, file.FileName);
            }
            return Results.Ok(new { rows, summary = rows.GroupBy(r => r.Status).ToDictionary(x => x.Key, x => x.Count()) });
        });

        // ---- NITA ----
        g.MapGet("/nita/claims", async (ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrLearning);
            return Results.Ok(await db.NitaClaims.Include(c => c.Lines).OrderByDescending(c => c.PeriodEnd).ToListAsync(ct));
        });
        g.MapPost("/nita/claims", async (NitaClaimCreate body, LearningService svc, CancellationToken ct) =>
        {
            var c = await svc.CreateNitaClaimAsync(body.LegalEntityId, body.From, body.To, ct);
            return Results.Created($"/api/payroll/learning/nita/claims/{c.Id}", c);
        });
        g.MapPost("/nita/claims/{id:guid}/status", async (Guid id, NitaClaimOutcome body, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrLearning);
            var c = await db.NitaClaims.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(NitaClaim), id);
            var ok = (c.Status, body.Status) switch
            {
                (ClaimStatus.Draft, ClaimStatus.Submitted) => true,
                (ClaimStatus.Submitted, ClaimStatus.Approved or ClaimStatus.Rejected) => true,
                (ClaimStatus.Approved, ClaimStatus.Paid) => true,
                _ => false,
            };
            if (!ok) throw new InvalidStateException($"A claim cannot move from {c.Status} to {body.Status}.");
            if (body.Status == ClaimStatus.Approved && (body.AmountApproved is null || body.AmountApproved < 0 || body.AmountApproved > c.TotalClaimed)) throw new DomainException("Enter the approved amount (up to the amount claimed).", "VALIDATION");
            c.Status = body.Status;
            if (body.AmountApproved is { } a) c.AmountApproved = a;
            if (body.NitaReference is { } r) c.NitaReference = r;
            if (body.Status == ClaimStatus.Submitted) c.SubmittedAt = DateTimeOffset.UtcNow;
            if (body.Status == ClaimStatus.Rejected) await db.NitaClaimLines.Where(l => l.NitaClaimId == id).ExecuteDeleteAsync(ct); // lines can be claimed again
            audit.Record("learning.nita.claim.status", nameof(NitaClaim), id, new { body.Status, body.AmountApproved, body.NitaReference }, c.LegalEntityId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(c);
        });
        g.MapGet("/nita/claims/{id:guid}/schedule", async (Guid id, string? format, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            ctx.Require(Permissions.HrLearning);
            var c = await db.NitaClaims.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(NitaClaim), id);
            var le = await db.LegalEntities.FirstAsync(x => x.Id == c.LegalEntityId, ct);
            var emps = await db.Employees.Where(e => c.Lines.Select(l => l.EmployeeId).Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
            var rows = c.Lines.OrderBy(l => l.CompletedOn).ToList();
            var file = TabularExport.Build(TabularExport.Parse(format) ?? ExportFormat.Xlsx, $"NITA reimbursement claim {c.Reference}", $"{le.Name} · NITA reg. {le.NitaRegistrationNumber ?? "—"} · {c.PeriodStart:dd MMM yyyy} – {c.PeriodEnd:dd MMM yyyy}", c.Reference, rows,
                [new("Employee No", l => emps.GetValueOrDefault(l.EmployeeId)?.EmployeeNumber), new("Trainee", l => emps.GetValueOrDefault(l.EmployeeId)?.FullName), new("ID No", l => FieldEncryptor.Mask(emps.GetValueOrDefault(l.EmployeeId)?.NationalId)),
                 new("Course", l => l.CourseTitle), new("NITA Code", l => l.NitaCourseCode), new("Completed", l => l.CompletedOn), new("Cost Claimed", l => l.Amount, true)],
                ["Total", null, null, null, null, null, c.TotalClaimed]);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
        g.MapGet("/nita/position", async (Guid legalEntityId, int year, LearningService svc, CancellationToken ct) => Results.Ok(await svc.NitaPositionAsync(legalEntityId, year, ct)));
    }
}
