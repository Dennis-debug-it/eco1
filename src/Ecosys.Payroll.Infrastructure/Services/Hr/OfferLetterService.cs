using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Ecosys.Payroll.Infrastructure.Services.Hr;

public sealed record OfferTemplateRequest(string Subject, string Body, string? SignatoryName, string? SignatoryTitle, int ProbationMonths, int AcceptWithinDays);
public sealed record OfferDraftEdit(string Subject, string Body, DateOnly AcceptBy, string? SignatoryName, string? SignatoryTitle);

/// <summary>
/// Offer letters: a per-company template with {placeholders}, drafts filled from the offer on the candidate's record,
/// sending (PDF kept, optional private link for the candidate to accept or decline online) and hand-over to the
/// employee's documents at hire. The letter states the salary, so drafting and sending need salary.write and reading
/// needs salary.read, as for the offer itself. Emails to the candidate never contain the salary.
/// </summary>
public sealed partial class OfferLetterService(PayrollDbContext db, TenantContext tenant, FileStore files, AuditService audit, NotificationService notify, IConfiguration config, LetterheadService letterheads)
{
    public static readonly (string Key, string Meaning)[] Placeholders =
    [
        ("candidate_name", "Candidate's full name"), ("candidate_first_name", "Candidate's first name"), ("candidate_email", "Candidate's email"),
        ("job_title", "Job title on the offer"), ("department", "Department on the requisition"), ("reports_to", "Position the role reports to"),
        ("work_location", "Branch, or the company address"), ("employment_type", "Permanent, fixed term, …"), ("start_date", "Start date on the offer"),
        ("basic_salary", "Monthly basic salary on the offer"), ("currency", "Pay currency, e.g. KES"), ("pay_day", "Monthly pay day, e.g. 28th"),
        ("probation_months", "Probation period (months)"), ("annual_leave_days", "Annual leave entitlement (days)"), ("accept_by", "Date to respond by"),
        ("company_name", "Company name"), ("company_address", "Company address"), ("signatory_name", "Person signing"), ("signatory_title", "Signatory's title"),
        ("today", "Date of the letter"), ("reference", "Letter reference"),
    ];

    public const string DefaultSubject = "Offer of employment: {job_title}";

    /// <summary>Kenyan starting point covering the written particulars in the Employment Act, 2007 (s.9–10). Employers adapt it, ideally with their lawyer.</summary>
    public const string DefaultBody = """
        Dear {candidate_first_name},

        We are pleased to offer you the position of {job_title} at {company_name}, on the terms set out below. A written contract of service with the full particulars required by sections 9 and 10 of the Employment Act, 2007 will be issued to you on or before your first day.

        ## Terms of the offer
        - Position: {job_title}
        - Department: {department}
        - Reporting to: {reports_to}
        - Place of work: {work_location}
        - Type of employment: {employment_type}
        - Start date: {start_date}
        - Basic salary: {currency} {basic_salary} per month, before statutory deductions (PAYE, NSSF, SHIF and the Affordable Housing Levy), paid by bank transfer on or before the {pay_day} of each month.
        - Probation: {probation_months} months. During probation either of us may end the employment by giving seven days' notice.
        - Hours of work: 45 hours a week, Monday to Friday, under the company's working-time policy.
        - Annual leave: {annual_leave_days} working days a year with full pay, in addition to public holidays.
        - Notice: after confirmation, one month's notice in writing by either party, or pay in lieu of notice.

        ## Conditions
        This offer depends on satisfactory references and on you giving us copies of your national ID, KRA PIN certificate, NSSF and SHA numbers, and your academic and professional certificates on or before your start date.

        You will be expected to follow the company's policies, including the code of conduct. We will use your personal data for employment purposes in line with the Data Protection Act, 2019.

        ## Accepting this offer
        Please confirm whether you accept this offer by {accept_by}. If we do not hear from you by then, the offer will lapse.

        We look forward to welcoming you to the team.
        """;

