using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Tests.Integration;

[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class PasswordResetTests(PayrollApiFixture app)
{
    private const string B = "/api/payroll";

    private async Task<Guid> NewUserAsync(string email, string tenant = "demo")
    {
        var id = Guid.Empty;
        await app.WithDbAsync(null, async db =>
        {
            var t = await db.Tenants.FirstAsync(x => x.Slug == tenant);
            var u = new User { TenantId = t.Id, Email = email, DisplayName = "Reset Tester", PasswordHash = PasswordHasher.Hash(PayrollApiFixture.Password), Roles = [Roles.Auditor] };
            db.Users.Add(u); id = u.Id;
            return await db.SaveChangesAsync();
        });
        return id;
    }

    private Task<List<string>> LinksAsync(Guid userId) =>
        app.WithDbAsync(null, db => db.NotificationMessages.Where(m => m.EntityId == userId && m.Template == "auth.password.reset.link").OrderBy(m => m.CreatedAt).Select(m => m.Body).ToListAsync());

    private static string TokenFrom(string body) => Regex.Match(body, @"reset-password\?token=([A-Za-z0-9_-]+)").Groups[1].Value;

    private async Task<HttpResponseMessage> ForgotAsync(string? organisation, string email) =>
        await app.CreateClient().PostAsJsonAsync($"{B}/auth/password/forgot", new { organisation, email });

    private async Task<HttpResponseMessage> ResetAsync(string token, string newPassword) =>
        await app.CreateClient().PostAsJsonAsync($"{B}/auth/password/reset", new { token, newPassword });

    private async Task<HttpResponseMessage> SignInAsync(string tenant, string email, string password) =>
        await app.CreateClient().PostAsJsonAsync($"{B}/auth/login", new { tenant, email, password });

    [Fact]
    public async Task Emailed_link_sets_a_new_password_once_and_signs_out_every_session()
    {
        var id = await NewUserAsync("forgetful@demo.ecosys");
        var before = await app.LoginAsync("forgetful@demo.ecosys");

        Assert.Equal(HttpStatusCode.NoContent, (await ForgotAsync("demo", "forgetful@demo.ecosys")).StatusCode);
        // A second request straight away sends nothing more, so the form can't flood a mailbox.
        Assert.Equal(HttpStatusCode.NoContent, (await ForgotAsync("demo", "forgetful@demo.ecosys")).StatusCode);
        var links = await LinksAsync(id);
        Assert.Single(links);
        Assert.Contains("organisation code 'demo'", links[0]);
        var token = TokenFrom(links[0]);

        var weak = await ResetAsync(token, "password123");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        var ok = await ResetAsync(token, "baobab shade at noon");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("demo", body!["slug"]!.GetValue<string>());

        // Single use.
        var again = await ResetAsync(token, "another long phrase here");
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("RESET_LINK_INVALID", await again.Content.ReadAsStringAsync());

        // Sessions opened with the old password end; the old password no longer works; the new one does.
        var revoked = await before.GetAsync($"{B}/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync("demo", "forgetful@demo.ecosys", PayrollApiFixture.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync("demo", "forgetful@demo.ecosys", "baobab shade at noon")).StatusCode);

        Assert.True(await app.WithDbAsync(null, db => db.NotificationMessages.AnyAsync(m => m.EntityId == id && m.Template == "auth.password.changed")));
        var actions = await app.WithDbAsync(null, db => db.PayrollAuditLogs.Where(a => a.EntityId == id).Select(a => a.Action).ToListAsync());
        Assert.Contains("auth.password.reset.requested", actions);
        Assert.Contains("auth.password.reset.completed", actions);
    }

    [Fact]
    public async Task Request_reveals_nothing_about_who_has_an_account()
    {
        var count = () => app.WithDbAsync(null, db => db.NotificationMessages.CountAsync(m => m.Template == "auth.password.reset.link"));
        var start = await count();
        Assert.Equal(HttpStatusCode.NoContent, (await ForgotAsync("demo", "nobody@nowhere.example")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ForgotAsync("no-such-org", "admin@demo.ecosys")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ForgotAsync(null, "not-an-email")).StatusCode);
        Assert.Equal(start, await count());
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync("made-up-token", "baobab shade at noon")).StatusCode);
    }

    [Fact]
    public async Task Without_an_organisation_code_each_account_gets_a_link_naming_its_organisation()
    {
        var mine = await NewUserAsync("two-hats@example.test");
        var other = await NewUserAsync("two-hats@example.test", "rival");
        Assert.Equal(HttpStatusCode.NoContent, (await ForgotAsync("", "two-hats@example.test")).StatusCode);
        Assert.Contains("organisation code 'demo'", Assert.Single(await LinksAsync(mine)));
        Assert.Contains("organisation code 'rival'", Assert.Single(await LinksAsync(other)));
    }

    [Fact]
    public async Task Expired_links_and_links_made_stale_by_another_password_change_are_refused()
    {
        var id = await NewUserAsync("stale@demo.ecosys");
        await ForgotAsync("demo", "stale@demo.ecosys");
        var token = TokenFrom((await LinksAsync(id)).Single());
        await app.WithDbAsync(null, async db => { (await db.Users.FirstAsync(u => u.Id == id)).PasswordResetExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); return await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(token, "baobab shade at noon")).StatusCode);

        // A fresh link, then an administrator reset: the link stops working.
        await app.WithDbAsync(null, async db => { (await db.Users.FirstAsync(u => u.Id == id)).PasswordResetExpiresAt = null; return await db.SaveChangesAsync(); });
        await ForgotAsync("demo", "stale@demo.ecosys");
        var fresh = TokenFrom((await LinksAsync(id)).Last());
        var admin = await app.LoginAsync("admin@demo.ecosys");
        await admin.PostJson($"{B}/auth/users/{id}/password", new { newPassword = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(fresh, "baobab shade at noon")).StatusCode);
    }

    [Fact]
    public async Task Reset_keeps_the_authenticator_requirement()
    {
        var id = await NewUserAsync("mfa-user@demo.ecosys");
        await app.WithDbAsync(null, async db =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == id);
            u.MfaEnabled = true; u.MfaSecret = Totp.NewSecret();
            return await db.SaveChangesAsync();
        });
        await ForgotAsync("demo", "mfa-user@demo.ecosys");
        var res = await ResetAsync(TokenFrom((await LinksAsync(id)).Single()), "baobab shade at noon");
        Assert.True((await res.Content.ReadFromJsonAsync<JsonObject>())!["mfaEnabled"]!.GetValue<bool>());
        var login = await SignInAsync("demo", "mfa-user@demo.ecosys", "baobab shade at noon");
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Contains("MFA_CODE_REQUIRED", await login.Content.ReadAsStringAsync());
    }
}
