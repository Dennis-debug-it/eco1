using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Leave;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>
/// HR modules end to end. Demo reporting lines: Otieno (approver@, SL002) manages Grace (payroll@, SL005);
/// Brian (brian.mwangi@, SL006) manages James (SL014, no login). Periods used here are not used by other test classes.
/// </summary>
[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class HrModulesTests(PayrollApiFixture app)
{
    private const string B = "/api/payroll";

    private async Task<Guid> EmployeeIdAsync(string number) =>
        await app.WithDbAsync(null, db => db.Employees.Where(e => e.EmployeeNumber == number).Select(e => e.Id).FirstAsync());

    private static MultipartFormDataContent Pdf(string field = "file", string name = "evidence.pdf")
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("%PDF-1.4\n%test\n"u8.ToArray()), field, name);
        return content;
    }

    private async Task<(Guid Run, JsonArray Rows)> CalculateAsync(HttpClient payroll, string period)
    {
        var le = (await payroll.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        var group = (await payroll.GetJson($"{B}/organisation/payroll-groups?legalEntityId={le}")).AsArray().First(g => g!["code"].Str() == "MTH")!["id"].Id();
        var periodId = (await payroll.GetJson($"{B}/organisation/payroll-groups/{group}/periods")).AsArray().First(p => p!["name"].Str() == period)!["id"].Id();
        var run = (await payroll.PostJson($"{B}/runs", new { legalEntityId = le, payrollGroupId = group, payrollPeriodId = periodId }, HttpStatusCode.Created))["id"].Id();
        await payroll.PostJson($"{B}/runs/{run}/calculate");
        return (run, (await payroll.GetJson($"{B}/runs/{run}/employees")).AsArray());
    }

    // ------------------------------------------------------------------ Leave

    [Fact]
    public async Task Sick_leave_routes_to_manager_splits_pay_and_reduces_payroll()
    {
        var grace = await app.LoginAsync("payroll@demo.ecosys");
        var otieno = await app.LoginAsync("approver@demo.ecosys"); // Grace's line manager, no leave.approve permission
        var brian = await app.LoginAsync("brian.mwangi@demo.ecosys");
        var sickType = (await grace.GetJson($"{B}/leave/types")).AsArray().First(t => t!["code"].Str() == "SICK")!["id"].Id();

        // Server counts working days: Mon 3 – Fri 14 Nov 2025 = 10.
        var created = await grace.PostJson($"{B}/leave/requests", new { leaveTypeId = sickType, startDate = "2025-11-03", endDate = "2025-11-14", days = 99, reason = "Flu" }, HttpStatusCode.Created);
        var id = created["request"]!["id"].Id();
        Assert.Equal(10m, created["request"]!["days"].Dec());

        // Sick leave needs evidence; a colleague who is not the manager cannot decide.
        Assert.Equal(HttpStatusCode.Conflict, (await otieno.PostAsJsonAsync($"{B}/leave/requests/{id}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await grace.PostAsync($"{B}/leave/requests/{id}/attachment", Pdf())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await brian.PostAsJsonAsync($"{B}/leave/requests/{id}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await grace.PostAsJsonAsync($"{B}/leave/requests/{id}/approve", new { })).StatusCode);

        var approved = await otieno.PostJson($"{B}/leave/requests/{id}/approve", new { comment = "Get well" });
        Assert.Equal((7m, 3m, 0m), (approved["fullPayDays"].Dec(), approved["halfPayDays"].Dec(), approved["unpaidDays"].Dec()));

        // Payroll: November gross is October gross less 3 half days = 1.5 × (135,000 / 22).
        var (_, oct) = await CalculateAsync(grace, "October 2025");
        var (_, nov) = await CalculateAsync(grace, "November 2025");
        decimal Gross(JsonArray rows) => rows.First(r => r!["employeeNumber"].Str() == "SL005")!["grossPay"].Dec();
        Assert.Equal(Math.Round(135_000m / 22m * 1.5m, 2), Gross(oct) - Gross(nov));
    }

    [Fact]
    public async Task Working_days_exclude_public_holidays_and_annual_balance_is_enforced()
    {
        var brian = await app.LoginAsync("brian.mwangi@demo.ecosys");
        var annual = (await brian.GetJson($"{B}/leave/types")).AsArray().First(t => t!["code"].Str() == "ANNUAL")!["id"].Id();
        // Mon 22 – Fri 26 Dec 2025: Christmas (Thu) and Utamaduni Day (Fri) are holidays → 3 days.
        Assert.Equal(3m, (await brian.GetJson($"{B}/leave/days?leaveTypeId={annual}&start=2025-12-22&end=2025-12-26"))["days"].Dec());
        var tooMuch = await brian.PostAsJsonAsync($"{B}/leave/requests", new { leaveTypeId = annual, startDate = "2026-03-02", endDate = "2026-05-29" });
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        Assert.Contains("INSUFFICIENT_BALANCE", await tooMuch.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------ Exits

    [Fact]
    public async Task Redundancy_final_dues_flow_through_final_payroll()
    {
        var hr = await app.LoginAsync("hr@demo.ecosys");
        var approver = await app.LoginAsync("approver@demo.ecosys");
        var mercy = await EmployeeIdAsync("SL009"); // basic 58,000, hired 1 Oct 2020

        var sep = await hr.PostJson($"{B}/separations", new { employeeId = mercy, type = "Redundancy", noticeDate = "2026-01-02", lastWorkingDay = "2026-01-31", noticeDaysServed = 0, settleNoticeInLieu = true, reason = "Mombasa depot consolidation" }, HttpStatusCode.Created);
        var id = sep["id"].Id();
        var payrollAdmin = await app.LoginAsync("payroll@demo.ecosys");
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsync($"{B}/separations/{id}/calculate", null)).StatusCode); // HR doesn't see pay
        Assert.True((await hr.GetJson($"{B}/separations/{id}"))["duesHidden"]!.GetValue<bool>());
        var calc = await payrollAdmin.PostJson($"{B}/separations/{id}/calculate");
        var lines = calc["dues"]!["lines"]!.AsArray().ToDictionary(l => l!["code"].Str(), l => l!["amount"].Dec());
        Assert.Equal(5, calc["dues"]!["completedYearsOfService"]!.GetValue<int>());
        Assert.Equal(Math.Round(30 * Math.Round(58_000m / 30m, 2), 2), lines["NOTICE_PAY"]);          // 30 days' notice in lieu
        Assert.Equal(Math.Round(15m * 5 * Math.Round(58_000m / 30m, 2), 2), lines["SEVERANCE"]);     // s.40(1)(g)
        Assert.True(lines["LEAVE_PAY"] > 0);

        // Maker-checker: HR initiated, HR cannot approve; the approver can.
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsJsonAsync($"{B}/separations/{id}/approve", new { })).StatusCode);
        await approver.PostJson($"{B}/separations/{id}/approve");

        // The final regular run pays salary to the last day plus the dues, even though the status is now Terminated.
        var payroll = await app.LoginAsync("payroll@demo.ecosys");
        var (run, rows) = await CalculateAsync(payroll, "January 2026");
        var row = rows.First(r => r!["employeeNumber"].Str() == "SL009")!;
        var detail = await payroll.GetJson($"{B}/runs/{run}/employees/{mercy}");
        var codes = detail["lines"]!.AsArray().Select(l => l!["code"].Str()).ToList();
        Assert.Contains("NOTICE_PAY", codes); Assert.Contains("SEVERANCE", codes); Assert.Contains("LEAVE_PAY", codes);
        Assert.True(row["grossPay"].Dec() > 58_000m + lines["NOTICE_PAY"] + lines["SEVERANCE"]);

        await payroll.PostJson($"{B}/runs/{run}/submit");
        await (await app.LoginAsync("reviewer@demo.ecosys")).PostJson($"{B}/runs/{run}/review");
        await approver.PostJson($"{B}/runs/{run}/approve");
        await approver.PostJson($"{B}/runs/{run}/finalise");
        Assert.Equal(0, await app.WithDbAsync(null, db => db.ScheduledPayItems.CountAsync(i => i.SourceId == id && i.Status == PayItemStatus.Pending)));

        // Completion requires the checklist; afterwards a certificate of service can be issued.
        Assert.Equal(HttpStatusCode.Conflict, (await hr.PostAsJsonAsync($"{B}/separations/{id}/complete", new { })).StatusCode);
        foreach (var item in (await hr.GetJson($"{B}/separations/{id}"))["separation"]!["checklist"]!.AsArray())
            await hr.PostJson($"{B}/separations/{id}/checklist", new { key = item!["key"].Str(), done = true });
        Assert.Equal("Completed", (await hr.PostJson($"{B}/separations/{id}/complete"))["status"].Str());
        var cert = await hr.GetAsync($"{B}/employees/{mercy}/certificate-of-service.pdf");
        Assert.Equal("application/pdf", cert.Content.Headers.ContentType!.MediaType);
    }

    // ------------------------------------------------------------------ Performance

    [Fact]
    public async Task Review_cycle_to_compensation_review_creates_salary_change_and_bonus()
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var hr = await app.LoginAsync("hr@demo.ecosys");
        var brian = await app.LoginAsync("brian.mwangi@demo.ecosys");
        var grace = await app.LoginAsync("payroll@demo.ecosys");
        var approver = await app.LoginAsync("approver@demo.ecosys");
        var james = await EmployeeIdAsync("SL014");
        var cycle = (await hr.GetJson($"{B}/performance/cycles"))[0]!["id"].Id();

        await brian.PostAsync($"{B}/performance/cycles/{cycle}/employees/{james}/agree-goals", null);
        await hr.PostJson($"{B}/performance/cycles/{cycle}/advance", new { to = "Review" });

        var reviewId = (await brian.GetJson($"{B}/performance/reviews?cycleId={cycle}&scope=team")).AsArray().First(r => r!["employeeId"].Id() == james)!["id"].Id();
        var review = await brian.GetJson($"{B}/performance/reviews/{reviewId}");
        var goals = review["goals"]!.AsArray();
        var g60 = goals.First(g => g!["weightPercent"].Dec() == 60)!["id"].Id();
        var g40 = goals.First(g => g!["weightPercent"].Dec() == 40)!["id"].Id();

        // 360: the manager asks Grace for feedback; she answers.
        var fb = await brian.PostJson($"{B}/performance/reviews/{reviewId}/feedback", new { providerEmployeeId = await EmployeeIdAsync("SL005"), relationship = "Peer" }, HttpStatusCode.Created);
        await grace.PostJson($"{B}/performance/feedback/{fb["id"].Id()}", new { rating = 4, strengths = "Responsive" });

        var competencies = review["cycle"]!["competencies"]!.AsArray().Select(c => new { competency = c!.GetValue<string>(), managerRating = 4 }).ToList();
        var submitted = await brian.PostJson($"{B}/performance/reviews/{reviewId}/manager", new
        {
            goalRatings = new[] { new { goalId = g60, managerRating = 4 }, new { goalId = g40, managerRating = 5 } }, competencyRatings = competencies, comments = "Strong year",
        });
        // Goals (60×4 + 40×5)/100 = 4.4; competencies 4; blended 70/30 → 4.28.
        Assert.Equal(4.28m, submitted["managerScore"].Dec());

        Assert.Equal(HttpStatusCode.Forbidden, (await brian.PostAsJsonAsync($"{B}/performance/reviews/{reviewId}/calibrate", new { finalRating = 4 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await hr.PostAsJsonAsync($"{B}/performance/reviews/{reviewId}/calibrate", new { finalRating = 5 })).StatusCode); // differs without a note
        await hr.PostJson($"{B}/performance/reviews/{reviewId}/calibrate", new { finalRating = 4 });
        await hr.PostJson($"{B}/performance/cycles/{cycle}/advance", new { to = "Calibration" });

        var cr = await admin.PostJson($"{B}/performance/compensation-reviews", new { reviewCycleId = cycle, name = "2026 merit review", effectiveDate = "2026-03-01" }, HttpStatusCode.Created);
        var crId = cr["id"].Id();
        var detail = await admin.GetJson($"{B}/performance/compensation-reviews/{crId}");
        var line = detail["lines"]!.AsArray().Single(l => l!["employeeId"].Id() == james)!;
        Assert.Equal(90_950m, line["newBasic"].Dec());   // 85,000 + 7% (rating 4)
        Assert.Equal(51_000m, line["bonusAmount"].Dec()); // 60% of basic
        await admin.PostAsync($"{B}/performance/compensation-reviews/{crId}/submit", null);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"{B}/performance/compensation-reviews/{crId}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await approver.PostAsync($"{B}/performance/compensation-reviews/{crId}/approve", null)).StatusCode);

        var (salary, bonus) = await app.WithDbAsync(null, async db => (
            await db.EmployeeSalaryAssignments.Where(a => a.EmployeeId == james && a.EffectiveFrom == new DateOnly(2026, 3, 1)).Select(a => a.BasicSalary).SingleAsync(),
            await db.ScheduledPayItems.Where(i => i.EmployeeId == james && i.SourceId == crId).Select(i => i.Amount).SingleAsync()));
        Assert.Equal((90_950m, 51_000m), (salary, bonus));
    }

    // ------------------------------------------------------------------ Learning

    [Fact]
    public async Task Training_completion_issues_certificates_posts_cost_and_feeds_nita_claim()
    {
        var hr = await app.LoginAsync("hr@demo.ecosys");
        var brian = await app.LoginAsync("brian.mwangi@demo.ecosys");
        var grace = await app.LoginAsync("payroll@demo.ecosys");
        var otieno = await app.LoginAsync("approver@demo.ecosys");
        var james = await EmployeeIdAsync("SL014");
        var graceId = await EmployeeIdAsync("SL005");
        var session = (await hr.GetJson($"{B}/learning/sessions")).AsArray().First(s => s!["code"].Str() == "FA-L1" && s["status"].Str() == "Planned")!;
        var sid = session["id"].Id();

        Assert.Equal("Approved", (await brian.PostJson($"{B}/learning/sessions/{sid}/nominate", new { employeeId = james }))["status"].Str()); // manager nomination
        var self = await grace.PostJson($"{B}/learning/sessions/{sid}/nominate", new { employeeId = graceId });
        Assert.Equal("Nominated", self["status"].Str());
        await otieno.PostJson($"{B}/learning/enrolments/{self["id"].Id()}/approve");

        var enrolments = (await hr.GetJson($"{B}/learning/sessions/{sid}/enrolments")).AsArray();
        var results = enrolments.Select(e => new { enrolmentId = e!["id"].Id(), status = "Completed", score = 80, certificateNumber = $"FA-{e["employeeNumber"].Str()}" }).ToList();
        var completed = await hr.PostJson($"{B}/learning/sessions/{sid}/complete", new { results, actualTotalCost = 20_000 });
        Assert.NotNull(completed["costJournalId"]);
        var end = DateOnly.Parse(session["endDate"].Str());
        var after = (await hr.GetJson($"{B}/learning/sessions/{sid}/enrolments")).AsArray();
        Assert.All(after, e => { Assert.Equal(10_000m, e!["costAllocated"].Dec()); Assert.Equal(end.AddMonths(24).ToString("yyyy-MM-dd"), e["expiresOn"].Str()); });

        var journal = await app.WithDbAsync(null, db => db.PayrollJournals.Include(j => j.Lines).SingleAsync(j => j.Id == completed["costJournalId"].Id()));
        Assert.Equal(("TrainingCost", 20_000m, 20_000m), (journal.Source, journal.TotalDebit, journal.TotalCredit));

        var le = (await hr.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        var claim = await hr.PostJson($"{B}/learning/nita/claims", new { legalEntityId = le, from = end.AddDays(-1).ToString("yyyy-MM-dd"), to = end.ToString("yyyy-MM-dd") }, HttpStatusCode.Created);
        Assert.Equal(20_000m, claim["totalClaimed"].Dec());
        Assert.Equal(HttpStatusCode.Conflict, (await hr.PostAsJsonAsync($"{B}/learning/nita/claims", new { legalEntityId = le, from = end.AddDays(-1).ToString("yyyy-MM-dd"), to = end.ToString("yyyy-MM-dd") })).StatusCode); // no double claims

        var bond = await hr.PostJson($"{B}/learning/enrolments/{after.First(e => e!["employeeId"].Id() == james)!["id"].Id()}/bond", new { months = 12 }, HttpStatusCode.Created);
        Assert.Equal(10_000m, bond["amount"].Dec());

        // Seeded drivers' defensive-driving certificates expire within 60 days.
        var compliance = await hr.GetJson($"{B}/learning/compliance?legalEntityId={le}");
        Assert.Contains(compliance["rows"]!.AsArray(), r => r!["employeeNumber"].Str() == "SL010" && r["status"].Str() == "Expiring");
    }

    // ------------------------------------------------------------------ Cases, expenses, recruitment, documents

    [Fact]
    public async Task Dismissal_requires_show_cause_and_hearing()
    {
        var hr = await app.LoginAsync("hr@demo.ecosys");
        var employee = await EmployeeIdAsync("SL016");
        var c = await hr.PostJson($"{B}/cases", new { type = "Disciplinary", employeeId = employee, category = "Absence", summary = "Absent without leave 3 days", incidentDate = "2026-10-01" }, HttpStatusCode.Created);
        var id = c["id"].Id();
        var early = await hr.PostAsJsonAsync($"{B}/cases/{id}/step", new { to = "Decided", note = "Dismiss", outcome = "Dismissal", hearingHeld = true }, PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("s.41", await early.Content.ReadAsStringAsync());
        await hr.PostJson($"{B}/cases/{id}/step", new { to = "ShowCauseIssued", note = "Show cause issued", showCauseIssuedOn = "2026-10-05", responseDueOn = "2026-10-12" });
        await hr.PostJson($"{B}/cases/{id}/step", new { to = "HearingScheduled", note = "Hearing set", hearingAt = "2026-10-15T09:00:00+03:00", companion = "Union shop steward" });
        var decided = await hr.PostJson($"{B}/cases/{id}/step", new { to = "Decided", note = "Admitted absence; repeated", outcome = "Dismissal", hearingHeld = true });
        Assert.Equal("Dismissal", decided["outcome"].Str());
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.LoginAsync("approver@demo.ecosys")).GetAsync($"{B}/cases")).StatusCode);
        Assert.Empty((await (await app.LoginAsync("admin@rival.test", "rival")).GetJson($"{B}/cases")).AsArray());
    }

    [Fact]
    public async Task Expense_claim_needs_receipts_two_approvals_and_taxes_per_diem_excess()
    {
        var brian = await app.LoginAsync("brian.mwangi@demo.ecosys");
        var finance = await app.LoginAsync("finance@demo.ecosys");
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var cats = (await brian.GetJson($"{B}/expenses/categories")).AsArray();
        Guid Cat(string code) => cats.First(c => c!["code"].Str() == code)!["id"].Id();
        var claim = await brian.PostJson($"{B}/expenses/claims", new
        {
            title = "Mombasa site visit",
            lines = new object[]
            {
                new { categoryId = Cat("PERDIEM"), date = "2026-10-20", amount = 36_000, days = 3, description = "Per diem 3 days" },
                new { categoryId = Cat("TRAVEL"), date = "2026-10-20", amount = 2_000, days = 1, description = "Taxi" },
            },
        }, HttpStatusCode.Created);
        var id = claim["id"].Id();
        Assert.Equal(6_000m, claim["taxableAmount"].Dec()); // 36,000 − 3 × 10,000 tax-free
        Assert.Equal(HttpStatusCode.BadRequest, (await brian.PostAsync($"{B}/expenses/claims/{id}/submit", null)).StatusCode); // taxi receipt missing
        Assert.Equal(HttpStatusCode.OK, (await brian.PostAsync($"{B}/expenses/claims/{id}/lines/1/receipt", Pdf())).StatusCode);
        await brian.PostJson($"{B}/expenses/claims/{id}/submit");

        Assert.Equal(HttpStatusCode.Forbidden, (await brian.PostAsync($"{B}/expenses/claims/{id}/approve", null)).StatusCode);
        Assert.Equal("ManagerApproved", (await finance.PostJson($"{B}/expenses/claims/{id}/approve"))["status"].Str()); // finance stands in for the manager
        Assert.Equal(HttpStatusCode.Forbidden, (await finance.PostAsync($"{B}/expenses/claims/{id}/approve", null)).StatusCode); // second step must be someone else
        Assert.Equal("Approved", (await admin.PostJson($"{B}/expenses/claims/{id}/approve"))["status"].Str());
        var items = await app.WithDbAsync(null, db => db.ScheduledPayItems.Where(i => i.SourceId == id).OrderBy(i => i.Amount).Select(i => i.Amount).ToListAsync());
        Assert.Equal([6_000m, 32_000m], items);
    }

    [Fact]
    public async Task Hiring_an_accepted_offer_creates_employee_and_onboarding()
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var req = (await admin.GetJson($"{B}/recruitment/requisitions")).AsArray().First(r => r!["requisition"]!["title"].Str() == "Driver")!["requisition"]!;
        var apps = (await admin.GetJson($"{B}/recruitment/requisitions/{req["id"].Id()}/applications")).AsArray();
        var kevin = apps.First(a => a!["firstName"].Str() == "Kevin")!["id"].Id();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{B}/recruitment/applications/{kevin}/offer", new { basic = 60_000, startDate = "2026-12-01", status = "Sent" })).StatusCode); // above range
        await admin.PostJson($"{B}/recruitment/applications/{kevin}/offer", new { basic = 42_000, startDate = "2026-12-01", jobTitle = "Driver", status = "Accepted" });
        var group = await app.WithDbAsync(null, db => db.PayrollGroups.Where(g => g.Code == "MTH").Select(g => g.Id).FirstAsync());
        var hired = await admin.PostJson($"{B}/recruitment/applications/{kevin}/hire", new { employeeNumber = "SL017", payrollGroupId = group, probationMonths = 6 });
        var empId = hired["employeeId"].Id();
        var tasks = (await admin.GetJson($"{B}/recruitment/onboarding?employeeId={empId}")).AsArray();
        Assert.Equal(8, tasks.Count);
        var emp = await admin.GetJson($"{B}/employees/{empId}");
        Assert.Equal("Probation", emp["employee"]!["employmentType"].Str());
        Assert.Equal(42_000m, emp["salaryAssignments"]![0]!["basicSalary"].Dec());
        Assert.Equal("Filled", (await admin.GetJson($"{B}/recruitment/requisitions")).AsArray().First(r => r!["requisition"]!["id"].Id() == req["id"].Id())!["requisition"]!["status"].Str());
    }

    [Fact]
    public async Task Employee_sees_visible_documents_only()
    {
        var hr = await app.LoginAsync("hr@demo.ecosys");
        var brian = await app.LoginAsync("brian.mwangi@demo.ecosys");
        var brianId = await EmployeeIdAsync("SL006");
        async Task<Guid> Upload(string category, bool visible)
        {
            var form = Pdf();
            form.Add(new StringContent($$"""{"employeeId":"{{brianId}}","category":"{{category}}","title":"{{category}} letter","visibleToEmployee":{{visible.ToString().ToLowerInvariant()}}}"""), "meta");
            var res = await hr.PostAsync($"{B}/documents", form);
            return (await res.Json(HttpStatusCode.Created))["storedFileId"].Id();
        }
        var contract = await Upload("Contract", true);
        var warning = await Upload("Disciplinary", true); // disciplinary documents are never employee-visible
        Assert.Equal(HttpStatusCode.OK, (await brian.GetAsync($"{B}/files/{contract}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await brian.GetAsync($"{B}/files/{warning}")).StatusCode);
        Assert.Single((await brian.GetJson($"{B}/documents?employeeId={brianId}")).AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await brian.GetAsync($"{B}/documents?employeeId={await EmployeeIdAsync("SL001")}")).StatusCode);
    }

    [Fact]
    public async Task Hr_analytics_summarises_workforce()
    {
        var hr = await app.LoginAsync("hr@demo.ecosys");
        var a = await hr.GetJson($"{B}/hr-analytics");
        Assert.True(a["headcount"]!.GetValue<int>() >= 15);
        Assert.Equal(12, a["trend"]!.AsArray().Count);
        Assert.NotNull(a["open"]!["cases"]); // HR administrators hold hr.cases
    }

    private static MultipartFormDataContent Dependant(object meta, bool withDocument = true)
    {
        var content = new MultipartFormDataContent { { new StringContent(System.Text.Json.JsonSerializer.Serialize(meta, PayrollApiFixture.Json)), "meta" } };
        if (withDocument) content.Add(new ByteArrayContent("%PDF-1.4\n%birth certificate\n"u8.ToArray()), "files", "birth-certificate.pdf");
        return content;
    }

    [Fact]
    public async Task Dependants_need_documents_then_verification_and_a_second_approver()
    {
        var brian = await app.LoginAsync("brian.mwangi@demo.ecosys");
        var hr = await app.LoginAsync("hr@demo.ecosys");
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var me = (await brian.GetJson($"{B}/auth/me"))["employeeId"].Id();

        // A supporting document is required.
        Assert.Equal(HttpStatusCode.BadRequest, (await brian.PostAsync($"{B}/benefits/dependants", Dependant(new { employeeId = me, fullName = "Wanjiku Mwangi", relationship = "Spouse" }, withDocument: false))).StatusCode);
        // Imani already holds an approved 100% share, so another beneficiary is refused up front.
        Assert.Equal(HttpStatusCode.BadRequest, (await brian.PostAsync($"{B}/benefits/dependants", Dependant(new { employeeId = me, fullName = "Wanjiku Mwangi", relationship = "Spouse", isBeneficiary = true, beneficiaryPercent = 10 }))).StatusCode);

        var created = await (await brian.PostAsync($"{B}/benefits/dependants", Dependant(new { employeeId = me, fullName = "Wanjiku Mwangi", relationship = "Spouse", dateOfBirth = "1993-06-14", idNumber = "29876543" }))).Json(HttpStatusCode.Created);
        var id = created["id"].Id();
        Assert.Equal("Submitted", created["status"].Str());
        var fileId = created["documents"]![0]!["id"].Id();
        Assert.True(await app.WithDbAsync(null, db => db.NotificationMessages.AnyAsync(m => m.Template == "benefits.dependant.submitted" && m.EntityId == id)));

        // Sequence: the employee can't act on it; approval before verification is refused; the verifier can't also approve.
        Assert.Equal(HttpStatusCode.Forbidden, (await brian.PostAsync($"{B}/benefits/dependants/{id}/verify", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"{B}/benefits/dependants/{id}/approve", null)).StatusCode);
        Assert.Equal("Verified", (await hr.PostJson($"{B}/benefits/dependants/{id}/verify"))["status"].Str());
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsync($"{B}/benefits/dependants/{id}/approve", null)).StatusCode);
        Assert.Equal("Approved", (await admin.PostJson($"{B}/benefits/dependants/{id}/approve"))["status"].Str());

        var mine = (await brian.GetJson($"{B}/benefits/dependants/{me}")).AsArray().First(d => d!["id"].Id() == id)!;
        Assert.Equal("Approved", mine["status"].Str());
        Assert.Equal("Amina Hassan", mine["verifiedBy"].Str());
        Assert.DoesNotContain("29876543", mine.ToJsonString()); // ID number masked
        // Documents: the employee and HR can open them; others cannot.
        Assert.Equal(HttpStatusCode.OK, (await brian.GetAsync($"{B}/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hr.GetAsync($"{B}/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.LoginAsync("approver@demo.ecosys")).GetAsync($"{B}/files/{fileId}")).StatusCode);
        // Only HR changes an approved record.
        Assert.Equal(HttpStatusCode.Forbidden, (await brian.DeleteAsync($"{B}/benefits/dependants/{id}")).StatusCode);

        // Rejection goes back to the employee with the reason.
        var second = (await (await brian.PostAsync($"{B}/benefits/dependants", Dependant(new { employeeId = me, fullName = "Baraka Mwangi", relationship = "Child" }))).Json(HttpStatusCode.Created))["id"].Id();
        Assert.Equal(HttpStatusCode.BadRequest, (await hr.PostAsJsonAsync($"{B}/benefits/dependants/{second}/reject", new { reason = "" })).StatusCode);
        Assert.Equal("Rejected", (await hr.PostJson($"{B}/benefits/dependants/{second}/reject", new { reason = "The certificate is for a different child" }))["status"].Str());
        Assert.True(await app.WithDbAsync(null, db => db.NotificationMessages.AnyAsync(m => m.Template == "benefits.dependant.rejected" && m.EntityId == second)));
        Assert.Equal(HttpStatusCode.NoContent, (await brian.DeleteAsync($"{B}/benefits/dependants/{second}")).StatusCode);

        // HR can't approve their own family.
        var amina = await app.WithDbAsync(null, db => db.Users.Where(u => u.Email == "hr@demo.ecosys").Select(u => u.EmployeeId!.Value).FirstAsync());
        var own = (await (await hr.PostAsync($"{B}/benefits/dependants", Dependant(new { employeeId = amina, fullName = "Juma Hassan", relationship = "Spouse" }))).Json(HttpStatusCode.Created))["id"].Id();
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsync($"{B}/benefits/dependants/{own}/verify", null)).StatusCode);
        await admin.PostJson($"{B}/benefits/dependants/{own}/verify");
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsync($"{B}/benefits/dependants/{own}/approve", null)).StatusCode);

        var queue = (await hr.GetJson($"{B}/benefits/dependants")).AsArray();
        Assert.Contains(queue, q => q!["dependant"]!["id"].Id() == own);
        Assert.DoesNotContain(queue, q => q!["dependant"]!["id"].Id() == id); // approved ones leave the queue
    }

    [Fact]
    public async Task Employees_do_not_see_what_medical_cover_costs()
    {
        var brian = await app.LoginAsync("brian.mwangi@demo.ecosys");
        var me = (await brian.GetJson($"{B}/auth/me"))["employeeId"].Id();
        var cover = (await brian.GetJson($"{B}/benefits/enrolments?employeeId={me}")).AsArray();
        Assert.NotEmpty(cover);
        Assert.All(cover, c => { Assert.Null(c!["premiumByCover"]); Assert.Null(c["employeeSharePercent"]); });
        Assert.Equal(HttpStatusCode.Forbidden, (await brian.GetAsync($"{B}/benefits/schemes")).StatusCode);
        var hr = await app.LoginAsync("hr@demo.ecosys");
        Assert.NotNull((await hr.GetJson($"{B}/benefits/enrolments?employeeId={me}"))[0]!["premiumByCover"]);
    }

    [Fact]
    public async Task Organogram_follows_reporting_lines_and_positions()
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var chart = await admin.GetJson($"{B}/organisation/chart");
        var people = chart["people"]!.AsArray();
        // (Other tests add people without a manager, so look for the MD rather than counting the top row.)
        var md0 = people.First(p => p!["employeeNumber"].Str() == "SL001")!;
        Assert.Null(md0["managerId"]);
        Assert.Equal("Managing Director", md0["jobTitle"].Str());
        Assert.Equal("Band A", md0["grade"].Str());
        var brian = people.First(p => p!["employeeNumber"].Str() == "SL006")!;
        Assert.Equal(md0["id"].Id(), brian["managerId"].Id());
        Assert.Equal("Band B", brian["grade"].Str());
        Assert.DoesNotContain("basicSalary", chart.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        // Positions view: vacant seats counted.
        var driver = chart["positions"]!.AsArray().First(p => p!["title"].Str() == "Driver")!;
        Assert.True(driver["vacancies"]!.GetValue<int>() >= 1);

        // Employees without employees.read can't read it.
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.LoginAsync("brian.mwangi@demo.ecosys")).GetAsync($"{B}/organisation/chart")).StatusCode);

        // A position cannot be made to report to itself, or to one below it.
        var positions = (await admin.GetJson($"{B}/positions")).AsArray();
        var md = positions.First(p => p!["title"].Str() == "Managing Director")!;
        var under = positions.First(p => p!["reportsToPositionId"] is JsonNode r && r.Id() == md["id"].Id())!;
        var body = md.AsObject().DeepClone().AsObject();
        body["reportsToPositionId"] = md["id"]!.DeepClone();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{B}/positions/{md["id"].Id()}", body, PayrollApiFixture.Json)).StatusCode);
        body["reportsToPositionId"] = under["id"]!.DeepClone();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{B}/positions/{md["id"].Id()}", body, PayrollApiFixture.Json)).StatusCode);
    }

    [Fact]
    public async Task Offer_letter_is_drafted_sent_accepted_online_and_filed_at_hire()
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var hr = await app.LoginAsync("hr@demo.ecosys");
        var le = (await admin.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        var req = await admin.PostJson($"{B}/recruitment/requisitions", new { legalEntityId = le, title = "Accounts Assistant", openings = 1, salaryMin = 50_000, salaryMax = 70_000, employmentType = "Permanent" }, HttpStatusCode.Created);
        await (await app.LoginAsync("approver@demo.ecosys")).PostJson($"{B}/recruitment/requisitions/{req["id"].Id()}/approve");
        var form = new MultipartFormDataContent { { new StringContent("Njeri"), "firstName" }, { new StringContent("Wambui"), "lastName" }, { new StringContent("njeri.wambui@candidate.example"), "email" } };
        var appId = (await (await admin.PostAsync($"{B}/recruitment/requisitions/{req["id"].Id()}/applications", form)).Json(HttpStatusCode.Created))["id"].Id();

        // Terms first; HR without salary access can't draft a letter that states the salary.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{B}/recruitment/applications/{appId}/offer-letter", new { })).StatusCode);
        await admin.PostJson($"{B}/recruitment/applications/{appId}/offer", new { basic = 60_000, startDate = "2027-01-04", jobTitle = "Accounts Assistant", status = "Draft" });
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsJsonAsync($"{B}/recruitment/applications/{appId}/offer-letter", new { })).StatusCode);

        // The template only takes known fields.
        var tpl = (await admin.GetJson($"{B}/recruitment/offer-template/{le}"))["template"]!;
        Assert.Contains("{basic_salary}", tpl["body"].Str());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{B}/recruitment/offer-template/{le}", new { subject = "Offer", body = "Your pay is {salary}.", probationMonths = 3, acceptWithinDays = 7 })).StatusCode);

        var draft = await admin.PostJson($"{B}/recruitment/applications/{appId}/offer-letter", new { });
        var letterId = draft["id"].Id();
        Assert.Equal("Draft", draft["status"].Str());
        Assert.Contains("KES 60,000.00 per month", draft["preview"]!["body"].Str());
        Assert.Contains("Dear Njeri,", draft["preview"]!["body"].Str());
        Assert.Empty(draft["preview"]!["unfilled"]!.AsArray());

        // A field the system can't fill blocks sending.
        var acceptBy = BusinessClockToday().AddDays(10).ToString("yyyy-MM-dd");
        await admin.PutJson($"{B}/recruitment/offer-letters/{letterId}", new { subject = draft["subject"].Str(), body = draft["body"].Str() + "\n\nBonus: {bonus}", acceptBy, signatoryName = "Wanjiru Kamau", signatoryTitle = "Managing Director" });
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{B}/recruitment/offer-letters/{letterId}/send", new { emailLink = true })).StatusCode);
        await admin.PutJson($"{B}/recruitment/offer-letters/{letterId}", new { subject = draft["subject"].Str(), body = draft["body"].Str(), acceptBy, signatoryName = "Wanjiru Kamau", signatoryTitle = "Managing Director" });

        var draftPdf = System.Text.Encoding.Latin1.GetString(await (await admin.GetAsync($"{B}/recruitment/offer-letters/{letterId}/pdf")).Content.ReadAsByteArrayAsync());
        Assert.StartsWith("%PDF", draftPdf);
        Assert.Contains("DRAFT", draftPdf);

        var sent = await admin.PostJson($"{B}/recruitment/offer-letters/{letterId}/send", new { emailLink = true });
        Assert.Equal("Sent", sent["letter"]!["status"].Str());
        var link = sent["link"].Str();
        var token = link[(link.LastIndexOf('/') + 1)..];
        var email = await app.WithDbAsync(null, db => db.NotificationMessages.Where(m => m.Template == "recruitment.offer.sent" && m.EntityId == letterId).Select(m => m.Body).FirstAsync());
        Assert.Contains(link, email);
        Assert.DoesNotContain("60,000", email); // no salary in emails
        // Terms are fixed while the letter is out.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{B}/recruitment/applications/{appId}/offer", new { basic = 65_000, startDate = "2027-01-04", jobTitle = "Accounts Assistant", status = "Sent" })).StatusCode);

        // The candidate's side: no sign-in, the link is the key.
        var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"{B}/offers/not-a-real-token")).StatusCode);
        var view = await anon.GetJson($"{B}/offers/{token}");
        Assert.Equal("Sent", view["status"].Str());
        Assert.Equal("Njeri Wambui", view["candidate"].Str());
        Assert.Contains(view["blocks"]!.AsArray(), b => b!["text"].Str().Contains("60,000.00"));
        Assert.StartsWith("%PDF", System.Text.Encoding.Latin1.GetString(await (await anon.GetAsync($"{B}/offers/{token}/pdf")).Content.ReadAsByteArrayAsync())[..4]);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync($"{B}/offers/{token}/respond", new { accept = true, fullName = "" })).StatusCode);
        Assert.Equal("Accepted", (await anon.PostJson($"{B}/offers/{token}/respond", new { accept = true, fullName = "Njeri Wambui" }))["status"].Str());
        Assert.Equal(HttpStatusCode.Conflict, (await anon.PostAsJsonAsync($"{B}/offers/{token}/respond", new { accept = false })).StatusCode);

        var letters = (await admin.GetJson($"{B}/recruitment/applications/{appId}/offer-letters")).AsArray();
        Assert.Equal("Accepted", letters[0]!["status"].Str());
        Assert.Equal("Njeri Wambui", letters[0]!["responseName"].Str());
        var acceptedFile = letters[0]!["acceptedFileId"].Id();
        Assert.Contains("Accepted electronically by Njeri Wambui", System.Text.Encoding.Latin1.GetString(await (await admin.GetAsync($"{B}/files/{acceptedFile}")).Content.ReadAsByteArrayAsync()));
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.GetAsync($"{B}/files/{acceptedFile}")).StatusCode); // states the salary
        Assert.True(await app.WithDbAsync(null, db => db.NotificationMessages.AnyAsync(m => m.Template == "recruitment.offer.accepted" && m.EntityId == letterId)));
        var application = (await admin.GetJson($"{B}/recruitment/requisitions/{req["id"].Id()}/applications")).AsArray().First(a => a!["id"].Id() == appId)!;
        Assert.Equal("Accepted", application["offerStatus"].Str());

        // At hire the accepted letter joins the employee's documents.
        var group = (await admin.GetJson($"{B}/organisation/payroll-groups?legalEntityId={le}"))[0]!["id"].Id();
        var hired = await admin.PostJson($"{B}/recruitment/applications/{appId}/hire", new { employeeNumber = "SL090", payrollGroupId = group, probationMonths = 3 });
        var docs = (await admin.GetJson($"{B}/documents?employeeId={hired["employeeId"].Id()}")).AsArray();
        Assert.Contains(docs, d => d!["title"].Str() == "Offer letter (accepted)" && d["category"].Str() == "Contract");
    }

    private static DateOnly BusinessClockToday() => Ecosys.Payroll.Domain.Common.BusinessClock.Today();
}