    /// <summary>The standard wording, for "start again from the standard text".</summary>
    public static string StandardBody => Dedent(DefaultBody);

    // ------------------------------------------------------------------ Template

    public async Task<OfferLetterTemplate> TemplateAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrRecruitment); tenant.RequireLegalEntity(legalEntityId);
        var t = await db.OfferLetterTemplates.FirstOrDefaultAsync(x => x.LegalEntityId == legalEntityId, ct);
        if (t is not null) return t;
        if (!await db.LegalEntities.AnyAsync(e => e.Id == legalEntityId, ct)) throw new NotFoundException("LegalEntity", legalEntityId);
        t = new OfferLetterTemplate { LegalEntityId = legalEntityId, Subject = DefaultSubject, Body = Dedent(DefaultBody) };
        db.OfferLetterTemplates.Add(t);
        await db.SaveChangesAsync(ct);
        return t;
    }

    public async Task<OfferLetterTemplate> SaveTemplateAsync(Guid legalEntityId, OfferTemplateRequest r, CancellationToken ct = default)
    {
        var t = await TemplateAsync(legalEntityId, ct);
        if (string.IsNullOrWhiteSpace(r.Subject) || string.IsNullOrWhiteSpace(r.Body)) throw new DomainException("The letter needs a subject and some text.", "VALIDATION");
        if (r.ProbationMonths is < 0 or > 12) throw new DomainException("Probation is 0 to 12 months (Employment Act s.42 allows six, extendable once by up to six).", "VALIDATION");
        if (r.AcceptWithinDays is < 1 or > 60) throw new DomainException("Give the candidate between 1 and 60 days to respond.", "VALIDATION");
        var unknown = PlaceholderRx().Matches(r.Subject + "\n" + r.Body).Select(m => m.Groups[1].Value).Distinct().Where(k => !Placeholders.Any(p => p.Key == k)).ToList();
        if (unknown.Count > 0) throw new DomainException($"Unknown fields: {string.Join(", ", unknown.Select(u => "{" + u + "}"))}. Use only the fields listed beside the template.", "VALIDATION");
        t.Subject = r.Subject.Trim(); t.Body = r.Body.Replace("\r", "").Trim(); t.SignatoryName = r.SignatoryName?.Trim(); t.SignatoryTitle = r.SignatoryTitle?.Trim();
        t.ProbationMonths = r.ProbationMonths; t.AcceptWithinDays = r.AcceptWithinDays;
        audit.Record("recruitment.offer_template.updated", nameof(OfferLetterTemplate), t.Id, new { t.ProbationMonths, t.AcceptWithinDays }, legalEntityId);
        await db.SaveChangesAsync(ct);
        return t;
    }

    // ------------------------------------------------------------------ Drafting

    public async Task<List<OfferLetter>> ForApplicationAsync(Guid applicationId, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrRecruitment); tenant.Require(Permissions.SalaryRead);
        var letters = await db.OfferLetters.Where(l => l.JobApplicationId == applicationId).OrderByDescending(l => l.Version).ToListAsync(ct);
        if (letters.Count > 0) tenant.RequireLegalEntity(letters[0].LegalEntityId);
        if (ExpireIfDue(letters)) await db.SaveChangesAsync(ct);
        return letters;
    }

    /// <summary>
    /// Opens the draft for the candidate's current offer. An existing draft takes the offer's latest salary, start date
    /// and title but keeps HR's edits, unless <paramref name="restart"/> resets the text to the template.
    /// </summary>
    public async Task<OfferLetter> DraftAsync(Guid applicationId, bool restart, CancellationToken ct = default)
    {
        RequireWrite();
        var app = await db.JobApplications.FirstOrDefaultAsync(a => a.Id == applicationId, ct) ?? throw new NotFoundException(nameof(JobApplication), applicationId);
        tenant.RequireLegalEntity(app.LegalEntityId);
        if (app.Stage is ApplicationStage.Hired or ApplicationStage.Rejected or ApplicationStage.Withdrawn) throw new InvalidStateException("This applicant's process has closed.");
        if (app.OfferBasic is not { } basic || app.OfferStartDate is not { } start) throw new InvalidStateException("Save the offer's salary and start date first.");
        var req = await db.JobRequisitions.FirstAsync(r => r.Id == app.JobRequisitionId, ct);
        var template = await TemplateAsync(app.LegalEntityId, ct);
        var latest = await db.OfferLetters.Where(l => l.JobApplicationId == app.Id).OrderByDescending(l => l.Version).FirstOrDefaultAsync(ct);
        if (latest is { Status: OfferLetterStatus.Sent or OfferLetterStatus.Accepted })
            throw new InvalidStateException(latest.Status == OfferLetterStatus.Accepted ? "The candidate has accepted this offer." : "An offer letter is out with the candidate. Withdraw it before drafting a new one.");

        var letter = latest is { Status: OfferLetterStatus.Draft } ? latest : null;
        if (letter is null)
        {
            var seq = await db.OfferLetters.CountAsync(l => db.JobApplications.Any(a => a.Id == l.JobApplicationId && a.JobRequisitionId == req.Id), ct) + 1;
            letter = new OfferLetter
            {
                LegalEntityId = app.LegalEntityId, JobApplicationId = app.Id, Version = (latest?.Version ?? 0) + 1, Reference = $"{req.Reference.Replace("REQ-", "OFR-")}-{seq:D2}",
                Subject = template.Subject, Body = template.Body, SignatoryName = template.SignatoryName, SignatoryTitle = template.SignatoryTitle,
                JobTitle = "", AcceptBy = BusinessClock.Today().AddDays(template.AcceptWithinDays),
            };
            db.OfferLetters.Add(letter);
        }
        else if (restart)
        {
            letter.Subject = template.Subject; letter.Body = template.Body; letter.SignatoryName = template.SignatoryName; letter.SignatoryTitle = template.SignatoryTitle;
        }
        letter.Basic = basic; letter.StartDate = start; letter.JobTitle = app.OfferJobTitle ?? req.Title;
        audit.Record("recruitment.offer_letter.drafted", nameof(OfferLetter), letter.Id, new { letter.Reference, letter.Version, restart }, app.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return letter;
    }

    public async Task<OfferLetter> UpdateDraftAsync(Guid id, OfferDraftEdit e, CancellationToken ct = default)
    {
        RequireWrite();
        var l = await LoadAsync(id, ct);
        if (l.Status != OfferLetterStatus.Draft) throw new InvalidStateException("Only a draft can be edited.");
        if (string.IsNullOrWhiteSpace(e.Subject) || string.IsNullOrWhiteSpace(e.Body)) throw new DomainException("The letter needs a subject and some text.", "VALIDATION");
        if (e.AcceptBy < BusinessClock.Today()) throw new DomainException("The respond-by date has passed.", "VALIDATION");
        l.Subject = e.Subject.Trim(); l.Body = e.Body.Replace("\r", "").Trim(); l.AcceptBy = e.AcceptBy; l.SignatoryName = e.SignatoryName?.Trim(); l.SignatoryTitle = e.SignatoryTitle?.Trim();
        await db.SaveChangesAsync(ct);
        return l;
    }

    /// <summary>The letter's text with every field filled. Sent letters are already frozen.</summary>
    public async Task<(string Subject, string Body, List<string> Unfilled)> PreviewAsync(OfferLetter l, CancellationToken ct = default)
    {
        if (l.Status != OfferLetterStatus.Draft) return (l.Subject, l.Body, []);
        var values = await ValuesAsync(l, ct);
        var unknown = new List<string>();
        string Fill(string text) => PlaceholderRx().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : AddUnknown(m.Value));
        string AddUnknown(string raw) { unknown.Add(raw); return raw; }
        return (Fill(l.Subject), Fill(l.Body), unknown.Distinct().ToList());
    }

    public async Task<byte[]> PdfAsync(OfferLetter l, CancellationToken ct = default)
    {
        var app = await db.JobApplications.FirstAsync(a => a.Id == l.JobApplicationId, ct);
        var entity = await db.LegalEntities.FirstAsync(e => e.Id == l.LegalEntityId, ct);
        var (subject, body, _) = await PreviewAsync(l, ct);
        var shown = new OfferLetter
        {
            LegalEntityId = l.LegalEntityId, Reference = l.Reference, Subject = subject, Body = body, JobTitle = l.JobTitle, SignatoryName = l.SignatoryName, SignatoryTitle = l.SignatoryTitle,
            Status = l.Status, RespondedAt = l.RespondedAt, ResponseName = l.ResponseName, ResponseIp = l.ResponseIp, RecordedByHr = l.RecordedByHr,
        };
        var issued = l.SentAt is { } s ? DateOnly.FromDateTime(BusinessClock.ToLocal(s).DateTime) : BusinessClock.Today();
        return HrDocumentRenderer.OfferLetter(entity, shown, $"{app.FirstName} {app.LastName}", app.Email, issued, draft: l.Status == OfferLetterStatus.Draft, await letterheads.BrandAsync(l.LegalEntityId, ct));
    }

    // ------------------------------------------------------------------ Sending and responses

    /// <summary>
    /// Freezes the text, keeps the PDF as sent and marks the offer sent. With <paramref name="emailLink"/> the candidate is
    /// emailed a private link to read and accept or decline. Returns the link (shown once) so HR can also share it directly.
    /// </summary>
    public async Task<(OfferLetter Letter, string? Link)> SendAsync(Guid id, bool emailLink, CancellationToken ct = default)
    {
        RequireWrite();
        var l = await LoadAsync(id, ct);
        if (l.Status != OfferLetterStatus.Draft) throw new InvalidStateException("This letter has already been sent.");
        var app = await db.JobApplications.FirstAsync(a => a.Id == l.JobApplicationId, ct);
        if (app.OfferBasic != l.Basic || app.OfferStartDate != l.StartDate || (app.OfferJobTitle is { } t && t != l.JobTitle))
            throw new InvalidStateException("The offer on the candidate's record has changed since this draft. Open the draft again to bring in the new terms.");
        if (l.AcceptBy < BusinessClock.Today()) throw new DomainException("The respond-by date has passed. Choose a later date.", "VALIDATION");
        var (subject, body, unfilled) = await PreviewAsync(l, ct);
        if (unfilled.Count > 0) throw new DomainException($"The letter still has fields that can't be filled: {string.Join(", ", unfilled)}.", "VALIDATION");

        l.Subject = subject; l.Body = body; l.Status = OfferLetterStatus.Sent; l.SentAt = DateTimeOffset.UtcNow; l.SentBy = tenant.UserName;
        var pdf = await PdfAsync(l, ct);
        l.SentFileId = (await files.SaveAsync($"{l.Reference}.pdf", pdf, nameof(OfferLetter), l.Id, null, ct)).Id;
        string? link = null;
        if (emailLink)
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            l.ResponseTokenHash = Hash(token);
            link = $"{(config["Payroll:PortalUrl"] ?? "https://payroll.ecosys.example").TrimEnd('/')}/offer/{token}";
            var company = await db.LegalEntities.Where(e => e.Id == l.LegalEntityId).Select(e => e.Name).FirstAsync(ct);
            notify.Enqueue(NotificationChannel.Email, app.Email, app.FirstName, "recruitment.offer.sent", $"Your offer from {company}",
                $"Dear {app.FirstName}, {company} has sent you an offer of employment for the position of {l.JobTitle}. Read the letter and accept or decline it here: {link} . Please respond by {l.AcceptBy:d MMMM yyyy}.",
                nameof(OfferLetter), l.Id);
        }
        app.OfferStatus = "Sent";
        audit.Record("recruitment.offer_letter.sent", nameof(OfferLetter), l.Id, new { l.Reference, l.AcceptBy, emailLink }, l.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return (l, link);
    }

    public async Task<OfferLetter> WithdrawAsync(Guid id, string reason, CancellationToken ct = default)
    {
        RequireWrite();
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Give a reason for withdrawing the offer.", "VALIDATION");
        var l = await LoadAsync(id, ct);
        if (l.Status is not (OfferLetterStatus.Draft or OfferLetterStatus.Sent)) throw new InvalidStateException($"A {l.Status.ToString().ToLowerInvariant()} offer cannot be withdrawn.");
        var wasSent = l.Status == OfferLetterStatus.Sent;
        l.Status = OfferLetterStatus.Withdrawn; l.DeclineReason = reason.Trim(); l.ResponseTokenHash = null;
        if (wasSent)
        {
            var app = await db.JobApplications.FirstAsync(a => a.Id == l.JobApplicationId, ct);
            app.OfferStatus = "Draft";
        }
        audit.Record("recruitment.offer_letter.withdrawn", nameof(OfferLetter), l.Id, new { l.Reference, reason }, l.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return l;
    }

    /// <summary>HR records a response received on paper or by email (from the offer's status on the candidate's record).</summary>
    public async Task RecordHrResponseAsync(JobApplication app, bool accepted, CancellationToken ct)
    {
        var l = await db.OfferLetters.Where(x => x.JobApplicationId == app.Id && x.Status == OfferLetterStatus.Sent).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        if (l is null) return;
        await CompleteResponseAsync(l, app, accepted, tenant.UserName ?? "HR", null, null, byHr: true, ct);
    }

    /// <summary>The candidate's view through their private link. Null if the link is unknown or the offer was withdrawn.</summary>
    public async Task<(OfferLetter Letter, JobApplication App, string Company)?> FindByTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        db.IgnoreTenantFilter = true;
        var hash = Hash(token);
        var l = await db.OfferLetters.FirstOrDefaultAsync(x => x.ResponseTokenHash == hash, ct);
        if (l is null) return null;
        tenant.TenantId = l.TenantId; tenant.UserName = "candidate";
        if (ExpireIfDue([l])) await db.SaveChangesAsync(ct);
        var app = await db.JobApplications.FirstAsync(a => a.Id == l.JobApplicationId, ct);
        var company = await db.LegalEntities.Where(e => e.Id == l.LegalEntityId).Select(e => e.Name).FirstAsync(ct);
        return (l, app, company);
    }

    public async Task<OfferLetter> RespondAsync(string token, bool accept, string? name, string? reason, string? ip, CancellationToken ct = default)
    {
        var found = await FindByTokenAsync(token, ct) ?? throw new DomainException("This link is not valid. Ask the employer for a new one.", "NOT_FOUND");
        var (l, app, _) = found;
        if (l.Status == OfferLetterStatus.Expired) throw new InvalidStateException($"This offer lapsed on {l.AcceptBy:d MMMM yyyy}. Contact the employer if you are still interested.");
        if (l.Status != OfferLetterStatus.Sent) throw new InvalidStateException($"You have already responded to this offer ({l.Status.ToString().ToLowerInvariant()}).");
        if (accept && (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 3)) throw new DomainException("Type your full name to accept the offer.", "VALIDATION");
        tenant.UserName = $"{app.FirstName} {app.LastName} (candidate)";
        await CompleteResponseAsync(l, app, accept, accept ? name!.Trim() : $"{app.FirstName} {app.LastName}", reason, ip, byHr: false, ct);
        return l;
    }

    private async Task CompleteResponseAsync(OfferLetter l, JobApplication app, bool accepted, string name, string? reason, string? ip, bool byHr, CancellationToken ct)
    {
        l.Status = accepted ? OfferLetterStatus.Accepted : OfferLetterStatus.Declined;
        l.RespondedAt = DateTimeOffset.UtcNow; l.ResponseName = name; l.ResponseIp = ip; l.RecordedByHr = byHr;
        l.DeclineReason = accepted ? null : string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        app.OfferStatus = accepted ? "Accepted" : "Declined";
        if (accepted) l.AcceptedFileId = (await files.SaveAsync($"{l.Reference}-accepted.pdf", await PdfAsync(l, ct), nameof(OfferLetter), l.Id, null, ct)).Id;
        audit.Record(accepted ? "recruitment.offer_letter.accepted" : "recruitment.offer_letter.declined", nameof(OfferLetter), l.Id, new { l.Reference, byHr, reason = l.DeclineReason }, l.LegalEntityId);
        if (!byHr) await NotifyHrAsync(l, app, accepted, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>At hire, the accepted letter goes onto the new employee's documents (as a contract record they can see).</summary>
    public async Task<EmployeeDocument?> FileWithEmployeeAsync(Guid applicationId, Guid employeeId, CancellationToken ct)
    {
        var l = await db.OfferLetters.Where(x => x.JobApplicationId == applicationId && x.Status == OfferLetterStatus.Accepted).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        var fileId = l?.AcceptedFileId ?? l?.SentFileId;
        if (l is null || fileId is null) return null;
        var source = await db.StoredFiles.FirstAsync(f => f.Id == fileId, ct);
        var doc = new EmployeeDocument
        {
            LegalEntityId = l.LegalEntityId, EmployeeId = employeeId, Category = DocumentCategory.Contract, Title = "Offer letter (accepted)",
            IssuedOn = l.SentAt is { } s ? DateOnly.FromDateTime(BusinessClock.ToLocal(s).DateTime) : null, VisibleToEmployee = true,
        };
        doc.StoredFileId = (await files.SaveAsync(source.FileName, source.Content, nameof(EmployeeDocument), doc.Id, employeeId, ct)).Id;
        db.EmployeeDocuments.Add(doc);
        return doc;
    }

    // ------------------------------------------------------------------ Helpers

    private void RequireWrite() { tenant.Require(Permissions.HrRecruitment); tenant.Require(Permissions.SalaryWrite); }

    private async Task<OfferLetter> LoadAsync(Guid id, CancellationToken ct)
    {
        var l = await db.OfferLetters.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(OfferLetter), id);
        tenant.RequireLegalEntity(l.LegalEntityId);
        ExpireIfDue([l]);
        return l;
    }

    /// <summary>A sent offer lapses at the end of its respond-by date.</summary>
    private static bool ExpireIfDue(IEnumerable<OfferLetter> letters)
    {
        var changed = false;
        foreach (var l in letters.Where(l => l.Status == OfferLetterStatus.Sent && l.AcceptBy < BusinessClock.Today()))
        {
            l.Status = OfferLetterStatus.Expired; changed = true;
        }
        return changed;
    }

    private async Task<Dictionary<string, string>> ValuesAsync(OfferLetter l, CancellationToken ct)
    {
        const string tbc = "to be confirmed";
        var app = await db.JobApplications.FirstAsync(a => a.Id == l.JobApplicationId, ct);
        var req = await db.JobRequisitions.FirstAsync(r => r.Id == app.JobRequisitionId, ct);
        var entity = await db.LegalEntities.FirstAsync(e => e.Id == l.LegalEntityId, ct);
        var template = await db.OfferLetterTemplates.FirstOrDefaultAsync(t => t.LegalEntityId == l.LegalEntityId, ct);
        var dept = req.DepartmentId is { } d ? await db.Departments.Where(x => x.Id == d).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var branch = req.BranchId is { } b ? await db.Branches.Where(x => x.Id == b).Select(x => new { x.Name, x.Location }).FirstOrDefaultAsync(ct) : null;
        string? reportsTo = null;
        if (req.PositionId is { } pid && await db.Positions.Where(p => p.Id == pid).Select(p => p.ReportsToPositionId).FirstOrDefaultAsync(ct) is { } parent)
            reportsTo = await db.Positions.Where(p => p.Id == parent).Select(p => p.Title).FirstOrDefaultAsync(ct);
        var payDay = await db.PayrollGroups.Where(g => g.LegalEntityId == l.LegalEntityId && g.IsActive).OrderBy(g => g.Code).Select(g => (int?)g.PayDay).FirstOrDefaultAsync(ct);
        var leaveDays = await db.LeaveTypes.Where(t => t.LegalEntityId == l.LegalEntityId && t.Code == "ANNUAL").Select(t => (decimal?)t.AnnualEntitlementDays).FirstOrDefaultAsync(ct);
        var today = BusinessClock.Today();
        return new()
        {
            ["candidate_name"] = $"{app.FirstName} {app.LastName}", ["candidate_first_name"] = app.FirstName, ["candidate_email"] = app.Email,
            ["job_title"] = l.JobTitle, ["department"] = dept ?? tbc, ["reports_to"] = reportsTo ?? tbc,
            ["work_location"] = branch is null ? entity.Address ?? tbc : string.IsNullOrWhiteSpace(branch.Location) ? branch.Name : $"{branch.Name}, {branch.Location}",
            ["employment_type"] = SplitWords(req.EmploymentType), ["start_date"] = l.StartDate.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
            ["basic_salary"] = l.Basic.ToString("N2", CultureInfo.InvariantCulture), ["currency"] = entity.CurrencyCode, ["pay_day"] = payDay is { } pd ? Ordinal(pd) : "last working day",
            ["probation_months"] = (template?.ProbationMonths ?? 3).ToString(CultureInfo.InvariantCulture), ["annual_leave_days"] = (leaveDays ?? 21m).ToString("0.##", CultureInfo.InvariantCulture),
            ["accept_by"] = l.AcceptBy.ToString("d MMMM yyyy", CultureInfo.InvariantCulture), ["company_name"] = entity.Name, ["company_address"] = entity.Address ?? "",
            ["signatory_name"] = l.SignatoryName ?? "Authorised signatory", ["signatory_title"] = l.SignatoryTitle ?? "", ["today"] = today.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
            ["reference"] = l.Reference,
        };
    }

    private async Task NotifyHrAsync(OfferLetter l, JobApplication app, bool accepted, CancellationToken ct)
    {
        var users = (await db.Users.Where(u => u.IsActive && u.TenantId == l.TenantId).ToListAsync(ct))
            .Where(u => Permissions.ForRoles(u.Roles).Contains(Permissions.HrRecruitment) && (u.LegalEntityIds.Count == 0 || u.LegalEntityIds.Contains(l.LegalEntityId)));
        foreach (var u in users)
            notify.Enqueue(NotificationChannel.Email, u.Email, u.DisplayName, $"recruitment.offer.{(accepted ? "accepted" : "declined")}",
                $"{app.FirstName} {app.LastName} {(accepted ? "accepted" : "declined")} the offer",
                accepted ? $"{app.FirstName} {app.LastName} accepted the offer for {l.JobTitle} ({l.Reference}), starting {l.StartDate:d MMMM yyyy}. Hire them from Recruitment to create the employee record."
                         : $"{app.FirstName} {app.LastName} declined the offer for {l.JobTitle} ({l.Reference}).{(l.DeclineReason is null ? "" : $" Reason given: {l.DeclineReason}")}",
                nameof(OfferLetter), l.Id, l.TenantId);
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
    private static string SplitWords(string s) => SplitRx().Replace(s, " $1").Trim() is var w && w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : s;
    private static string Dedent(string s) => string.Join('\n', s.Replace("\r", "").Split('\n').Select(l => l.Trim()));

    [GeneratedRegex(@"\{([a-z_]+)\}")] private static partial Regex PlaceholderRx();
    [GeneratedRegex("(?<!^)([A-Z])")] private static partial Regex SplitRx();
}
