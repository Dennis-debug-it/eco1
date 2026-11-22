using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Ecosys.Payroll.Infrastructure.Services.Hr;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

/// <summary>A job as the public sees it. Deliberately has no salary, justification or internal reference.</summary>
public sealed record PublicJob(Guid Id, string Title, string? Department, string? Location, string EmploymentType, string? Description, DateOnly? ClosingDate, int Openings, DateTimeOffset? PostedAt);
public sealed record PublicCareers(string Company, IReadOnlyList<PublicJob> Jobs);
/// <summary>A job plus what the privacy notice must tell the candidate: how long unsuccessful applications are kept.</summary>
public sealed record PublicJobPage(string Company, PublicJob Job, int RetentionMonths, int ConsentRetentionMonths);

/// <summary>
/// The public careers page: anyone can list a company's posted jobs and apply. No sign-in, so every request is
/// scoped to one tenant by its slug, answers the same "not found" for an unknown company and for one with nothing
/// posted, and applications are rate-limited, size-limited and checked before anything is stored.
/// </summary>
public static class CareersEndpoints
{
    public const string ApplyRateLimit = "careers-apply";
    public const long MaxRequestBytes = FileStore.MaxBytes + 512 * 1024;
    public const string Source = "Careers page";

    public static void MapCareers(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/public/careers/{slug}").AllowAnonymous();

        g.MapGet("", async (string slug, PayrollDbContext db, TenantContext ctx, CancellationToken ct) =>
        {
            if (await ResolveAsync(slug, db, ctx, ct) is not { } tenant) return NotFound();
            var jobs = await PublicJobsAsync(db, null, ct);
            return jobs.Count == 0 ? NotFound() : Results.Ok(new PublicCareers(tenant.Name, jobs));
        });

        g.MapGet("/jobs/{id:guid}", async (string slug, Guid id, PayrollDbContext db, TenantContext ctx, DataProtectionService retention, CancellationToken ct) =>
        {
            if (await ResolveAsync(slug, db, ctx, ct) is not { } tenant) return NotFound();
            var job = (await PublicJobsAsync(db, id, ct)).FirstOrDefault();
            return job is null ? NotFound() : Results.Ok(new PublicJobPage(tenant.Name, job, retention.ApplicantMonths, retention.ApplicantConsentMonths));
        });

        g.MapPost("/jobs/{id:guid}/apply", async (string slug, Guid id, HttpRequest http, PayrollDbContext db, TenantContext ctx, FileStore files, AuditService audit, NotificationService notify, CancellationToken ct) =>
        {
            if (http.ContentLength is > MaxRequestBytes) throw new DomainException($"Your CV must be {FileStore.MaxBytes / 1024 / 1024} MB or smaller.", "VALIDATION");
            if (await ResolveAsync(slug, db, ctx, ct) is not { } tenant || (await PublicJobsAsync(db, id, ct)).Count == 0) return NotFound();
            var req = await db.JobRequisitions.FirstAsync(r => r.Id == id, ct);

            var form = await http.ReadFormAsync(ct);
            string F(string k, int max) { var v = form[k].ToString().Trim(); return v.Length > max ? v[..max] : v; }
            var received = Results.Json(new { message = "Thank you. Your application has been received." }, statusCode: StatusCodes.Status202Accepted);

            // Bots fill every field; people never see this one. Answer as if it worked, store nothing.
            if (form["website"].ToString().Length > 0) return received;

            var problems = new List<string>();
            var first = F("firstName", 100); var last = F("lastName", 100);
            var email = F("email", 200).ToLowerInvariant();
            var phoneRaw = F("phone", 30);
            var phone = phoneRaw.Length == 0 ? null : KenyaIdentifiers.NormalisePhone(phoneRaw);
            var note = F("coverNote", 3000);
            if (first.Length == 0 || last.Length == 0) problems.Add("Enter your first and last name.");
            if (!IsEmail(email)) problems.Add("Enter a valid email address.");
            if (phoneRaw.Length > 0 && phone is null) problems.Add("Enter a valid phone number, e.g. 07XX XXX XXX.");
            if (form["privacyConsent"] != "true") problems.Add("Please agree to how we use your information so we can consider your application.");
            var cv = form.Files.GetFile("cv");
            byte[]? content = null;
            if (cv is null || cv.Length == 0) problems.Add("Attach your CV as a PDF or Word (.docx) file.");
            else if (cv.Length > FileStore.MaxBytes) problems.Add($"Your CV must be {FileStore.MaxBytes / 1024 / 1024} MB or smaller.");
            else
            {
                content = await ReadAsync(cv, ct);
                if (!LooksLikeCv(cv.FileName, content)) problems.Add("Your CV must be a PDF or Word (.docx) file.");
            }
            if (problems.Count > 0) throw new DomainException(string.Join(" ", problems), "VALIDATION");

            // A second application for the same job is acknowledged but not stored or re-emailed, without telling
            // the requester whether that email address had applied. Emails are encrypted with a random nonce, so
            // they are compared after decryption rather than in SQL.
            var earlier = await db.JobApplications.AsNoTracking().Where(a => a.JobRequisitionId == id && a.Stage != ApplicationStage.Withdrawn).Select(a => a.Email).ToListAsync(ct);
            if (earlier.Contains(email, StringComparer.OrdinalIgnoreCase)) return received;

            var app = new JobApplication
            {
                TenantId = tenant.Id, LegalEntityId = req.LegalEntityId, JobRequisitionId = id, FirstName = first, LastName = last, Email = email, Phone = phone,
                Source = Source, CoverNote = note.Length == 0 ? null : note, ConsentToRetain = form["retainConsent"] == "true",
            };
            app.CvFileId = (await files.SaveAsync(Path.GetFileName(cv!.FileName), content!, nameof(JobApplication), app.Id, null, ct)).Id;
            db.JobApplications.Add(app);
            audit.Record("recruitment.application.received", nameof(JobApplication), app.Id, new { req.Reference, source = Source }, req.LegalEntityId);
            notify.ApplicationReceived(app, req.Title, tenant.Name);
            await db.SaveChangesAsync(ct);
            return received;
        }).DisableAntiforgery().RequireRateLimiting(ApplyRateLimit).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
    }

