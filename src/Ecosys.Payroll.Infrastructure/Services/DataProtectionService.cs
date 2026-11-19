using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Ecosys.Payroll.Infrastructure.Services;

public sealed record RetentionDue(Guid EmployeeId, string EmployeeNumber, string Name, DateOnly TerminationDate, DateOnly DueSince);
public sealed record ApplicantDue(Guid ApplicationId, string Name, string Requisition, DateTimeOffset AppliedAt, bool Consent);

/// <summary>
/// Kenya Data Protection Act 2019 duties that fall on the payroll/HR system:
/// <list type="bullet">
/// <item>s.26 right of access: a complete, machine-readable copy of what is held about an employee.</item>
/// <item>s.39 retention: keep personal data no longer than needed. Employment Act s.74(2) requires employment records
/// for five years after employment ends, and Tax Procedures Act s.23 five years for tax records, so the default here
/// is seven years and the setting can't go below five.</item>
/// <item>Unsuccessful applicants' data is kept for a limited time unless they agreed to be considered again.</item>
/// </list>
/// Anonymisation keeps payroll amounts (the accounts and statutory returns must still add up) but removes everything
/// that identifies the person.
/// </summary>
public sealed class DataProtectionService(PayrollDbContext db, ITenantContext tenant, AuditService audit, IConfiguration config)
{
    public int EmployeeRetentionYears => Math.Max(5, config.GetValue("Payroll:Retention:EmployeeRecordYears", 7));
    public int ApplicantMonths => Math.Max(1, config.GetValue("Payroll:Retention:ApplicantMonths", 12));
    public int ApplicantConsentMonths => Math.Max(ApplicantMonths, config.GetValue("Payroll:Retention:ApplicantConsentMonths", 24));

    // ---------------------------------------------------------------- s.26 access
    public async Task<Dictionary<string, object?>> ExportAsync(Guid employeeId, CancellationToken ct)
    {
        if (tenant.EmployeeId != employeeId) tenant.Require(Permissions.OrganisationManage);
        var e = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.Id == employeeId, ct) ?? throw new NotFoundException(nameof(Employee), employeeId);
        if (tenant.EmployeeId != employeeId) tenant.RequireLegalEntity(e.LegalEntityId);
        var id = e.Id;
        var user = await db.Users.AsNoTracking().Where(u => u.EmployeeId == id).Select(u => new { u.Email, u.DisplayName, u.Roles, u.IsActive, u.LastLoginAt, u.MfaEnabled, u.Phone, u.CreatedAt }).ToListAsync(ct);
        var contacts = new[] { e.Email, e.Phone }.Where(x => !string.IsNullOrEmpty(x)).ToList();

        var export = new Dictionary<string, object?>
        {
            ["about"] = new
            {
                generatedAt = BusinessClock.Now(),
                law = "Kenya Data Protection Act 2019, section 26 (right of access)",
                note = "Everything Ecosys Payroll holds about you, as structured data. Payroll amounts are kept for statutory periods; ask HR if anything is wrong (section 40, right to rectification).",
            },
            ["personal"] = e,
            ["employmentHistory"] = await db.EmploymentRecords.AsNoTracking().Where(x => x.EmployeeId == id).OrderBy(x => x.EffectiveFrom).ToListAsync(ct),
            ["statutory"] = await db.EmployeeStatutoryDetails.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["bankAccounts"] = await db.EmployeeBankAccounts.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["salary"] = await db.EmployeeSalaryAssignments.AsNoTracking().Where(x => x.EmployeeId == id).Include(x => x.Lines).OrderBy(x => x.EffectiveFrom).ToListAsync(ct),
            ["payrollResults"] = await db.PayrollRunEmployees.AsNoTracking().Where(x => x.EmployeeId == id)
                .Join(db.PayrollRuns, r => r.PayrollRunId, run => run.Id, (r, run) => new { run.Reference, run.Status, r.GrossPay, r.TaxablePay, r.Paye, r.Nssf, r.Shif, r.HousingLevy, r.TotalEmployeeDeductions, r.NetPay })
                .ToListAsync(ct),
            ["payslips"] = await db.Payslips.AsNoTracking().Where(x => x.EmployeeId == id).OrderBy(x => x.PeriodStart).Select(x => new { x.PayslipNumber, x.PeriodStart, x.PeriodEnd, x.GrossPay, x.NetPay, x.IssuedAt }).ToListAsync(ct),
            ["loans"] = await db.StaffLoans.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["attendance"] = await db.AttendanceRecords.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["leaveBalances"] = await db.EmployeeLeaveBalances.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["leaveRequests"] = await db.LeaveRequests.AsNoTracking().Where(x => x.EmployeeId == id).OrderBy(x => x.StartDate).ToListAsync(ct),
            ["documents"] = await db.EmployeeDocuments.AsNoTracking().Where(x => x.EmployeeId == id).Select(x => new { x.Category, x.Title, x.IssuedOn, x.ExpiresOn, x.Version, x.CreatedAt }).ToListAsync(ct),
            ["dependants"] = await db.Dependants.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["benefits"] = await db.BenefitEnrolments.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["performanceReviews"] = await db.PerformanceReviews.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["goals"] = await db.Goals.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["improvementPlans"] = await db.ImprovementPlans.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["training"] = await db.TrainingEnrolments.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["trainingBonds"] = await db.TrainingBonds.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["disciplinaryAndGrievance"] = await db.HrCases.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["expenseClaims"] = await db.ExpenseClaims.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["assets"] = await db.AssetAssignments.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["separation"] = await db.Separations.AsNoTracking().Where(x => x.EmployeeId == id).ToListAsync(ct),
            ["userAccount"] = user,
            ["messagesSent"] = await db.NotificationMessages.AsNoTracking().Where(m => contacts.Contains(m.Recipient)).OrderBy(m => m.CreatedAt)
                .Select(m => new { m.Channel, m.Subject, m.Status, m.CreatedAt, m.SentAt }).ToListAsync(ct),
            ["recordOfChanges"] = await db.PayrollAuditLogs.AsNoTracking().Where(a => a.EntityId == id).OrderBy(a => a.CreatedAt)
                .Select(a => new { a.CreatedAt, a.Action, a.ActorName }).ToListAsync(ct),
        };
        audit.Record("dpa.subject_access.exported", nameof(Employee), id, new { e.EmployeeNumber, bySelf = tenant.EmployeeId == id }, e.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return export;
    }

