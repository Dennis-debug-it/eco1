using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Ecosys.Payroll.Domain.Integration;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Seed;
using Ecosys.Payroll.Infrastructure.Services.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>The published API: keys for programs, /api/v1, safe retries, the events feed and signed webhooks.</summary>
[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class PublicApiTests(PayrollApiFixture app)
{
    private const string B = "/api/payroll";
    private const string V1 = "/api/v1";
    private static int _n;

    private sealed record Co(HttpClient Admin, Guid TenantId, Guid Le, Guid Group);

    /// <summary>A fresh organisation with one company and one payroll group, so events and webhooks are its own.</summary>
    private async Task<Co> NewCompanyAsync()
    {
        var n = Interlocked.Increment(ref _n);
        TenantProvisionResult r;
        using (var scope = app.Services.CreateScope())
            r = await scope.ServiceProvider.GetRequiredService<TenantProvisioner>().ProvisionAsync(
                new($"Api Co {n}", $"api-{n}-{Guid.NewGuid():N}"[..16].TrimEnd('-'), $"Api Co {n} Ltd", $"P05{Random.Shared.Next(1000000, 9999999)}{(char)('C' + n % 10)}", $"admin{n}@api.example", "Baraka Admin"));
        await app.WithDbAsync(r.TenantId, async db =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == r.AdminUserId);
            u.PasswordHash = PasswordHasher.Hash(PayrollApiFixture.Password); u.MustChangePassword = false;
            return await db.SaveChangesAsync();
        });
        var admin = await app.LoginAsync(r.AdminEmail, r.Slug);
        var group = await app.WithDbAsync(r.TenantId, db => db.PayrollGroups.Select(g => g.Id).FirstAsync());
        return new Co(admin, r.TenantId, r.LegalEntityId, group);
    }

    private async Task<(HttpClient Client, Guid Id, string Key)> KeyAsync(HttpClient admin, string name, string[] permissions, int? rpm = null)
    {
        var res = await admin.PostJson($"{B}/integrations/keys", new { name, permissions, requestsPerMinute = rpm }, HttpStatusCode.Created);
        var key = res["key"].Str();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return (client, res["client"]!["id"].Id(), key);
    }

    private static async Task<string> Code(HttpResponseMessage res) => JsonNode.Parse(await res.Content.ReadAsStringAsync())?["code"]?.GetValue<string>() ?? $"HTTP {(int)res.StatusCode}";

    [Fact]
    public async Task A_key_is_shown_once_stored_as_a_hash_holds_only_its_permissions_and_can_be_revoked()
    {
        var co = await NewCompanyAsync();
        var (api, id, key) = await KeyAsync(co.Admin, "Biometric clock", ["employees.read"]);
        Assert.StartsWith("eco_live_", key);
        var stored = await app.WithDbAsync(co.TenantId, db => db.ApiClients.SingleAsync(c => c.Id == id));
        Assert.Equal(ApiKeyService.Hash(key), stored.KeyHash);
        Assert.DoesNotContain(key, await (await co.Admin.GetAsync($"{B}/integrations/keys")).Content.ReadAsStringAsync());

        var me = await api.GetJson($"{V1}/me");
        Assert.Equal("api_key", me["kind"].Str());
        Assert.Equal("API: Biometric clock", me["name"].Str());
        Assert.Equal(["employees.read"], me["permissions"]!.AsArray().Select(p => p.Str()));

        // Reads are allowed; writes, pay figures and the screens' own endpoints are not.
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync($"{V1}/employees")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsJsonAsync($"{V1}/employees", new { companyId = co.Le, employeeNumber = "X1", firstName = "A", lastName = "B", hireDate = "2025-01-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync($"{V1}/payroll-runs")).StatusCode);
        var screens = await api.GetAsync($"{B}/employees");
        Assert.Equal(HttpStatusCode.Forbidden, screens.StatusCode);
        Assert.Equal("API_KEY_SCOPE", await Code(screens));

        // A key can't approve payroll, and HR administrators can't make keys at all.
        var approve = await co.Admin.PostAsJsonAsync($"{B}/integrations/keys", new { name = "Approver bot", permissions = new[] { "payroll.approve" } });
        Assert.Equal(HttpStatusCode.BadRequest, approve.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.LoginAsync("hr@demo.ecosys")).PostAsJsonAsync($"{B}/integrations/keys", new { name = "x", permissions = new[] { "employees.read" } })).StatusCode);

        await co.Admin.PostJson($"{B}/integrations/keys/{id}/revoke");
        var after = await api.GetAsync($"{V1}/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal("INVALID_API_KEY", await Code(after));
        var actions = await app.WithDbAsync(co.TenantId, db => db.PayrollAuditLogs.Select(a => a.Action).ToListAsync());
        Assert.Contains("integration.key.created", actions);
        Assert.Contains("integration.key.revoked", actions);

        // Keys never reach another organisation's records.
        var demo = await app.LoginAsync("admin@demo.ecosys");
        var (demoKey, _, _) = await KeyAsync(demo, $"Demo reader {Guid.NewGuid():N}", ["employees.read"]);
        var page = await demoKey.GetJson($"{V1}/employees?limit=500");
        Assert.DoesNotContain(page["items"]!.AsArray(), e => e!["id"].Id() == app.RivalEmployeeId);
        Assert.Equal(HttpStatusCode.NotFound, (await demoKey.GetAsync($"{V1}/employees/{app.RivalEmployeeId}")).StatusCode);
    }

    [Fact]
    public async Task A_key_adds_and_changes_employees_retries_safely_and_feeds_attendance_and_pay_items_into_a_run()
    {
        var co = await NewCompanyAsync();
        var (api, _, _) = await KeyAsync(co.Admin, "HR system", ["employees.write", "payroll.prepare", "salary.read"]);

        var body = new { companyId = co.Le, employeeNumber = "API-1", firstName = "Nekesa", lastName = "Wafula", hireDate = "2025-02-01", nationalId = "12345678", kraPin = "A400000001B", jobTitle = "Storekeeper" };
        HttpRequestMessage Post(object b, string idem) => new(HttpMethod.Post, $"{V1}/employees") { Content = JsonContent.Create(b, options: PayrollApiFixture.Json), Headers = { { "Idempotency-Key", idem } } };
        var first = await api.SendAsync(Post(body, "create-API-1"));
        var created = await first.Json(HttpStatusCode.Created);
        var id = created["id"].Id();
        Assert.Equal("A400000001B", created["kraPin"].Str());
        Assert.Equal(co.Group, created["payrollGroupId"].Id()); // the company's only payroll group

        // The retry gets the same answer, marked as a replay; nothing is added twice.
        var retry = await api.SendAsync(Post(body, "create-API-1"));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(id, (await retry.Json(HttpStatusCode.Created))["id"].Id());
        Assert.Equal(1, await app.WithDbAsync(co.TenantId, db => db.Employees.CountAsync(e => e.EmployeeNumber == "API-1")));
        var reused = await api.SendAsync(Post(body with { employeeNumber = "API-2" }, "create-API-1"));
        Assert.Equal((HttpStatusCode)422, reused.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", await Code(reused));
        Assert.Equal("DUPLICATE", await Code(await api.PostAsJsonAsync($"{V1}/employees", body, PayrollApiFixture.Json)));

        // A change sends only what changes; the rest is kept.
        var patched = await (await api.PatchAsJsonAsync($"{V1}/employees/{id}", new { jobTitle = "Stores supervisor", nssfNumber = "2001234567" }, PayrollApiFixture.Json)).Json();
        Assert.Equal(("Stores supervisor", "2001234567", "12345678", "A400000001B"), (patched["jobTitle"].Str(), patched["nssfNumber"].Str(), patched["nationalId"].Str(), patched["kraPin"].Str()));
        Assert.Equal("VALIDATION", await Code(await api.PatchAsJsonAsync($"{V1}/employees/{id}", new { kraPin = "not-a-pin" }, PayrollApiFixture.Json)));
        var audit = await app.WithDbAsync(co.TenantId, db => db.PayrollAuditLogs.Where(a => a.Action == "employee.created" && a.EntityId == id).SingleAsync());
        Assert.Equal("API: HR system", audit.ActorName);

        // Attendance and one-off pay items go into the open run, named by employee number and component code.
        await co.Admin.PostJson($"{B}/employees/{id}/salary", new { basicSalary = 52000, effectiveFrom = "2025-02-01" }, HttpStatusCode.Created);
        var period = await app.WithDbAsync(co.TenantId, db => db.PayrollPeriods.OrderBy(p => p.PeriodStart).Select(p => p.Id).FirstAsync());
        var run = (await co.Admin.PostJson($"{B}/runs", new { legalEntityId = co.Le, payrollGroupId = co.Group, payrollPeriodId = period }, HttpStatusCode.Created))["id"].Id();
        var runs = await api.GetJson($"{V1}/payroll-runs?status=Draft");
        Assert.Contains(runs["items"]!.AsArray(), r => r!["id"].Id() == run);
        await (await api.PutAsJsonAsync($"{V1}/payroll-runs/{run}/attendance", new[] { new { employeeNumber = "API-1", daysWorked = 20m, absentDays = 1m, overtimeHours = 6m, restDayOvertimeHours = 0m, note = "clock" } }, PayrollApiFixture.Json)).Json();
        var code = await app.WithDbAsync(co.TenantId, db => db.SalaryComponents.Where(c => c.Type == Domain.Compensation.ComponentType.Earning && !c.IsBasic).Select(c => c.Code).FirstAsync());
        await (await api.PostAsJsonAsync($"{V1}/payroll-runs/{run}/variable-inputs", new[] { new { employeeNumber = "API-1", componentCode = code.ToLowerInvariant(), amount = 1500m, note = "Bonus" } }, PayrollApiFixture.Json)).Json();
        Assert.Equal("VALIDATION", await Code(await api.PostAsJsonAsync($"{V1}/payroll-runs/{run}/variable-inputs", new[] { new { employeeNumber = "NOPE", componentCode = code, amount = 1m } }, PayrollApiFixture.Json)));
        var att = await app.WithDbAsync(co.TenantId, db => db.AttendanceRecords.SingleAsync(a => a.PayrollRunId == run));
        Assert.Equal((20m, 6m, "api"), (att.DaysWorked, att.OvertimeHours, att.Source));

        await co.Admin.PostJson($"{B}/runs/{run}/calculate");
        var results = await api.GetJson($"{V1}/payroll-runs/{run}/results");
        Assert.True(results[0]!["grossPay"].Dec() > 52000m);

        // The events feed lists what happened, oldest first, with a cursor.
        var events = await api.GetJson($"{V1}/events?limit=500");
        var mine = events["items"]!.AsArray().Where(e => e!["data"]!["id"]?.GetValue<string>() == id.ToString()).ToList();
        Assert.Equal("employee.created", mine[0]!["type"].Str());
        Assert.Equal($"https://payroll.test/api/v1/employees/{id}", mine[0]!["data"]!["url"].Str());
        Assert.Contains(mine, e => e!["type"].Str() == "employee.updated");
        var next = events["next"]!.GetValue<long>();
        Assert.Empty((await api.GetJson($"{V1}/events?after={next}"))["items"]!.AsArray());
    }

    [Fact]
    public async Task Webhooks_are_signed_name_records_not_pay_are_retried_and_switch_off_after_repeated_failures()
    {
        var co = await NewCompanyAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var ok = $"https://hooks-{tag}.example.com/ecosys";
        var down = $"https://down-{tag}.example.com/ecosys";
        app.Webhooks.Answers[down] = 503;

        foreach (var bad in new[] { "http://hooks.example.com/x", "https://127.0.0.1/x", "https://10.1.2.3/x", "https://169.254.169.254/latest", "https://localhost/x", "https://user:pw@hooks.example.com/" })
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync($"{B}/integrations/webhooks", new { name = "bad", url = bad })).StatusCode);
        var hook = await co.Admin.PostJson($"{B}/integrations/webhooks", new { name = "HR sync", url = ok, events = new[] { "employee.created", "employee.updated" } }, HttpStatusCode.Created);
        var secret = hook["secret"].Str();
        Assert.StartsWith("whsec_", secret);
        var hookId = hook["webhook"]!["id"].Id();
        var downHook = (await co.Admin.PostJson($"{B}/integrations/webhooks", new { name = "Flaky", url = down, events = new[] { "employee.created" } }, HttpStatusCode.Created))["webhook"]!["id"].Id();
        var atRest = await app.WithDbAsync(null, db => db.Database.SqlQueryRaw<string>("select secret as \"Value\" from webhook_endpoints where id = {0}", hookId).SingleAsync());
        Assert.NotEqual(secret, atRest); // encrypted in the database

        var emp = await co.Admin.PostJson($"{B}/employees", new { legalEntityId = co.Le, employeeNumber = "W1", firstName = "Kip", lastName = "Rotich", hireDate = "2025-01-06", payrollGroupId = co.Group }, HttpStatusCode.Created);
        var dispatcher = app.Services.GetRequiredService<WebhookDispatcher>();
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        var sent = Assert.Single(app.Webhooks.To(ok));
        Assert.True(WebhookSignature.Verify(secret, sent.Body, sent.Headers[WebhookSignature.Header], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(secret, sent.Body.Replace("employee.created", "employee.updated"), sent.Headers[WebhookSignature.Header], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(secret, sent.Body, sent.Headers[WebhookSignature.Header], DateTimeOffset.UtcNow.AddMinutes(10), TimeSpan.FromMinutes(5)));
        var payload = JsonNode.Parse(sent.Body)!;
        Assert.Equal(("employee.created", emp["id"].Str(), "employee"), (payload["type"].Str(), payload["data"]!["id"].Str(), payload["data"]!["object"].Str()));
        Assert.Equal("employee.created", sent.Headers["Ecosys-Event"]);
        Assert.DoesNotContain("Rotich", sent.Body); // ids and links only

        // The failing receiver is retried later, not straight away.
        var pending = await app.WithDbAsync(co.TenantId, db => db.WebhookDeliveries.SingleAsync(d => d.WebhookEndpointId == downHook));
        Assert.Equal((WebhookDeliveryStatus.Pending, 1, 503), (pending.Status, pending.Attempts, pending.LastStatusCode!.Value));
        Assert.True(pending.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(20));
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.Single(app.Webhooks.To(down));

        // After the last retry of the fifth delivery in a row, the endpoint is switched off and administrators are told.
        await app.WithDbAsync(co.TenantId, async db =>
        {
            var d = await db.WebhookDeliveries.SingleAsync(x => x.WebhookEndpointId == downHook);
            d.Attempts = WebhookDispatcher.MaxAttempts - 1; d.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            (await db.WebhookEndpoints.SingleAsync(w => w.Id == downHook)).ConsecutiveFailures = WebhookDispatcher.DisableAfter - 1;
            return await db.SaveChangesAsync();
        });
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        var off = await app.WithDbAsync(co.TenantId, db => db.WebhookEndpoints.SingleAsync(w => w.Id == downHook));
        Assert.False(off.IsActive);
        Assert.Contains("Switched off automatically", off.DisabledReason);
        Assert.True(await app.WithDbAsync(co.TenantId, db => db.NotificationMessages.AnyAsync(m => m.Template == "integration.webhook.disabled" && m.EntityId == downHook)));
        var failed = await app.WithDbAsync(co.TenantId, db => db.WebhookDeliveries.SingleAsync(x => x.WebhookEndpointId == downHook));
        Assert.Equal(WebhookDeliveryStatus.Failed, failed.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync($"{B}/integrations/deliveries/{failed.Id}/redeliver", new { })).StatusCode);

        // A test event, and sending a past delivery again, both go out with the same signing.
        await co.Admin.PostJson($"{B}/integrations/webhooks/{hookId}/test");
        var deliveries = await co.Admin.GetJson($"{B}/integrations/webhooks/{hookId}/deliveries");
        await co.Admin.PostJson($"{B}/integrations/deliveries/{deliveries.AsArray().Last()!["id"].Id()}/redeliver");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        var all = app.Webhooks.To(ok);
        Assert.Equal(3, all.Count);
        Assert.Contains(all, s => s.Headers["Ecosys-Event"] == "ping");
        Assert.Equal(2, all.Count(s => JsonNode.Parse(s.Body)!["id"].Str() == payload["id"].Str())); // same event id, so receivers can ignore the repeat
    }

    [Fact]
    public async Task Each_key_has_its_own_request_limit()
    {
        var co = await NewCompanyAsync();
        var (api, _, _) = await KeyAsync(co.Admin, "Chatty", ["employees.read"], rpm: 10);
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.OK, (await api.GetAsync($"{V1}/me")).StatusCode);
        var limited = await api.GetAsync($"{V1}/me");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null);
        var (other, _, _) = await KeyAsync(co.Admin, "Quiet", ["employees.read"], rpm: 10);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"{V1}/me")).StatusCode);
    }

    [Fact]
    public async Task The_published_description_covers_v1_only_and_needs_no_sign_in()
    {
        var doc = await app.CreateClient().GetJson($"{V1}/openapi.json");
        var paths = doc["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/api/v1/employees", paths);
        Assert.Contains("/api/v1/payroll-runs/{id}/attendance", paths);
        Assert.Contains("/api/v1/events", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("/api/payroll"));
        Assert.Equal("bearer", doc["components"]!["securitySchemes"]!["apiKey"]!["scheme"].Str());
        Assert.Equal("Ecosys Payroll API", doc["info"]!["title"].Str());
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.8", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("41.90.64.10", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Webhooks_never_go_to_internal_addresses(string ip, bool blocked) =>
        Assert.Equal(blocked, WebhookAddress.IsBlocked(IPAddress.Parse(ip)));
}
