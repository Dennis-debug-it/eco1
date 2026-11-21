using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Domain.Billing;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Seed;
using Ecosys.Payroll.Infrastructure.Services.Saas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>Separate app instance in SaaS mode: public sign-up, plans, billing and the operator console.</summary>
public sealed class SaasFixture : PayrollApiFixture
{
    protected override IReadOnlyDictionary<string, string> ExtraSettings => new Dictionary<string, string>
    {
        ["Saas:Enabled"] = "true",
        ["Saas:BillingJobEnabled"] = "false", // tests drive the billing cycle with chosen dates
        ["Saas:SignupAttemptsPerHour"] = "100000",
        ["Payroll:PortalUrl"] = "https://payroll.test",
    };
}

[Trait("Category", "Integration")]
public partial class SaasTests(SaasFixture app) : IClassFixture<SaasFixture>
{
    private const string B = "/api/payroll";
    private const string P = "/api/platform";
    private const string StrongPassword = "Blue-heron-4417-river";
    private static int _pin = 2000000;

    private static string NextPin() => $"P05{Interlocked.Increment(ref _pin):0000000}Q";

    /// <summary>Signs up, confirms the email from the outbox and signs in.</summary>
    private async Task<(HttpClient Client, string Slug, Guid TenantId)> SignUpAsync(string name, string plan = "growth")
    {
        var anon = app.CreateClient();
        var email = $"owner@{name.ToLowerInvariant().Replace(' ', '-')}.example";
        var res = await anon.PostJson($"{B}/signup", new { organisationName = name, kraPin = NextPin(), adminName = "Wanjiru Kamau", adminEmail = email, password = StrongPassword, planCode = plan, acceptTerms = true }, HttpStatusCode.Created);
        var slug = res["slug"].Str();
        Assert.True(res["verificationRequired"]!.GetValue<bool>());

        var tenantId = await app.WithDbAsync(null, db => db.Tenants.Where(t => t.Slug == slug).Select(t => t.Id).FirstAsync());
        await anon.PostJson($"{B}/signup/verify", new { token = await TokenAsync(tenantId) });
        return (await LoginAsync(slug, email, StrongPassword), slug, tenantId);
    }

    private async Task<string> TokenAsync(Guid tenantId)
    {
        var body = await app.WithDbAsync(null, db => db.NotificationMessages.Where(m => m.TenantId == tenantId && m.Template == "signup.verify").OrderByDescending(m => m.CreatedAt).Select(m => m.Body).FirstAsync());
        return TokenRx().Match(body).Groups[1].Value;
    }