    /// <summary>The same answer for an unknown company and one with nothing posted, so company slugs can't be enumerated.</summary>
    private static IResult NotFound() => Results.Problem("This company has no open positions on its careers page right now.", statusCode: 404, title: "Not found");

    /// <summary>Scopes the request to the tenant, so query filters and stamping work as for a signed-in user.</summary>
    private static async Task<Tenant?> ResolveAsync(string slug, PayrollDbContext db, TenantContext ctx, CancellationToken ct)
    {
        db.IgnoreTenantFilter = true;
        Tenant? tenant;
        try { tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug.ToLowerInvariant() && t.IsActive, ct); }
        finally { db.IgnoreTenantFilter = false; }
        if (tenant is null) return null;
        ctx.TenantId = tenant.Id;
        ctx.UserName = Source;
        return tenant;
    }

    private static async Task<List<PublicJob>> PublicJobsAsync(PayrollDbContext db, Guid? id, CancellationToken ct)
    {
        var today = BusinessClock.Today();
        var q = db.JobRequisitions.AsNoTracking().Where(r => r.IsPublished && r.Status == RequisitionStatus.Open && (r.ClosingDate == null || r.ClosingDate >= today));
        if (id is { } one) q = q.Where(r => r.Id == one);
        var rows = await q.OrderByDescending(r => r.PublishedAt).ToListAsync(ct);
        var deptIds = rows.Where(r => r.DepartmentId != null).Select(r => r.DepartmentId!.Value).ToList();
        var branchIds = rows.Where(r => r.BranchId != null).Select(r => r.BranchId!.Value).ToList();
        var depts = await db.Departments.Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var branches = await db.Branches.Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Location ?? b.Name, ct);
        return rows.Select(r => new PublicJob(r.Id, r.Title, r.DepartmentId is { } d ? depts.GetValueOrDefault(d) : null, r.BranchId is { } b ? branches.GetValueOrDefault(b) : null,
            r.EmploymentType, r.Description, r.ClosingDate, r.Openings, r.PublishedAt)).ToList();
    }

    private static bool IsEmail(string e)
    {
        var at = e.IndexOf('@');
        return at > 0 && at == e.LastIndexOf('@') && e.IndexOf('.', at) > at + 1 && !e.EndsWith('.') && !e.Any(char.IsWhiteSpace);
    }

    /// <summary>PDF, or a .docx (a zip package). FileStore checks the PDF signature and size again.</summary>
    private static bool LooksLikeCv(string fileName, byte[] content)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => content.Length > 4 && content[0] == '%' && content[1] == 'P' && content[2] == 'D' && content[3] == 'F',
            ".docx" => content.Length > 4 && content[0] == 'P' && content[1] == 'K' && content[2] == 3 && content[3] == 4,
            _ => false,
        };
    }

    private static async Task<byte[]> ReadAsync(IFormFile f, CancellationToken ct) { using var ms = new MemoryStream(); await f.CopyToAsync(ms, ct); return ms.ToArray(); }
}
