using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>
/// Acceptance criteria exercised end-to-end through HTTP against PostgreSQL.
/// Each test uses its own payroll period so tests are independent within the shared database.
/// </summary>
[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class PayrollApiTests(PayrollApiFixture app)
{
    private const string B = "/api/payroll";

    private async Task<(Guid Le, Guid Group, Guid Period)> PeriodAsync(HttpClient c, string name)
    {
        var le = (await c.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        var group = (await c.GetJson($"{B}/organisation/payroll-groups?legalEntityId={le}"))[0]!["id"].Id();
        var periods = (await c.GetJson($"{B}/organisation/payroll-groups/{group}/periods")).AsArray();
        return (le, group, periods.First(p => p!["name"].Str() == name)!["id"].Id());
    }

    /// <summary>
    /// The demo data has 14 monthly periods (from January 2025) and other tests use all of them; tests that need a period of
    /// their own take one from 2024, generated on demand (generation skips existing periods). Not a future year: finalising a
    /// future period would lock salary changes dated before it.
    /// </summary>
    private async Task<string> OwnPeriodAsync(string month)
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var le = (await admin.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        var group = (await admin.GetJson($"{B}/organisation/payroll-groups?legalEntityId={le}"))[0]!["id"].Id();
        await admin.PostJson($"{B}/organisation/payroll-groups/{group}/periods/generate", new { year = 2024, payDay = 28 });
        return $"{month} 2024";
    }

    private async Task<Guid> CalculatedRunAsync(HttpClient payroll, string periodName)
    {
        var (le, group, period) = await PeriodAsync(payroll, periodName);
        var run = await payroll.PostJson($"{B}/runs", new { legalEntityId = le, payrollGroupId = group, payrollPeriodId = period }, HttpStatusCode.Created);
        var id = run["id"].Id();
        var calc = await payroll.PostJson($"{B}/runs/{id}/calculate");
        Assert.Equal("Calculated", calc["status"].Str());
        Assert.Equal(0, calc["errorCount"]!.GetValue<int>());
        return id;
    }

    private async Task<Guid> ApprovedRunAsync(string periodName)
    {
        var payroll = await app.LoginAsync("payroll@demo.ecosys");
        var approver = await app.LoginAsync("approver@demo.ecosys");
        var id = await CalculatedRunAsync(payroll, periodName);
        await payroll.PostJson($"{B}/runs/{id}/submit", new { text = "ready" });
        await (await app.LoginAsync("reviewer@demo.ecosys")).PostJson($"{B}/runs/{id}/review", new { text = "checked" });
        await approver.PostJson($"{B}/runs/{id}/approve", new { text = "ok" });
        return id;
    }

    // ------------------------------------------------------------------ Security

    [Fact]
    public async Task Tenant_A_cannot_read_or_reference_tenant_B_records()
    {
        var demo = await app.LoginAsync("admin@demo.ecosys");
        var rival = await app.LoginAsync("admin@rival.test", "rival");

        // Direct reads of another tenant's ids look exactly like missing records.
        Assert.Equal(HttpStatusCode.NotFound, (await demo.GetAsync($"{B}/employees/{app.RivalEmployeeId}")).StatusCode);

        var demoEmployees = await demo.GetJson($"{B}/employees?limit=500");
        Assert.DoesNotContain(demoEmployees["items"]!.AsArray(), e => e!["employeeNumber"].Str() == "R001");
        var rivalEmployees = await rival.GetJson($"{B}/employees?limit=500");
        Assert.Equal(1, rivalEmployees["total"]!.GetValue<int>());
        Assert.Single((await rival.GetJson($"{B}/organisation/legal-entities")).AsArray());

        // Writing a record that points at the other tenant's legal entity is rejected even though the FK would accept it.
        var res = await demo.PostAsJsonAsync($"{B}/organisation/branches", new { legalEntityId = app.RivalLegalEntityId, code = "X", name = "Smuggled", isActive = true });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        // An employee referencing another tenant's payroll group is rejected.
        var rivalGroup = await app.WithDbAsync(app.RivalTenantId, db => db.PayrollGroups.Select(g => g.Id).FirstAsync());
        var demoLe = (await demo.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        var bad = await demo.PostAsJsonAsync($"{B}/employees", new { legalEntityId = demoLe, employeeNumber = "X999", firstName = "A", lastName = "B", hireDate = "2025-01-01", payrollGroupId = rivalGroup, gender = "Unspecified", employmentType = "Permanent", status = "Active" }, PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Salary_visibility_is_independent_of_employee_access()
    {
        var hr = await app.LoginAsync("hr@demo.ecosys");
        var list = await hr.GetJson($"{B}/employees?limit=5");
        Assert.All(list["items"]!.AsArray(), e => Assert.Null(e!["basicSalary"]?.GetValue<decimal?>()));
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.GetAsync($"{B}/runs")).StatusCode);
        var someone = list["items"]![0]!["id"].Id();
        var detail = await hr.GetJson($"{B}/employees/{someone}");
        Assert.Null(detail["salaryAssignments"]);
    }

    [Fact]
    public async Task Sensitive_fields_are_encrypted_at_rest_and_masked_for_readers()
    {
        var raw = await app.WithDbAsync(null, async db =>
        {
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            await db.Database.OpenConnectionAsync();
            cmd.CommandText = "select account_number from employee_bank_accounts limit 1";
            return (string)(await cmd.ExecuteScalarAsync())!;
        });
        Assert.StartsWith("enc:v1:", raw);

        var reviewer = await app.LoginAsync("reviewer@demo.ecosys"); // employees.read but not employees.write
        var id = (await reviewer.GetJson($"{B}/employees?limit=1"))["items"]![0]!["id"].Id();
        var d = await reviewer.GetJson($"{B}/employees/{id}");
        Assert.StartsWith("****", d["bankAccounts"]![0]!["accountNumber"].Str());
        Assert.StartsWith("****", d["statutory"]!["kraPin"].Str());
    }

    // ------------------------------------------------------------------ Workflow

    [Fact]
    public async Task Full_lifecycle_with_maker_checker_reconciliation_journal_and_payslips()
    {
        var payroll = await app.LoginAsync("payroll@demo.ecosys");
        var approver = await app.LoginAsync("approver@demo.ecosys");
        var finance = await app.LoginAsync("finance@demo.ecosys");
        var (le, group, period) = await PeriodAsync(payroll, "March 2025");

        var run = (await payroll.PostJson($"{B}/runs", new { legalEntityId = le, payrollGroupId = group, payrollPeriodId = period }, HttpStatusCode.Created))["id"].Id();

        // Duplicate regular run for the same group/period is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await payroll.PostAsJsonAsync($"{B}/runs", new { legalEntityId = le, payrollGroupId = group, payrollPeriodId = period })).StatusCode);

        var calc = await payroll.PostJson($"{B}/runs/{run}/calculate");
        Assert.Equal(15, calc["employeeCount"]!.GetValue<int>()); // 16 on file; SL014 is hired in September 2025
        var net = calc["totalNet"].Dec();
        Assert.Equal(calc["totalGross"].Dec() - calc["totalEmployeeDeductions"].Dec(), net);

        await payroll.PostJson($"{B}/runs/{run}/submit", new { text = "March" });

        // Preparer lacks approve permission; a reviewer can't approve either.
        Assert.Equal(HttpStatusCode.Forbidden, (await payroll.PostAsJsonAsync($"{B}/runs/{run}/approve", new { })).StatusCode);
        var reviewer = await app.LoginAsync("reviewer@demo.ecosys");
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.PostAsJsonAsync($"{B}/runs/{run}/approve", new { })).StatusCode);
        // Review comes before approval (legal entity setting), and only once per submission.
        var early = await approver.PostAsJsonAsync($"{B}/runs/{run}/approve", new { });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("must be reviewed", await early.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await payroll.PostAsJsonAsync($"{B}/runs/{run}/review", new { })).StatusCode); // preparer
        await reviewer.PostJson($"{B}/runs/{run}/review", new { text = "Variances explained" });
        Assert.Equal(HttpStatusCode.Conflict, (await reviewer.PostAsJsonAsync($"{B}/runs/{run}/review", new { })).StatusCode);
        Assert.Equal("Approved", (await approver.PostJson($"{B}/runs/{run}/approve", new { text = "ok" }))["status"].Str());

        // Approved payroll cannot be edited.
        Assert.Equal(HttpStatusCode.Conflict, (await payroll.PostAsJsonAsync($"{B}/runs/{run}/calculate", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await payroll.PostAsJsonAsync($"{B}/runs/{run}/variable-inputs", Array.Empty<object>())).StatusCode);

        // Payment file reconciles to approved net.
        var file = await finance.PostAsJsonAsync($"{B}/runs/{run}/payment-file", new { format = "GenericCsv" }, PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        var csv = (await file.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1);
        Assert.Equal(net, csv.Sum(l => decimal.Parse(l.Split(',')[5], System.Globalization.CultureInfo.InvariantCulture)));

        var summary = await finance.GetJson($"{B}/runs/{run}");
        Assert.Equal("PaymentProcessing", summary["run"]!["status"].Str());
        var batch = summary["batches"]![0]!;
        Assert.Equal(net, batch["totalAmount"].Dec());
        await finance.PostJson($"{B}/runs/{run}/payments/confirm", new { batchId = batch["id"].Id(), bankReference = "BANK-REF-1" });

        Assert.Equal("Finalised", (await approver.PostJson($"{B}/runs/{run}/finalise"))["status"].Str());

        var journal = (await finance.GetJson($"{B}/runs/{run}/journal"))[0]!;
        Assert.Equal(journal["totalDebit"].Dec(), journal["totalCredit"].Dec());
        Assert.Equal(calc["totalEmployerCost"].Dec(), journal["totalDebit"].Dec());
        Assert.Contains(journal["lines"]!.AsArray(), l => l!["description"].Str() == "House allowance");

        // Employee sees own payslip but not a colleague's.
        var brian = await app.LoginAsync("brian.mwangi@demo.ecosys");
        var me = await brian.GetJson($"{B}/auth/me");
        var slips = (await brian.GetJson($"{B}/employees/{me["employeeId"].Id()}/payslips")).AsArray();
        var slip = slips.First(s => s!["runReference"].Str() == summary["run"]!["reference"].Str())!;
        var pdf = await brian.GetAsync($"{B}/payslips/{slip["id"].Id()}/pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF-1.4", Encoding.Latin1.GetString(await pdf.Content.ReadAsByteArrayAsync())[..8]);
        var other = (await payroll.GetJson($"{B}/runs/{run}/employees")).AsArray().First(e => e!["employeeId"].Id() != me["employeeId"].Id())!["employeeId"].Id();
        Assert.Equal(HttpStatusCode.Forbidden, (await brian.GetAsync($"{B}/employees/{other}/payslips")).StatusCode);

        // Audit trail has the whole lifecycle.
        var audit = await (await app.LoginAsync("auditor@demo.ecosys")).GetJson($"{B}/reports/audit?entityId={run}&limit=100");
        var actions = audit["items"]!.AsArray().Select(a => a!["action"].Str()).ToList();
        foreach (var a in new[] { "payroll.run.created", "payroll.run.calculated", "payroll.run.submitted", "payroll.run.approved", "payroll.run.paymentgenerated", "payroll.run.paymentconfirmed", "payroll.run.finalised" })
            Assert.Contains(a, actions);

        // Payslips were announced through the outbox and the dispatcher delivers them.
        var queued = await app.WithDbAsync(null, db => db.NotificationMessages.CountAsync(m => m.EntityId == run && m.Template == "payslip.issued"));
        Assert.True(queued >= 15);
        var dispatcher = app.Services.GetRequiredService<NotificationDispatcher>();
        var sent = 0;
        for (int n; (n = await dispatcher.DispatchOnceAsync(default)) > 0;) sent += n; // 50 per pass
        Assert.True(sent >= queued);
        Assert.Equal(0, await app.WithDbAsync(null, db => db.NotificationMessages.CountAsync(m => m.EntityId == run && m.Status == NotificationStatus.Pending)));
    }

    [Fact]
    public async Task Parallel_run_reconciliation_compares_the_outgoing_register()
    {
        var payroll = await app.LoginAsync("payroll@demo.ecosys");
        var run = await CalculatedRunAsync(payroll, "December 2025");
        var rows = (await payroll.GetJson($"{B}/runs/{run}/employees")).AsArray();
        // Build the "legacy" register from our own figures, then introduce one PAYE error and drop one employee.
        var csv = new System.Text.StringBuilder("Payroll No,Name,Gross Pay,PAYE,Net Pay\n");
        foreach (var r in rows.Skip(1))
        {
            var paye = r!["paye"].Dec() + (r["employeeNumber"].Str() == "SL003" ? 150m : 0m);
            csv.Append($"{r["employeeNumber"].Str()},\"{r["employeeName"].Str()}\",\"{r["grossPay"].Dec():N2}\",{paye:F2},{r["netPay"].Dec():F2}\n");
        }
        var missing = rows[0]!["employeeNumber"].Str();
        var res = await payroll.PostAsync($"{B}/runs/{run}/reconcile", new StringContent(csv.ToString(), System.Text.Encoding.UTF8, "text/csv"));
        var result = await res.Json();
        Assert.Equal(1, result["different"]!.GetValue<int>());
        Assert.Equal(1, result["onlyInEcosys"]!.GetValue<int>());
        var diff = result["rows"]!.AsArray().Single(r => r!["status"].Str() == "Different")!;
        Assert.Equal("SL003", diff["employeeNumber"].Str());
        Assert.Equal(-150m, diff["differences"]![0]!["difference"].Dec());
        Assert.Equal(missing, result["rows"]!.AsArray().Single(r => r!["status"].Str() == "OnlyInEcosys")!["employeeNumber"].Str());

        var xlsx = await payroll.PostAsync($"{B}/runs/{run}/reconcile?format=xlsx", new StringContent(csv.ToString(), System.Text.Encoding.UTF8, "text/csv"));
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType!.MediaType);
        // HR without salary access can't see it.
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.LoginAsync("hr@demo.ecosys")).PostAsync($"{B}/runs/{run}/reconcile", new StringContent(csv.ToString(), System.Text.Encoding.UTF8, "text/csv"))).StatusCode);
    }

    [Fact]
    public async Task Minimum_approvals_needs_that_many_different_approvers()
    {
        var payroll = await app.LoginAsync("payroll@demo.ecosys");
        var approver = await app.LoginAsync("approver@demo.ecosys");
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var run = await CalculatedRunAsync(payroll, "September 2025");
        var le = await app.WithDbAsync(null, db => db.PayrollRuns.Where(r => r.Id == run).Select(r => r.LegalEntityId).FirstAsync());
        await SetMinimumApprovals(le, 2);
        try
        {
            await payroll.PostJson($"{B}/runs/{run}/submit");
            await (await app.LoginAsync("reviewer@demo.ecosys")).PostJson($"{B}/runs/{run}/review");
            var first = await approver.PostJson($"{B}/runs/{run}/approve", new { text = "Finance ok" });
            Assert.Equal("UnderReview", first["status"].Str());
            Assert.Equal(1, first["approvalsReceived"]!.GetValue<int>());
            Assert.Equal(2, first["approvalsRequired"]!.GetValue<int>());
            Assert.Equal(HttpStatusCode.Conflict, (await approver.PostAsJsonAsync($"{B}/runs/{run}/approve", new { })).StatusCode); // same person twice
            Assert.Equal("Approved", (await admin.PostJson($"{B}/runs/{run}/approve", new { text = "MD ok" }))["status"].Str());
        }
        finally { await SetMinimumApprovals(le, 1); }
    }

    [Fact]
    public async Task One_person_company_can_prepare_and_approve_its_own_payroll_when_configured()
    {
        // The tenant administrator stands in for a sole payroll officer: they hold prepare and approve permissions.
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var run = await CalculatedRunAsync(admin, await OwnPeriodAsync("October"));
        await admin.PostJson($"{B}/runs/{run}/submit", new { text = "Feb" });
        var le = await app.WithDbAsync(null, db => db.PayrollRuns.Where(r => r.Id == run).Select(r => r.LegalEntityId).FirstAsync());
        try
        {
            // Default workflow: blocked, and the message says where to change it.
            var blocked = await admin.PostAsJsonAsync($"{B}/runs/{run}/approve", new { });
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Contains("Approval workflow", await blocked.Content.ReadAsStringAsync());

            // Switch the company to "one person runs payroll" through the API.
            var entity = (await admin.GetJson($"{B}/organisation/legal-entities")).AsArray().First(e => e!["id"].Id() == le)!.AsObject();
            entity["settings"]!["minimumApprovals"] = 6;
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{B}/organisation/legal-entities/{le}", entity, PayrollApiFixture.Json)).StatusCode);
            entity["settings"]!["allowPreparerToApprove"] = true;
            entity["settings"]!["requireReviewBeforeApproval"] = false;
            entity["settings"]!["minimumApprovals"] = 1;
            await admin.PutJson($"{B}/organisation/legal-entities/{le}", entity);

            var policy = (await admin.GetJson($"{B}/runs/{run}"))["policy"]!;
            Assert.True(policy["allowPreparerToApprove"]!.GetValue<bool>());
            Assert.False(policy["requireReview"]!.GetValue<bool>());
            Assert.Equal("Approved", (await admin.PostJson($"{B}/runs/{run}/approve", new { text = "checked it myself" }))["status"].Str());

            // Corrections after approval: they can also approve their own adjustment.
            var line = (await admin.GetJson($"{B}/runs/{run}/employees")).AsArray()[0]!;
            var component = (await admin.GetJson($"{B}/compensation/components")).AsArray().First(c => c!["code"].Str() == "BONUS")!["id"].Id();
            var adj = await admin.PostJson($"{B}/adjustments", new { employeeId = line["employeeId"].Id(), originalRunId = run, salaryComponentId = component, type = "Arrears", amount = 1500m, reason = "Missed allowance" }, HttpStatusCode.Created);
            await admin.PostJson($"{B}/adjustments/{adj["id"].Id()}/approve");

            var actions = await app.WithDbAsync(null, db => db.PayrollAuditLogs.Where(a => a.EntityId == run || a.EntityId == adj["id"].Id()).Select(a => a.Action).ToListAsync());
            Assert.Contains("payroll.run.self_approved", actions);
            Assert.Contains("payroll.adjustment.self_approved", actions);
        }
        finally
        {
            await app.WithDbAsync(null, async db =>
            {
                var e = await db.LegalEntities.FirstAsync(x => x.Id == le);
                e.Settings.AllowPreparerToApprove = false; e.Settings.RequireReviewBeforeApproval = true; e.Settings.MinimumApprovals = 1;
                return await db.SaveChangesAsync();
            });
        }
    }

    [Fact]
    public async Task Payslip_shows_nssf_to_date_and_leave_remaining_but_not_employer_cost()
    {
        var run = await ApprovedRunAsync(await OwnPeriodAsync("November"));
        await (await app.LoginAsync("approver@demo.ecosys")).PostJson($"{B}/runs/{run}/finalise");

        var brian = await app.LoginAsync("brian.mwangi@demo.ecosys");
        var me = (await brian.GetJson($"{B}/auth/me"))["employeeId"].Id();
        var slipId = (await brian.GetJson($"{B}/employees/{me}/payslips")).AsArray().First(p => p!["periodEnd"].Str() == "2024-11-30")!["id"].Id();

        // NSSF to date: employee + employer contributions from every approved, unreversed run up to this period, plus any opening balance.
        var counted = new[] { PayrollRunStatus.Approved, PayrollRunStatus.PaymentProcessing, PayrollRunStatus.Paid, PayrollRunStatus.Finalised };
        var expected = await app.WithDbAsync(null, async db =>
        {
            var runIds = await db.PayrollRuns.Where(r => counted.Contains(r.Status)).Join(db.PayrollPeriods, r => r.PayrollPeriodId, p => p.Id, (r, p) => new { r.Id, p.PeriodEnd })
                .Where(x => x.PeriodEnd <= new DateOnly(2024, 11, 30)).Select(x => x.Id).ToListAsync();
            var nssf = await db.PayrollRunEmployees.Where(e => runIds.Contains(e.PayrollRunId) && e.EmployeeId == me)
                .SelectMany(e => e.Lines).Where(l => l.StatutoryRuleCode == "KE.NSSF").SumAsync(l => l.Amount);
            var opening = await db.EmployeeStatutoryDetails.Where(x => x.EmployeeId == me).Select(x => (decimal?)x.NssfOpeningBalance).FirstOrDefaultAsync() ?? 0m;
            return nssf + opening;
        });
        var thisRun = await app.WithDbAsync(null, db => db.PayrollRunEmployees.Where(e => e.PayrollRunId == run && e.EmployeeId == me)
            .SelectMany(e => e.Lines).Where(l => l.StatutoryRuleCode == "KE.NSSF").SumAsync(l => l.Amount));
        Assert.True(thisRun > 0 && expected >= thisRun);
        var own = await brian.GetJson($"{B}/payslips/{slipId}");
        Assert.Equal(expected, own["slip"]!["nssfContributionsToDate"].Dec());
        // (Demo data records days taken without dates, so the figure can be negative; only its presence is checked.)
        Assert.Contains(own["slip"]!["leaveBalances"]!.AsArray(), l => l!["leaveType"].Str() == "Annual leave" && l["daysRemaining"] is not null);
        // The employee's own view leaves out what they cost the employer; payroll still sees it.
        Assert.Null(own["result"]!["totalEmployerCost"]);
        Assert.NotNull((await (await app.LoginAsync("payroll@demo.ecosys")).GetJson($"{B}/payslips/{slipId}"))["result"]!["totalEmployerCost"]);

        var pdf = System.Text.Encoding.Latin1.GetString(await (await brian.GetAsync($"{B}/payslips/{slipId}/pdf")).Content.ReadAsByteArrayAsync());
        Assert.Contains("NSSF contributions to date", pdf);
        Assert.Contains("Annual leave remaining", pdf);
        Assert.DoesNotContain("Total employer cost", pdf);
    }

    private Task<int> SetMinimumApprovals(Guid legalEntityId, int n) => app.WithDbAsync(null, async db =>
    {
        var e = await db.LegalEntities.FirstAsync(x => x.Id == legalEntityId);
        e.Settings.MinimumApprovals = n;
        return await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Preparer_cannot_approve_own_payroll_even_with_approver_role()
    {
        // The tenant admin holds every permission; maker-checker must still stop self-approval.
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var run = await CalculatedRunAsync(admin, "April 2025");
        await admin.PostJson($"{B}/runs/{run}/submit");
        var res = await admin.PostAsJsonAsync($"{B}/runs/{run}/approve", new { });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("separation of duties", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rejection_returns_run_for_correction_and_notifies_preparer()
    {
        var payroll = await app.LoginAsync("payroll@demo.ecosys");
        var approver = await app.LoginAsync("approver@demo.ecosys");
        var run = await CalculatedRunAsync(payroll, "May 2025");
        await payroll.PostJson($"{B}/runs/{run}/submit");
        Assert.Equal(HttpStatusCode.BadRequest, (await approver.PostAsJsonAsync($"{B}/runs/{run}/reject", new { text = " " })).StatusCode);
        Assert.Equal("Rejected", (await approver.PostJson($"{B}/runs/{run}/reject", new { text = "Missing commissions" }))["status"].Str());
        Assert.Equal("Calculated", (await payroll.PostJson($"{B}/runs/{run}/calculate"))["status"].Str());
        Assert.True(await app.WithDbAsync(null, db => db.NotificationMessages.AnyAsync(m => m.EntityId == run && m.Template == "payroll.rejected")));
    }

    [Fact]
    public async Task Reversal_posts_contra_journal_and_reopens_period()
    {
        var run = await ApprovedRunAsync("June 2025");
        var approver = await app.LoginAsync("approver@demo.ecosys");
        await approver.PostJson($"{B}/runs/{run}/finalise"); // no bank file: cash-style finalisation
        Assert.Equal(HttpStatusCode.BadRequest, (await approver.PostAsJsonAsync($"{B}/runs/{run}/reverse", new { text = "" })).StatusCode);
        Assert.Equal("Reversed", (await approver.PostJson($"{B}/runs/{run}/reverse", new { text = "Wrong period" }))["status"].Str());

        var journals = (await (await app.LoginAsync("finance@demo.ecosys")).GetJson($"{B}/runs/{run}/journal")).AsArray();
        Assert.Equal(2, journals.Count);
        Assert.Equal("Reversed", journals[0]!["status"].Str());
        Assert.Equal(journals[0]!["totalDebit"].Dec(), journals[1]!["totalCredit"].Dec());

        // Period is open again, so a corrected regular run can be created.
        var payroll = await app.LoginAsync("payroll@demo.ecosys");
        var (le, group, period) = await PeriodAsync(payroll, "June 2025");
        await payroll.PostJson($"{B}/runs", new { legalEntityId = le, payrollGroupId = group, payrollPeriodId = period }, HttpStatusCode.Created);
    }

    [Fact]
    public async Task Attendance_import_drives_overtime_and_absence()
    {
        var payroll = await app.LoginAsync("payroll@demo.ecosys");
        var (le, group, period) = await PeriodAsync(payroll, "July 2025");
        var run = (await payroll.PostJson($"{B}/runs", new { legalEntityId = le, payrollGroupId = group, payrollPeriodId = period }, HttpStatusCode.Created))["id"].Id();
        await payroll.PostJson($"{B}/runs/{run}/calculate");
        var before = (await payroll.GetJson($"{B}/runs/{run}/employees")).AsArray();
        decimal Gross(JsonArray rows, string no) => rows.First(r => r!["employeeNumber"].Str() == no)!["grossPay"].Dec();

        var csv = "EmployeeNumber,DaysWorked,AbsentDays,OvertimeHours,RestDayOvertimeHours\nSL010,22,0,10,4\nSL013,20,2,0,0\n";
        var res = await payroll.PostAsync($"{B}/runs/{run}/attendance/import", new StringContent(csv, Encoding.UTF8, "text/csv"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await payroll.PostAsync($"{B}/runs/{run}/attendance/import", new StringContent("EmployeeNumber,OvertimeHours\nNOPE,1\n", Encoding.UTF8, "text/csv"))).StatusCode);

        Assert.Equal("Calculated", (await payroll.PostJson($"{B}/runs/{run}/calculate"))["status"].Str());
        var after = (await payroll.GetJson($"{B}/runs/{run}/employees")).AsArray();
        // SL010 basic 42,000 → hourly 42,000/195 = 215.3846; 10h x 1.5 = 3,230.77; 4h x 2.0 = 1,723.08
        Assert.Equal(3_230.77m + 1_723.08m, Gross(after, "SL010") - Gross(before, "SL010"));
        // SL013 basic 35,000 → daily 35,000/22 = 1,590.909…; 2 days = 3,181.82
        Assert.Equal(-3_181.82m, Gross(after, "SL013") - Gross(before, "SL013"));
    }

    [Fact]
    public async Task Statutory_rule_versions_need_independent_approval()
    {
        var payroll = await app.LoginAsync("payroll@demo.ecosys");   // statutory.manage
        var approver = await app.LoginAsync("approver@demo.ecosys"); // statutory.approve
        var draft = new
        {
            ruleCode = "KE.TEST.LEVY", jurisdiction = "KE", authority = "TEST", name = "Test levy", method = "FixedAmount", @base = "GrossPay",
            effectiveFrom = "2025-01-01", employeeRate = 0, employerRate = 0, parameters = new { employerFixedAmount = 10 },
        };
        var v1 = await payroll.PostJson($"{B}/statutory-rules", draft, HttpStatusCode.Created);
        await payroll.PostJson($"{B}/statutory-rules/{v1["id"].Id()}/submit");
        await approver.PostJson($"{B}/statutory-rules/{v1["id"].Id()}/approve", new { note = "verified" });

        var v2 = await payroll.PostJson($"{B}/statutory-rules", draft with { }, HttpStatusCode.Created);
        var v2Id = v2["id"].Id();
        // Drafts are created "effective from" the payload date; shift v2 to July directly (the API has no in-place edit by design).
        await app.WithDbAsync(null, async db => { var r = await db.StatutoryRuleVersions.FirstAsync(x => x.Id == v2Id); r.EffectiveFrom = new DateOnly(2025, 7, 1); return await db.SaveChangesAsync(); });
        await payroll.PostJson($"{B}/statutory-rules/{v2Id}/submit");
        await approver.PostJson($"{B}/statutory-rules/{v2Id}/approve");

        var versions = (await payroll.GetJson($"{B}/statutory-rules?ruleCode=KE.TEST.LEVY")).AsArray();
        Assert.Equal("2025-06-30", versions.First(v => v!["version"]!.GetValue<int>() == 1)!["effectiveTo"].Str());
        var effective = (await payroll.GetJson($"{B}/statutory-rules/effective?jurisdiction=KE&asOf=2025-08-01")).AsArray();
        Assert.Equal(2, effective.First(r => r!["ruleCode"].Str() == "KE.TEST.LEVY")!["version"]!.GetValue<int>());
    }

    [Fact]
    public async Task Rule_author_cannot_approve_own_rule()
    {
        var admin = await app.LoginAsync("admin@demo.ecosys"); // both manage and approve
        var v = await admin.PostJson($"{B}/statutory-rules", new { ruleCode = "KE.TEST.SELF", jurisdiction = "KE", authority = "T", name = "Self", method = "FixedAmount", @base = "GrossPay", effectiveFrom = "2025-01-01", employeeRate = 0, employerRate = 0, parameters = new { } }, HttpStatusCode.Created);
        await admin.PostJson($"{B}/statutory-rules/{v["id"].Id()}/submit");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"{B}/statutory-rules/{v["id"].Id()}/approve", new { })).StatusCode);
    }

    // ------------------------------------------------------------------ Reports & exports

    [Fact]
    public async Task Reports_export_as_csv_xlsx_and_pdf_and_annual_totals_match_runs()
    {
        var run = await ApprovedRunAsync("August 2025");
        var approver = await app.LoginAsync("approver@demo.ecosys");
        await approver.PostJson($"{B}/runs/{run}/finalise");
        var finance = await app.LoginAsync("finance@demo.ecosys");

        var csv = await finance.GetAsync($"{B}/reports/payroll-register?runId={run}&format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);

        var xlsx = await finance.GetAsync($"{B}/reports/statutory?runId={run}&ruleCode=KE.PAYE&format=xlsx");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        using (var zip = new ZipArchive(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync())))
        {
            var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
            Assert.Contains("PAYE return", sheet);
            Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        }

        var pdf = await finance.GetAsync($"{B}/reports/variance?runId={run}&format=pdf");
        Assert.StartsWith("%PDF", Encoding.Latin1.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));

        var le = (await finance.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        var annual = await finance.GetJson($"{B}/reports/annual?legalEntityId={le}&year=2025");
        var runPaye = (await finance.GetJson($"{B}/runs/{run}"))["run"]!["totalPaye"].Dec();
        var augustPaye = annual["employees"]!.AsArray().Sum(e => e!["months"]![7]!["paye"].Dec());
        Assert.Equal(runPaye, augustPaye);
        var p9 = await finance.GetAsync($"{B}/reports/annual?legalEntityId={le}&year=2025&format=pdf");
        Assert.Equal("application/pdf", p9.Content.Headers.ContentType!.MediaType);
    }

    // ------------------------------------------------------------------ Periods & payments

    [Fact]
    public async Task Weekly_group_generates_weekly_periods_and_scaled_statutory_amounts()
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var le = (await admin.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        var group = await admin.PostJson($"{B}/organisation/payroll-groups", new { legalEntityId = le, code = "WK", name = "Weekly casuals", frequency = "Weekly", payDay = 5, isActive = true }, HttpStatusCode.Created);
        var gid = group["id"].Id();
        var created = await admin.PostJson($"{B}/organisation/payroll-groups/{gid}/periods/generate", new { year = 2025 });
        Assert.InRange(created["created"]!.GetValue<int>(), 52, 53);
        var periods = (await admin.GetJson($"{B}/organisation/payroll-groups/{gid}/periods")).AsArray();
        Assert.All(periods, p => Assert.Equal(6, DateOnly.Parse(p!["periodEnd"].Str()).DayNumber - DateOnly.Parse(p["periodStart"].Str()).DayNumber));
    }

    [Fact]
    public void Net_pay_is_split_across_accounts_without_losing_a_cent()
    {
        var primary = new Domain.Employees.EmployeeBankAccount { AccountName = "P", AccountNumber = "1", IsPrimary = true, IsVerified = true };
        var sacco = new Domain.Employees.EmployeeBankAccount { AccountName = "S", AccountNumber = "2", IsPrimary = false, IsVerified = true, SplitPercent = 33.33m };
        var parts = PayrollRunService.SplitNetPay(10_000.01m, [primary, sacco]);
        Assert.Equal(10_000.01m, parts.Sum(p => p.Amount));
        Assert.Equal(3_333.00m, parts.Single(p => p.Account == sacco).Amount);
        Assert.Same(primary, parts[0].Account);
    }
}