    private async Task<HttpClient> LoginAsync(string slug, string email, string password)
    {
        var client = app.CreateClient();
        var body = await client.PostJson($"{B}/auth/login", new { tenant = slug, email, password });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body["token"].Str());
        return client;
    }

    private async Task<HttpClient> OperatorAsync()
    {
        var email = $"ops{Guid.NewGuid():N}"[..12] + "@operator.example";
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PlatformAdminProvisioner>().CreateAsync(email, "Ops Person");
        await app.WithDbAsync(null, async db =>
        {
            var u = await db.Users.FirstAsync(x => x.Email == email);
            u.PasswordHash = PasswordHasher.Hash(StrongPassword); u.MustChangePassword = false;
            return await db.SaveChangesAsync();
        });
        return await LoginAsync(PlatformAdminProvisioner.PlatformSlug, email, StrongPassword);
    }

    private async Task<BillingCycleResult> RunBillingAsync(DateOnly today, params Guid[] tenantIds)
    {
        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TenantContext>();
        ctx.UserName = "billing-test"; ctx.IsPlatformAdministrator = true;
        var result = await scope.ServiceProvider.GetRequiredService<BillingService>().RunCycleAsync(today);
        foreach (var id in tenantIds) app.Services.GetRequiredService<EntitlementCache>().Invalidate(id);
        return result;
    }

    private Task<Subscription> SubscriptionAsync(Guid tenantId) => app.WithDbAsync(tenantId, db => db.Subscriptions.AsNoTracking().FirstAsync());

    private static async Task<HttpResponseMessage> AddEmployeeAsync(HttpClient c, string number)
    {
        var le = (await c.GetJson($"{B}/organisation/legal-entities")).AsArray()[0]!["id"].Id();
        var group = (await c.GetJson($"{B}/organisation/payroll-groups")).AsArray()[0]!["id"].Id();
        return await c.PostAsJsonAsync($"{B}/employees", new { legalEntityId = le, employeeNumber = number, firstName = "Test", lastName = number, hireDate = "2026-01-05", payrollGroupId = group, gender = "Unspecified", employmentType = "Permanent", status = "Active" }, PayrollApiFixture.Json);
    }

    private static async Task<string> Code(HttpResponseMessage res) => (JsonNode.Parse(await res.Content.ReadAsStringAsync())?["code"]?.GetValue<string>()) ?? $"HTTP {(int)res.StatusCode}";

    [Fact]
    public async Task Signup_confirms_email_then_starts_a_trial()
    {
        var anon = app.CreateClient();
        var options = await anon.GetJson($"{B}/signup/options");
        Assert.True(options["enabled"]!.GetValue<bool>());
        var codes = options["plans"]!.AsArray().Select(p => p!["code"].Str()).ToList();
        Assert.Contains("starter", codes); Assert.Contains("growth", codes);
        Assert.DoesNotContain("enterprise", codes); // private plans are assigned by the operator

        Assert.False((await anon.GetJson($"{B}/signup/slug?value=platform"))["available"]!.GetValue<bool>());
        var suggestion = await anon.GetJson($"{B}/signup/slug?name=Nyeri%20Coffee%20Co.");
        Assert.Equal("nyeri-coffee-co", suggestion["slug"].Str());

        var pin = NextPin();
        var created = await anon.PostJson($"{B}/signup", new { organisationName = "Nyeri Coffee Co.", kraPin = pin, adminName = "Achieng Otieno", adminEmail = "Achieng@NyeriCoffee.example", password = StrongPassword, planCode = "growth", acceptTerms = true }, HttpStatusCode.Created);
        Assert.Equal("nyeri-coffee-co", created["slug"].Str());

        // Cannot sign in until the email is confirmed; the right password is needed to learn that.
        var wrong = await anon.PostAsJsonAsync($"{B}/auth/login", new { tenant = "nyeri-coffee-co", email = "achieng@nyericoffee.example", password = "Not-the-password-9" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var early = await anon.PostAsJsonAsync($"{B}/auth/login", new { tenant = "nyeri-coffee-co", email = "achieng@nyericoffee.example", password = StrongPassword });
        Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
        Assert.Equal("EMAIL_NOT_VERIFIED", await Code(early));

        var tenantId = await app.WithDbAsync(null, db => db.Tenants.Where(t => t.Slug == "nyeri-coffee-co").Select(t => t.Id).FirstAsync());
        var token = await TokenAsync(tenantId);
        Assert.Equal("nyeri-coffee-co", (await anon.PostJson($"{B}/signup/verify", new { token }))["slug"].Str());
        Assert.Equal("VERIFICATION_INVALID", await Code(await anon.PostAsJsonAsync($"{B}/signup/verify", new { token }))); // single use
        // The token is stored hashed only.
        Assert.False(await app.WithDbAsync(null, db => db.Users.AnyAsync(u => u.EmailVerificationTokenHash == token)));

        var admin = await LoginAsync("nyeri-coffee-co", "achieng@nyericoffee.example", StrongPassword);
        var me = await admin.GetJson($"{B}/auth/me");
        Assert.False(me["mustChangePassword"]!.GetValue<bool>()); // they chose their own password
        Assert.Contains("billing.manage", me["permissions"]!.AsArray().Select(p => p!.GetValue<string>()));
        var status = await admin.GetJson($"{B}/billing/status");
        Assert.Equal("Trialing", status["status"].Str());
        Assert.Equal(14, status["trialDaysLeft"]!.GetValue<int>());
        Assert.Contains(PlanFeatures.HrSuite, status["features"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal(200, (int)(await admin.GetAsync($"{B}/performance/cycles")).StatusCode);

        // One account per employer PIN, reserved codes refused.
        Assert.Equal("DUPLICATE", await Code(await anon.PostAsJsonAsync($"{B}/signup", new { organisationName = "Nyeri Again", kraPin = pin, adminName = "Someone Else", adminEmail = "x@again.example", password = StrongPassword, acceptTerms = true })));
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync($"{B}/signup", new { organisationName = "Platform", slug = "platform", kraPin = NextPin(), adminName = "Someone Else", adminEmail = "x@p.example", password = StrongPassword, acceptTerms = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync($"{B}/signup", new { organisationName = "No Terms Ltd", kraPin = NextPin(), adminName = "Someone Else", adminEmail = "x@n.example", password = StrongPassword, acceptTerms = false })).StatusCode);

        // Resend never reveals whether an account exists.
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync($"{B}/signup/resend", new { organisation = "nobody-here", email = "ghost@x.example" })).StatusCode);
    }

    [Fact]
    public async Task Plan_limits_and_features_are_enforced()
    {
        var ops = await OperatorAsync();
        await ops.PostJson($"{P}/plans", new { code = "tiny", name = "Tiny", monthlyBaseFee = 500m, includedEmployees = 1, pricePerAdditionalEmployee = 0m, maxEmployees = 1, maxLegalEntities = 1, features = Array.Empty<string>(), isPublic = false, isActive = true, sortOrder = 9 });
        var (admin, _, tenantId) = await SignUpAsync("Limit Test Ltd");
        await ops.PostJson($"{P}/tenants/{tenantId}/plan", new { planCode = "tiny" });

        Assert.Equal(HttpStatusCode.Created, (await AddEmployeeAsync(admin, "E001")).StatusCode);
        var second = await AddEmployeeAsync(admin, "E002");
        Assert.Equal(HttpStatusCode.PaymentRequired, second.StatusCode);
        Assert.Equal("PLAN_LIMIT", await Code(second));

        var entity = await admin.PostAsJsonAsync($"{B}/organisation/legal-entities", new { name = "Second Co", registrationNumber = "PVT-2", taxPin = NextPin() }, PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.PaymentRequired, entity.StatusCode);

        var hr = await admin.GetAsync($"{B}/performance/cycles");
        Assert.Equal(HttpStatusCode.PaymentRequired, hr.StatusCode);
        Assert.Equal("FEATURE_NOT_IN_PLAN", await Code(hr));
        Assert.DoesNotContain(PlanFeatures.HrSuite, (await admin.GetJson($"{B}/billing/status"))["features"]!.AsArray().Select(f => f!.GetValue<string>()));

        // Customers can't pick private plans; they can move to a public plan they fit in, which lifts the limits.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{B}/billing/plan", new { planCode = "enterprise" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"{B}/billing/plan", new { planCode = "growth" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddEmployeeAsync(admin, "E002")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{B}/performance/cycles")).StatusCode);

        // Downgrading below current usage is refused.
        Assert.Equal("PLAN_LIMIT", await Code(await ops.PostAsJsonAsync($"{P}/tenants/{tenantId}/plan", new { planCode = "tiny" })));
        Assert.Contains(await app.WithDbAsync(tenantId, db => db.PayrollAuditLogs.Select(a => a.Action).ToListAsync()), a => a == "billing.plan.changed");
    }

    [Fact]
    public async Task Billing_cycle_converts_trial_invoices_in_arrears_and_suspends_for_non_payment()
    {
        var ops = await OperatorAsync();
        await ops.PostJson($"{P}/plans", new { code = "metered", name = "Metered", monthlyBaseFee = 1000m, includedEmployees = 1, pricePerAdditionalEmployee = 100m, maxEmployees = 100, maxLegalEntities = 1, features = Array.Empty<string>(), isPublic = true, isActive = true, sortOrder = 8 });
        var (admin, _, tenantId) = await SignUpAsync("Arrears Trading", plan: "metered");
        foreach (var n in new[] { "A1", "A2", "A3" }) Assert.Equal(HttpStatusCode.Created, (await AddEmployeeAsync(admin, n)).StatusCode);
        await admin.PostJson($"{B}/billing/activate", new { billingName = "Arrears Trading Ltd", billingEmail = "accounts@arrears.example", billingTaxPin = "p051234567a" });

        var sub = await SubscriptionAsync(tenantId);
        var trialEnd = sub.TrialEndsOn!.Value;
        Assert.Equal(SubscriptionStatus.Trialing, sub.Status); // activation during a trial does not cut it short

        await RunBillingAsync(trialEnd.AddDays(1), tenantId);
        sub = await SubscriptionAsync(tenantId);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal(trialEnd.AddDays(1), sub.CurrentPeriodStart);
        var periodEnd = sub.CurrentPeriodEnd!.Value;
        Assert.Equal(trialEnd.AddDays(1).AddMonths(1).AddDays(-1), periodEnd);

        // Period ends: invoice in arrears. 3 employees, 1 included: 1,000 + 2 × 100 = 1,200; VAT 16% = 192; total 1,392.
        var issueDay = periodEnd.AddDays(1);
        await RunBillingAsync(issueDay, tenantId);
        Assert.Equal(0, (await RunBillingAsync(issueDay, tenantId)).InvoicesIssued); // idempotent
        var inv = await app.WithDbAsync(tenantId, db => db.SubscriptionInvoices.AsNoTracking().SingleAsync());
        Assert.Equal(1200m, inv.Subtotal); Assert.Equal(192m, inv.Vat); Assert.Equal(1392m, inv.Total);
        Assert.Equal(3, inv.BillableEmployees);
        Assert.Equal(issueDay.AddDays(14), inv.DueOn);
        Assert.Equal("P051234567A", inv.BillingTaxPin);
        Assert.Matches("^INV-\\d{6}$", inv.Number);
        Assert.True(await app.WithDbAsync(tenantId, db => db.NotificationMessages.AnyAsync(m => m.Template == "billing.invoice")));

        var bill = await admin.GetJson($"{B}/billing");
        Assert.Equal(1392m, bill["invoices"]![0]!["total"].Dec());
        var pdf = await admin.GetAsync($"{B}/billing/invoices/{inv.Id}/pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync())[..4]);
        // Another organisation cannot fetch it.
        var rival = await app.LoginAsync("admin@demo.ecosys");
        Assert.Equal(HttpStatusCode.NotFound, (await rival.GetAsync($"{B}/billing/invoices/{inv.Id}/pdf")).StatusCode);

        // Overdue: past due but still usable.
        await RunBillingAsync(inv.DueOn.AddDays(1), tenantId);
        Assert.Equal(SubscriptionStatus.PastDue, (await SubscriptionAsync(tenantId)).Status);
        Assert.Equal(HttpStatusCode.Created, (await AddEmployeeAsync(admin, "A4")).StatusCode);

        // Beyond the grace period: read-only. Reads and exports still work, writes do not.
        var suspendDay = inv.DueOn.AddDays(8);
        await RunBillingAsync(suspendDay, tenantId);
        sub = await SubscriptionAsync(tenantId);
        Assert.Equal(SubscriptionStatus.Suspended, sub.Status);
        var blocked = await AddEmployeeAsync(admin, "A5");
        Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
        Assert.Equal("SUBSCRIPTION_INACTIVE", await Code(blocked));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{B}/employees")).StatusCode);
        Assert.True((await admin.GetJson($"{B}/billing/status"))["readOnly"]!.GetValue<bool>());
        // Reactivating by re-entering billing details doesn't get round an unpaid invoice.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{B}/billing/activate", new { billingName = "Arrears Trading Ltd", billingEmail = "accounts@arrears.example" })).StatusCode);

        // A part payment leaves it read-only; paying the balance reopens it.
        // (Recorded through the service so the simulated date applies; the endpoint uses the real clock.)
        using (var scope = app.Services.CreateScope())
        {
            var part = await scope.ServiceProvider.GetRequiredService<BillingService>().RecordPaymentAsync(inv.Id, 1000m, BillingPaymentMethod.MPesa, "SKL0001ABC", null, null, suspendDay, default);
            Assert.Equal(392m, part.Balance);
        }
        Assert.Equal(SubscriptionStatus.Suspended, (await SubscriptionAsync(tenantId)).Status);
        Assert.Equal("DUPLICATE", await Code(await ops.PostAsJsonAsync($"{P}/invoices/{inv.Id}/payments", new { amount = 100m, method = "MPesa", reference = "SKL0001ABC" }, PayrollApiFixture.Json)));
        Assert.Equal(HttpStatusCode.BadRequest, (await ops.PostAsJsonAsync($"{P}/invoices/{inv.Id}/payments", new { amount = 500m, method = "MPesa", reference = "SKL0002ABC" }, PayrollApiFixture.Json)).StatusCode); // more than owed
        await ops.PostJson($"{P}/invoices/{inv.Id}/payments", new { amount = 392m, method = "BankTransfer", reference = "FT26001" });
        app.Services.GetRequiredService<EntitlementCache>().Invalidate(tenantId);
        Assert.Equal(SubscriptionStatus.Active, (await SubscriptionAsync(tenantId)).Status);
        Assert.Equal(HttpStatusCode.Created, (await AddEmployeeAsync(admin, "A5")).StatusCode);

        var actions = await app.WithDbAsync(tenantId, db => db.PayrollAuditLogs.Select(a => a.Action).ToListAsync());
        foreach (var a in new[] { "tenant.signup", "billing.activated", "billing.trial.converted", "billing.invoice.issued", "billing.past_due", "billing.suspended", "billing.payment.recorded", "billing.reinstated" })
            Assert.Contains(a, actions);
    }

    [Fact]
    public async Task Trial_that_ends_without_billing_details_becomes_read_only_until_activated()
    {
        var (admin, _, tenantId) = await SignUpAsync("Lapsed Trial Ltd");
        var trialEnd = (await SubscriptionAsync(tenantId)).TrialEndsOn!.Value;
        await RunBillingAsync(trialEnd.AddDays(-3), tenantId);
        await RunBillingAsync(trialEnd.AddDays(-3), tenantId); // no repeat reminder the same day
        Assert.Equal(1, await app.WithDbAsync(tenantId, db => db.NotificationMessages.CountAsync(m => m.Template == "billing.trial.ending")));
        await RunBillingAsync(trialEnd.AddDays(1), tenantId);
        Assert.Equal(SubscriptionStatus.Suspended, (await SubscriptionAsync(tenantId)).Status);
        Assert.Equal("SUBSCRIPTION_INACTIVE", await Code(await AddEmployeeAsync(admin, "L1")));

        await admin.PostJson($"{B}/billing/activate", new { billingName = "Lapsed Trial Ltd", billingEmail = "pay@lapsed.example", planCode = "starter" });
        var sub = await SubscriptionAsync(tenantId);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal("starter", await app.WithDbAsync(tenantId, db => db.Plans.Where(p => p.Id == sub.PlanId).Select(p => p.Code).FirstAsync()));
        Assert.Equal(HttpStatusCode.Created, (await AddEmployeeAsync(admin, "L1")).StatusCode);

        // Cancelling takes effect at the end of the paid period, and can be undone until then.
        var cancel = await admin.PostJson($"{B}/billing/cancel", new { reason = "Moving to another provider" });
        Assert.True(cancel["cancelAtPeriodEnd"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Created, (await AddEmployeeAsync(admin, "L2")).StatusCode);
        await RunBillingAsync(sub.CurrentPeriodEnd!.Value.AddDays(1), tenantId);
        Assert.Equal(SubscriptionStatus.Cancelled, (await SubscriptionAsync(tenantId)).Status);
        Assert.Equal(1, await app.WithDbAsync(tenantId, db => db.SubscriptionInvoices.CountAsync())); // final invoice for the last period
        Assert.Equal("SUBSCRIPTION_INACTIVE", await Code(await AddEmployeeAsync(admin, "L3")));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{B}/employees")).StatusCode);
    }

    [Fact]
    public async Task Operator_console_is_for_platform_administrators_and_shows_no_payroll_data()
    {
        var (tenantAdmin, _, consoleTenant) = await SignUpAsync("Console Check Ltd");
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync($"{P}/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.LoginAsync("admin@demo.ecosys")).GetAsync($"{P}/overview")).StatusCode);

        var ops = await OperatorAsync();
        var tenants = (await ops.GetJson($"{P}/tenants")).AsArray();
        Assert.Contains(tenants, t => t!["slug"].Str() == "console-check-ltd" && t["status"].Str() == "Trialing");
        Assert.Contains(tenants, t => t!["slug"].Str() == "demo" && t["status"].Str() == "Managed");
        var overview = await ops.GetJson($"{P}/overview");
        Assert.True(overview["tenants"]!.GetValue<int>() >= 3);

        var demoId = tenants.First(t => t!["slug"].Str() == "demo")!["id"].Id();
        var detail = await (await ops.GetAsync($"{P}/tenants/{demoId}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Mwangi", detail); // employee names stay inside the tenant
        Assert.DoesNotContain("basicSalary", detail, StringComparison.OrdinalIgnoreCase);

        // An operator-managed tenant can be put on a plan; operator actions appear in the customer's audit log.
        await ops.PostJson($"{P}/tenants/{demoId}/subscription", new { planCode = "enterprise" });
        Assert.Equal(SubscriptionStatus.Active, (await SubscriptionAsync(demoId)).Status);
        Assert.Contains("billing.subscription.started", await app.WithDbAsync(demoId, db => db.PayrollAuditLogs.Select(a => a.Action).ToListAsync()));

        // A manual suspension can only be lifted by the operator, not by re-entering billing details.
        await ops.PostJson($"{P}/tenants/{consoleTenant}/suspend", new { reason = "Chargeback under review" });
        app.Services.GetRequiredService<EntitlementCache>().Invalidate(consoleTenant);
        Assert.Equal("SUBSCRIPTION_INACTIVE", await Code(await AddEmployeeAsync(tenantAdmin, "C1")));
        Assert.Equal(HttpStatusCode.Conflict, (await tenantAdmin.PostAsJsonAsync($"{B}/billing/activate", new { billingName = "Console Check Ltd", billingEmail = "pay@console.example" })).StatusCode);
        await ops.PostJson($"{P}/tenants/{consoleTenant}/reactivate");
        Assert.Equal(HttpStatusCode.Created, (await AddEmployeeAsync(tenantAdmin, "C1")).StatusCode);
        Assert.DoesNotContain(tenants, t => t!["slug"].Str() == PlatformAdminProvisioner.PlatformSlug);

        // Provision from the console with a one-time password.
        var made = await ops.PostJson($"{P}/tenants", new { organisationName = "Console Made Ltd", slug = "console-made", kraPin = NextPin(), adminEmail = "boss@made.example", adminName = "Boss Person", planCode = "starter", trial = true });
        Assert.False(string.IsNullOrEmpty(made["temporaryPassword"].Str()));
    }

    [GeneratedRegex("token=([A-Za-z0-9_-]+)")] private static partial Regex TokenRx();
}