    // ---------------------------------------------------------------- s.39 retention
    public async Task<(List<RetentionDue> Employees, List<ApplicantDue> Applicants)> DueAsync(CancellationToken ct)
    {
        tenant.Require(Permissions.OrganisationManage);
        var today = BusinessClock.Today();
        var cutoff = today.AddYears(-EmployeeRetentionYears);
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.AnonymisedAt == null && e.TerminationDate != null && e.TerminationDate <= cutoff)
            .OrderBy(e => e.TerminationDate)
            .Select(e => new { e.Id, e.EmployeeNumber, e.FirstName, e.LastName, e.TerminationDate, e.LegalEntityId }).ToListAsync(ct);
        var visible = employees.Where(e => tenant.LegalEntityIds.Count == 0 || tenant.LegalEntityIds.Contains(e.LegalEntityId))
            .Select(e => new RetentionDue(e.Id, e.EmployeeNumber, $"{e.FirstName} {e.LastName}", e.TerminationDate!.Value, e.TerminationDate.Value.AddYears(EmployeeRetentionYears))).ToList();
        return (visible, await ApplicantsDueAsync(ct));
    }

    private async Task<List<ApplicantDue>> ApplicantsDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var plain = now.AddMonths(-ApplicantMonths);
        var consented = now.AddMonths(-ApplicantConsentMonths);
        return await db.JobApplications.AsNoTracking()
            .Where(a => a.HiredEmployeeId == null && a.Stage != ApplicationStage.Hired && (a.ConsentToRetain ? a.CreatedAt <= consented : a.CreatedAt <= plain))
            .Join(db.JobRequisitions, a => a.JobRequisitionId, r => r.Id, (a, r) => new ApplicantDue(a.Id, a.FirstName + " " + a.LastName, r.Title, a.CreatedAt, a.ConsentToRetain))
            .ToListAsync(ct);
    }

    public async Task AnonymiseEmployeeAsync(Guid employeeId, string reason, CancellationToken ct)
    {
        tenant.Require(Permissions.OrganisationManage);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Record why the data is being anonymised.", "VALIDATION");
        var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == employeeId, ct) ?? throw new NotFoundException(nameof(Employee), employeeId);
        tenant.RequireLegalEntity(e.LegalEntityId);
        if (e.AnonymisedAt is not null) throw new InvalidStateException("This employee's data has already been anonymised.");
        if (e.TerminationDate is not { } left) throw new InvalidStateException("Only former employees can be anonymised.");
        var due = left.AddYears(EmployeeRetentionYears);
        if (due > BusinessClock.Today())
            throw new InvalidStateException($"Records must be kept until {due:dd MMM yyyy} ({EmployeeRetentionYears} years after employment ended; Employment Act s.74 and Tax Procedures Act s.23 require at least five).");
        if (await db.PayrollRunEmployees.AnyAsync(r => r.EmployeeId == e.Id && db.PayrollRuns.Any(run => run.Id == r.PayrollRunId && (run.Status == Domain.Payroll.PayrollRunStatus.Draft || run.Status == Domain.Payroll.PayrollRunStatus.Prepared || run.Status == Domain.Payroll.PayrollRunStatus.Calculated || run.Status == Domain.Payroll.PayrollRunStatus.UnderReview)), ct))
            throw new InvalidStateException("The employee is in a payroll run that isn't approved yet.");

        var number = e.EmployeeNumber;
        var label = $"Former employee {number}";
        e.FirstName = "Former employee"; e.MiddleName = null; e.LastName = number;
        e.NationalId = null; e.PassportNumber = null; e.DateOfBirth = null; e.Gender = Gender.Unspecified;
        e.Email = null; e.Phone = null; e.PhysicalAddress = null; e.EmergencyContactName = null; e.EmergencyContactPhone = null;
        e.AnonymisedAt = DateTimeOffset.UtcNow;

        foreach (var s in await db.EmployeeStatutoryDetails.Where(x => x.EmployeeId == e.Id).ToListAsync(ct))
        { s.KraPin = null; s.NssfNumber = null; s.ShifNumber = null; s.NhifNumber = null; s.DisabilityExemptionCertificate = null; }
        foreach (var b in await db.EmployeeBankAccounts.Where(x => x.EmployeeId == e.Id).ToListAsync(ct))
        { b.AccountName = label; b.AccountNumber = "REDACTED"; b.IsVerified = false; }
        foreach (var p in await db.PayrollPayments.Where(x => x.EmployeeId == e.Id).ToListAsync(ct))
        { p.AccountName = label; p.AccountNumber = p.AccountNumber is null ? null : FieldEncryptor.Mask(p.AccountNumber); }
        await db.PayrollRunEmployees.Where(x => x.EmployeeId == e.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.EmployeeName, label), ct);

        db.Dependants.RemoveRange(await db.Dependants.Where(x => x.EmployeeId == e.Id).ToListAsync(ct));
        db.EmployeeDocuments.RemoveRange(await db.EmployeeDocuments.Where(x => x.EmployeeId == e.Id).ToListAsync(ct));
        db.StoredFiles.RemoveRange(await db.StoredFiles.Where(x => x.EmployeeId == e.Id).ToListAsync(ct));
        foreach (var l in await db.LeaveRequests.Where(x => x.EmployeeId == e.Id).ToListAsync(ct)) { l.Reason = null; l.AttachmentFileId = null; }
        foreach (var r in await db.PerformanceReviews.Where(x => x.EmployeeId == e.Id).ToListAsync(ct))
        { r.SelfComments = null; r.ManagerComments = null; r.CalibrationNote = null; r.EmployeeResponse = null; foreach (var g in r.GoalRatings) g.Comment = null; }
        foreach (var c in await db.HrCases.Where(x => x.EmployeeId == e.Id).ToListAsync(ct))
        { c.Summary = "[removed after retention period]"; c.OutcomeReason = null; c.Companion = null; c.Events = []; }
        foreach (var x in await db.ExpenseClaims.Where(x => x.EmployeeId == e.Id).ToListAsync(ct))
            foreach (var line in x.Lines) { line.Description = "[removed]"; line.ReceiptFileId = null; }
        foreach (var s in await db.Separations.Where(x => x.EmployeeId == e.Id).ToListAsync(ct)) { s.Reason = "[removed]"; s.ExitInterviewNotes = null; }
        foreach (var a in await db.JobApplications.Where(x => x.HiredEmployeeId == e.Id).ToListAsync(ct))
        { a.FirstName = "Former"; a.LastName = "employee"; a.Email = $"removed-{a.Id:N}@invalid"; a.Phone = null; a.CvFileId = null; a.Interviews = []; }
        foreach (var m in await db.NotificationMessages.Where(m => m.EntityId == e.Id).ToListAsync(ct)) { m.Recipient = "removed"; m.RecipientName = null; m.Body = "[removed]"; }
        foreach (var u in await db.Users.Where(u => u.EmployeeId == e.Id).ToListAsync(ct))
        {
            u.Email = $"removed-{u.Id:N}@invalid"; u.DisplayName = label; u.Phone = null; u.PasswordHash = null; u.MfaSecret = null; u.MfaEnabled = false;
            u.IsActive = false; u.ExternalSubject = null; u.RotateSecurityStamp();
        }

        audit.Record("dpa.employee.anonymised", nameof(Employee), e.Id, new { employeeNumber = number, reason, retentionYears = EmployeeRetentionYears }, e.LegalEntityId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> PurgeApplicantsAsync(CancellationToken ct)
    {
        tenant.Require(Permissions.OrganisationManage);
        var due = (await ApplicantsDueAsync(ct)).Select(a => a.ApplicationId).ToList();
        if (due.Count == 0) return 0;
        var apps = await db.JobApplications.Where(a => due.Contains(a.Id)).ToListAsync(ct);
        var files = apps.Where(a => a.CvFileId != null).Select(a => a.CvFileId!.Value).ToList();
        db.StoredFiles.RemoveRange(await db.StoredFiles.Where(f => files.Contains(f.Id) || (f.OwnerType == nameof(JobApplication) && f.OwnerId != null && due.Contains(f.OwnerId.Value))).ToListAsync(ct));
        db.JobApplications.RemoveRange(apps);
        audit.Record("dpa.applicants.purged", nameof(JobApplication), null, new { count = apps.Count, months = ApplicantMonths, consentMonths = ApplicantConsentMonths });
        await db.SaveChangesAsync(ct);
        return apps.Count;
    }
}
