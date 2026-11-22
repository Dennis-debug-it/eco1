using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>The public careers page: what anyone can see, and applying without an account.</summary>
[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class CareersTests(PayrollApiFixture app)
{
    private const string B = "/api/payroll";
    private const string Careers = $"{B}/public/careers/demo";
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF");

    private static MultipartFormDataContent Form(string email, bool privacy = true, string fileName = "cv.pdf", byte[]? file = null, string? website = null, bool retain = false, string? note = null)
    {
        var f = new MultipartFormDataContent
        {
            { new StringContent("Wanjiku"), "firstName" }, { new StringContent("Mwangi"), "lastName" }, { new StringContent(email), "email" },
            { new StringContent("0722 123 456"), "phone" }, { new StringContent(privacy ? "true" : "false"), "privacyConsent" },
            { new StringContent(retain ? "true" : "false"), "retainConsent" },
        };
        if (note is not null) f.Add(new StringContent(note), "coverNote");
        if (website is not null) f.Add(new StringContent(website), "website");
        if (fileName.Length > 0) f.Add(new ByteArrayContent(file ?? Pdf), "cv", fileName);
        return f;
    }

    /// <summary>Applicant emails are encrypted with a random nonce, so they're compared after loading.</summary>
    private Task<List<string>> EmailsAsync() => app.WithDbAsync(null, db => db.JobApplications.Select(a => a.Email).ToListAsync());

    /// <summary>An approved job posted on the careers page, with a salary range that must never be shown.</summary>
    private async Task<Guid> PostedJobAsync(string title)
    {
        var hr = await app.LoginAsync("hr@demo.ecosys");
        var le = (await hr.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        var id = (await hr.PostJson($"{B}/recruitment/requisitions", new { legalEntityId = le, title, openings = 2, salaryMin = 91_500, salaryMax = 97_250, justification = "Confidential: replacing a dismissal",
            isPublished = true, description = "Plan routes for our fleet.\n\nYou have a class CE driving licence." }, HttpStatusCode.Created))["id"].Id();
        await (await app.LoginAsync("approver@demo.ecosys")).PostJson($"{B}/recruitment/requisitions/{id}/approve");
        return id;
    }

    [Fact]
    public async Task The_careers_page_lists_posted_jobs_without_salary_or_internal_notes()
    {
        var job = await PostedJobAsync("Fleet Planner");
        var anon = app.CreateClient();
        var res = await anon.GetAsync(Careers);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var raw = await res.Content.ReadAsStringAsync();
        Assert.Contains("Savanna Logistics Group", raw);
        Assert.Contains("Fleet Planner", raw);
        foreach (var secret in new[] { "salary", "91500", "97250", "justification", "Confidential", "REQ-" })
            Assert.DoesNotContain(secret, raw, StringComparison.OrdinalIgnoreCase);

        var one = await (await anon.GetAsync($"{Careers}/jobs/{job}")).Content.ReadAsStringAsync();
        Assert.Contains("class CE driving licence", one);
        foreach (var secret in new[] { "salary", "91500", "97250", "Confidential" })
            Assert.DoesNotContain(secret, one, StringComparison.OrdinalIgnoreCase);

        // An unknown company and a company with nothing posted look the same, so slugs can't be enumerated.
        var unknown = await anon.GetAsync($"{B}/public/careers/no-such-company");
        var empty = await anon.GetAsync($"{B}/public/careers/rival");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        Assert.Equal((await unknown.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["detail"].ToString(), (await empty.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["detail"].ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"{B}/public/careers/rival/jobs/{job}")).StatusCode); // another tenant's slug can't reach it
    }

    [Fact]
    public async Task A_job_appears_only_once_approved_and_posted()
    {
        var hr = await app.LoginAsync("hr@demo.ecosys");
        var anon = app.CreateClient();
        var le = (await hr.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();

        var noDescription = await hr.PostAsJsonAsync($"{B}/recruitment/requisitions", new { legalEntityId = le, title = "Accountant", openings = 1, isPublished = true }, PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.BadRequest, noDescription.StatusCode);
        Assert.Contains("job description", await noDescription.Content.ReadAsStringAsync());

        var req = (await hr.PostJson($"{B}/recruitment/requisitions", new { legalEntityId = le, title = "Warehouse Supervisor", openings = 1, salaryMin = 60000, salaryMax = 70000, isPublished = true, description = "Lead the Mombasa warehouse team." }, HttpStatusCode.Created))["id"].Id();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"{Careers}/jobs/{req}")).StatusCode); // awaiting approval

        await (await app.LoginAsync("approver@demo.ecosys")).PostJson($"{B}/recruitment/requisitions/{req}/approve");
        var listed = await (await anon.GetAsync($"{Careers}/jobs/{req}")).Content.ReadAsStringAsync();
        Assert.Contains("Warehouse Supervisor", listed);
        Assert.DoesNotContain("60000", listed);
        Assert.DoesNotContain("70000", listed);

        // Closing date in the past is refused; taking it down removes it.
        Assert.Equal(HttpStatusCode.BadRequest, (await hr.PostAsJsonAsync($"{B}/recruitment/requisitions/{req}/posting", new { published = true, description = "x", closingDate = "2020-01-01" }, PayrollApiFixture.Json)).StatusCode);
        await hr.PostJson($"{B}/recruitment/requisitions/{req}/posting", new { published = false, description = "Lead the Mombasa warehouse team." });
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"{Careers}/jobs/{req}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsync($"{Careers}/jobs/{req}/apply", Form("late@example.com"))).StatusCode);

        // Employees without hr.recruitment can't post jobs.
        var brian = await app.LoginAsync("brian.mwangi@demo.ecosys");
        Assert.Equal(HttpStatusCode.Forbidden, (await brian.PostAsJsonAsync($"{B}/recruitment/requisitions/{req}/posting", new { published = true, description = "x" }, PayrollApiFixture.Json)).StatusCode);
    }

    [Fact]
    public async Task Candidates_apply_without_an_account_and_land_in_the_pipeline()
    {
        var job = await PostedJobAsync("Transport Officer");
        var anon = app.CreateClient();
        var res = await anon.PostAsync($"{Careers}/jobs/{job}/apply", Form("wanjiku.m@example.com", retain: true, note: "Eight years on the Northern Corridor."));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        var hr = await app.LoginAsync("hr@demo.ecosys");
        var apps = (await hr.GetJson($"{B}/recruitment/requisitions/{job}/applications")).AsArray();
        var mine = Assert.Single(apps, a => a!["email"].Str() == "wanjiku.m@example.com")!;
        Assert.Equal("Careers page", mine["source"].Str());
        Assert.Equal("Applied", mine["stage"].Str());
        Assert.Equal("+254722123456", mine["phone"].Str());
        Assert.Equal("Eight years on the Northern Corridor.", mine["coverNote"].Str());
        Assert.True(mine["consentToRetain"]!.GetValue<bool>());
        var cv = mine["cvFileId"].Id();

        // HR can open the CV; the public can't.
        Assert.Equal(HttpStatusCode.OK, (await hr.GetAsync($"{B}/files/{cv}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"{B}/files/{cv}")).StatusCode);

        // The candidate gets an acknowledgement with no salary in it.
        // (Recipients are encrypted at rest, so look the message up by the application.)
        var appId = mine["id"].Id();
        var ack = await app.WithDbAsync(null, db => db.NotificationMessages.Where(m => m.EntityId == appId && m.Template == "recruitment.application.received").SingleAsync());
        Assert.Equal("wanjiku.m@example.com", ack.Recipient);
        Assert.Contains("Transport Officer", ack.Body);
        Assert.DoesNotContain("91", ack.Body);

        // Applying again is acknowledged but stored and emailed only once.
        Task<int> Acks() => app.WithDbAsync(null, db => db.NotificationMessages.CountAsync(m => m.Template == "recruitment.application.received"));
        var before = await Acks();
        Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsync($"{Careers}/jobs/{job}/apply", Form("wanjiku.m@example.com"))).StatusCode);
        Assert.Equal(1, (await EmailsAsync()).Count(e => e == "wanjiku.m@example.com"));
        Assert.Equal(before, await Acks());
    }

    [Fact]
    public async Task Applications_are_checked_before_anything_is_stored()
    {
        var job = await PostedJobAsync("Logistics Clerk");
        var anon = app.CreateClient();
        async Task<string> Rejected(MultipartFormDataContent form)
        {
            var r = await anon.PostAsync($"{Careers}/jobs/{job}/apply", form);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        }

        Assert.Contains("agree to how we use your information", await Rejected(Form("a1@example.com", privacy: false)));
        Assert.Contains("Attach your CV", await Rejected(Form("a2@example.com", fileName: "")));
        Assert.Contains("PDF or Word", await Rejected(Form("a3@example.com", fileName: "cv.exe", file: [0x4D, 0x5A, 0x90, 0x00, 0x03])));
        Assert.Contains("PDF or Word", await Rejected(Form("a4@example.com", fileName: "cv.docx", file: Encoding.ASCII.GetBytes("not really a document"))));
        Assert.Contains("PDF or Word", await Rejected(Form("a5@example.com", fileName: "cv.pdf", file: Encoding.ASCII.GetBytes("<html>"))));
        Assert.Contains("valid email", await Rejected(Form("not-an-email")));

        // A bot that fills the hidden field is told it worked, and nothing is kept.
        Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsync($"{Careers}/jobs/{job}/apply", Form("bot@example.com", website: "http://spam.example"))).StatusCode);

        var emails = await EmailsAsync();
        Assert.DoesNotContain(emails, e => e is "a1@example.com" or "a2@example.com" or "a3@example.com" or "a4@example.com" or "a5@example.com" or "bot@example.com");
        Assert.Equal(0, await app.WithDbAsync(null, db => db.StoredFiles.CountAsync(f => f.OwnerType == "JobApplication" && f.FileName == "cv.exe")));
    }

    [Fact]
    public async Task Me_includes_the_company_slug_for_sharing_links()
    {
        var hr = await app.LoginAsync("hr@demo.ecosys");
        Assert.Equal("demo", (await hr.GetJson($"{B}/auth/me"))["tenantSlug"].Str());
    }
}

public sealed class CareersRateLimitFixture : PayrollApiFixture
{
    protected override int CareersApplicationsPerHour => 2;
}

[Trait("Category", "Integration")]
public class CareersRateLimitTests(CareersRateLimitFixture app) : IClassFixture<CareersRateLimitFixture>
{
    [Fact]
    public async Task Applications_from_one_network_are_capped()
    {
        var anon = app.CreateClient();
        var job = (await anon.GetJson("/api/payroll/public/careers/demo"))["jobs"]![0]!["id"].Id();
        Task<HttpResponseMessage> Apply() => anon.PostAsync($"/api/payroll/public/careers/demo/jobs/{job}/apply", new MultipartFormDataContent { { new StringContent("x"), "firstName" } });
        Assert.Equal(HttpStatusCode.BadRequest, (await Apply()).StatusCode); // invalid, but it counts
        Assert.Equal(HttpStatusCode.BadRequest, (await Apply()).StatusCode);
        var third = await Apply();
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Contains("Too many applications", await third.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/payroll/public/careers/demo")).StatusCode); // browsing isn't limited
    }
}