[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class PlatformRuleUpgradeTests(PayrollApiFixture app)
{
    [Fact]
    public async Task Startup_seed_closes_superseded_platform_version_on_existing_database()
    {
        // Simulate a database seeded before NSSF year 4 existed: v3 open-ended, v4 absent.
        var v4 = Ecosys.Payroll.Engine.Jurisdictions.Kenya.KenyaStatutoryRules.All().Single(r => r.RuleCode == "KE.NSSF" && r.Version == 4).Id;
        var v3 = Ecosys.Payroll.Engine.Jurisdictions.Kenya.KenyaStatutoryRules.All().Single(r => r.RuleCode == "KE.NSSF" && r.Version == 3).Id;
        await app.WithDbAsync(null, async db =>
        {
            db.StatutoryRuleVersions.Remove(await db.StatutoryRuleVersions.FirstAsync(r => r.Id == v4));
            await db.SaveChangesAsync();
            await db.StatutoryRuleVersions.IgnoreQueryFilters().Where(r => r.Id == v4).ExecuteDeleteAsync();
            return await db.StatutoryRuleVersions.Where(r => r.Id == v3).ExecuteUpdateAsync(s => s.SetProperty(r => r.EffectiveTo, (DateOnly?)null));
        });

        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Ecosys.Payroll.Infrastructure.Seed.DemoDataSeeder>().SeedPlatformRulesAsync();

        var (to, present) = await app.WithDbAsync(null, async db => (
            (await db.StatutoryRuleVersions.FirstAsync(r => r.Id == v3)).EffectiveTo,
            await db.StatutoryRuleVersions.AnyAsync(r => r.Id == v4)));
        Assert.Equal(new DateOnly(2026, 1, 31), to);
        Assert.True(present);
    }
}
