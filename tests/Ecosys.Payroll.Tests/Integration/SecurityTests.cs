using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace Ecosys.Payroll.Tests.Integration;

[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class SecurityTests(PayrollApiFixture app)
{
    private const string B = "/api/payroll";

    private async Task<(HttpResponseMessage Res, string? Token)> SignIn(string email, string password)
    {
        var res = await app.CreateClient().PostAsJsonAsync($"{B}/auth/login", new { tenant = "demo", email, password });
        var token = res.IsSuccessStatusCode ? (await res.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!["token"]!.GetValue<string>() : null;
        return (res, token);
    }

    private HttpClient With(string token)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private async Task<Guid> CreateUser(HttpClient admin, string email, string password, string role = "PayrollAdministrator") =>
        (await admin.PostJson($"{B}/auth/users", new { email, displayName = "Wairimu Test", password, roles = new[] { role }, employeeId = (Guid?)null, legalEntityIds = Array.Empty<Guid>() }, HttpStatusCode.Created))["id"].Id();

    [Fact]
    public async Task New_user_must_replace_the_admin_password_and_old_sessions_end()
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var weak = await admin.PostAsJsonAsync($"{B}/auth/users", new { email = "weak@demo.ecosys", displayName = "Weak", password = "Nairobi2026!", roles = new[] { "Auditor" } });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("too common", await weak.Content.ReadAsStringAsync());

        await CreateUser(admin, "wairimu@demo.ecosys", "tusker-baobab-figtree");
        var (_, first) = await SignIn("wairimu@demo.ecosys", "tusker-baobab-figtree");
        var me = await With(first!).GetJson($"{B}/auth/me");
        Assert.True(me["mustChangePassword"]!.GetValue<bool>());
        var blocked = await With(first!).GetAsync($"{B}/runs");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("PASSWORD_CHANGE_REQUIRED", await blocked.Content.ReadAsStringAsync());

        // A second device signed in with the old password...
        var (_, other) = await SignIn("wairimu@demo.ecosys", "tusker-baobab-figtree");
        var changed = await With(first!).PostJson($"{B}/auth/password", new { currentPassword = "tusker-baobab-figtree", newPassword = "jacaranda flowers in october" });
        var fresh = changed["token"].Str();
        Assert.Equal(HttpStatusCode.OK, (await With(fresh).GetAsync($"{B}/runs")).StatusCode);
        // ...is signed out by the change.
        var revoked = await With(other!).GetAsync($"{B}/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Contains("SESSION_REVOKED", await revoked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_until_an_admin_unlocks_it()
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var id = await CreateUser(admin, "otieno.lock@demo.ecosys", "weaver-bird-nest-42", "Auditor");
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn("otieno.lock@demo.ecosys", "wrong-password-" + i)).Res.StatusCode);
        var fifth = await SignIn("otieno.lock@demo.ecosys", "wrong-password-5");
        Assert.Equal((HttpStatusCode)429, fifth.Res.StatusCode);
        Assert.Contains("ACCOUNT_LOCKED", await fifth.Res.Content.ReadAsStringAsync());
        // Even the right password is refused while locked.
        Assert.Equal((HttpStatusCode)429, (await SignIn("otieno.lock@demo.ecosys", "weaver-bird-nest-42")).Res.StatusCode);

        await admin.PostAsync($"{B}/auth/users/{id}/unlock", null);
        Assert.Equal(HttpStatusCode.OK, (await SignIn("otieno.lock@demo.ecosys", "weaver-bird-nest-42")).Res.StatusCode);

        var audit = await app.WithDbAsync(null, async db => db.PayrollAuditLogs.Where(a => a.EntityId == id).Select(a => a.Action).ToList());
        Assert.Contains("auth.locked", audit);
        Assert.Contains("auth.unlocked", audit);
    }

    [Fact]
    public async Task Deactivating_a_user_ends_their_session_and_admin_reset_forces_a_change()
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var id = await CreateUser(admin, "leaver.user@demo.ecosys", "flamingo-lake-nakuru-7", "Auditor");
        var (_, token) = await SignIn("leaver.user@demo.ecosys", "flamingo-lake-nakuru-7");
        await With(token!).PostJson($"{B}/auth/password", new { currentPassword = "flamingo-lake-nakuru-7", newPassword = "kestrel over the rift" });
        var (_, current) = await SignIn("leaver.user@demo.ecosys", "kestrel over the rift");
        Assert.Equal(HttpStatusCode.OK, (await With(current!).GetAsync($"{B}/reports/audit?format=csv")).StatusCode);

        var reset = await admin.PostJson($"{B}/auth/users/{id}/password", new { newPassword = (string?)null });
        var temp = reset["temporaryPassword"].Str();
        Assert.Equal(HttpStatusCode.Unauthorized, (await With(current!).GetAsync($"{B}/auth/me")).StatusCode);
        var (_, afterReset) = await SignIn("leaver.user@demo.ecosys", temp);
        Assert.Equal(HttpStatusCode.Forbidden, (await With(afterReset!).GetAsync($"{B}/reports/audit?format=csv")).StatusCode);

        var put = await admin.PutAsJsonAsync($"{B}/auth/users/{id}", new { displayName = "Leaver", roles = new[] { "Auditor" }, employeeId = (Guid?)null, legalEntityIds = Array.Empty<Guid>(), isActive = false });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await With(afterReset!).GetAsync($"{B}/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn("leaver.user@demo.ecosys", temp)).Res.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var res = await app.CreateClient().GetAsync("/health");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        var api = await (await app.LoginAsync("admin@demo.ecosys")).GetAsync($"{B}/auth/me");
        Assert.Equal("no-store", api.Headers.CacheControl!.ToString());
    }

    [Fact]
    public void Production_guard_rejects_development_settings()
    {
        var dev = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payroll:SeedDemoData"] = "true",
            ["Payroll:Encryption:Key"] = "ZGV2LW9ubHkta2V5LWRvLW5vdC11c2UtaW4tcHJvZCE=",
            ["Auth:SigningKey"] = "development-signing-key-change-me-0123456789abcdef",
            ["Auth:RequireMfaForPrivilegedRoles"] = "false",
            ["ConnectionStrings:Payroll"] = "Host=db;Username=payroll;Password=postgres",
        }).Build();
        Assert.Equal(5, ProductionGuard.Problems(dev).Count());

        var prod = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payroll:Encryption:Key"] = FieldEncryptor.GenerateKey(),
            ["Auth:SigningKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)),
            ["ConnectionStrings:Payroll"] = "Host=db;Username=payroll;Password=Xk2-strong-9q",
        }).Build();
        Assert.Empty(ProductionGuard.Problems(prod));
    }

    [Fact]
    public async Task Kenyan_identifiers_are_normalised_and_checked()
    {
        var hr = await app.LoginAsync("admin@demo.ecosys");
        var list = await hr.GetJson($"{B}/employees?limit=1");
        var id = list["items"]![0]!["id"].Id();
        var detail = await hr.GetJson($"{B}/employees/{id}");
        var emp = detail["employee"]!.AsObject();

        emp["phone"] = "0722 123 456";
        var saved = await hr.PutJson($"{B}/employees/{id}", emp);
        Assert.Equal("+254722123456", saved["phone"].Str());

        emp["phone"] = "12345";
        var bad = await hr.PutAsJsonAsync($"{B}/employees/{id}", emp, PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("isn't a valid number", await bad.Content.ReadAsStringAsync());

        var pin = await hr.PutAsJsonAsync($"{B}/employees/{id}/statutory", new { kraPin = "P051234567A", statutoryExemptions = Array.Empty<string>() }, PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.BadRequest, pin.StatusCode);
        Assert.Contains("belongs to a company", await pin.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("Password123!", false)]
    [InlineData("Safaricom2026", false)]
    [InlineData("1234567890", false)]
    [InlineData("aaaaaaaaaaaa", false)]
    [InlineData("wanjiru.kamau99", false)] // contains the email name
    [InlineData("jacaranda flowers in october", true)]
    [InlineData("Mt-Kenya-5199m-climb", true)]
    public void Password_policy(string password, bool ok) =>
        Assert.Equal(ok, PasswordPolicy.Validate(password, "wanjiru.kamau@savanna.example", "Wanjiru Kamau") is null);
}
