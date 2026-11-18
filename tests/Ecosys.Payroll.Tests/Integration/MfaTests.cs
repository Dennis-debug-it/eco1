using System.Net;
using System.Net.Http.Json;
using Ecosys.Payroll.Infrastructure.Security;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>Separate app instance with MFA enforced for privileged roles (PRD §13.5).</summary>
public sealed class MfaFixture : PayrollApiFixture
{
    protected override bool RequireMfa => true;
}

[Trait("Category", "Integration")]
public class MfaTests(MfaFixture app) : IClassFixture<MfaFixture>
{
    private const string B = "/api/payroll";

    [Fact]
    public async Task Privileged_user_must_enrol_and_use_totp()
    {
        // Password-only token for an approver can reach auth endpoints but not payroll data.
        var approver = await app.LoginAsync("approver@demo.ecosys");
        var blocked = await approver.GetAsync($"{B}/runs");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("MFA_REQUIRED", await blocked.Content.ReadAsStringAsync());
        var me = await approver.GetJson($"{B}/auth/me");
        Assert.True(me["mfaRequired"]!.GetValue<bool>());
        Assert.False(me["mfaSatisfied"]!.GetValue<bool>());

        // Enrol.
        var setup = await approver.PostJson($"{B}/auth/mfa/setup");
        var secret = setup["secret"].Str();
        Assert.StartsWith("otpauth://totp/", setup["provisioningUri"].Str());
        Assert.Equal(HttpStatusCode.BadRequest, (await approver.PostAsJsonAsync($"{B}/auth/mfa/enable", new { code = "000000" })).StatusCode);
        var enabled = await approver.PostJson($"{B}/auth/mfa/enable", new { code = Totp.Code(secret, DateTimeOffset.UtcNow) });
        approver.DefaultRequestHeaders.Authorization = new("Bearer", enabled["token"].Str());
        Assert.Equal(HttpStatusCode.OK, (await approver.GetAsync($"{B}/runs")).StatusCode);

        // Subsequent sign-ins need a code.
        var client = app.CreateClient();
        var noCode = await client.PostAsJsonAsync($"{B}/auth/login", new { tenant = "demo", email = "approver@demo.ecosys", password = PayrollApiFixture.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);
        Assert.Contains("MFA_CODE_REQUIRED", await noCode.Content.ReadAsStringAsync());
        var wrong = await client.PostAsJsonAsync($"{B}/auth/login", new { tenant = "demo", email = "approver@demo.ecosys", password = PayrollApiFixture.Password, code = "123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var ok = await app.LoginAsync("approver@demo.ecosys", code: Totp.Code(secret, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.OK, (await ok.GetAsync($"{B}/runs")).StatusCode);

        // Non-privileged employees are unaffected.
        var brian = await app.LoginAsync("brian.mwangi@demo.ecosys");
        var bme = await brian.GetJson($"{B}/auth/me");
        Assert.Equal(HttpStatusCode.OK, (await brian.GetAsync($"{B}/employees/{bme["employeeId"].Id()}/payslips")).StatusCode);
    }

    [Fact]
    public void Totp_matches_rfc6238_reference_vector()
    {
        // RFC 6238 Appendix B, SHA-1, T = 59s → 94287082 (8 digits); the 6-digit truncation is 287082.
        var secret = Totp.Base32Encode(System.Text.Encoding.ASCII.GetBytes("12345678901234567890"));
        Assert.Equal("287082", Totp.Code(secret, DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.True(Totp.Verify(secret, "287082", DateTimeOffset.FromUnixTimeSeconds(59 + 30))); // one step of drift
        Assert.False(Totp.Verify(secret, "287082", DateTimeOffset.FromUnixTimeSeconds(59 + 120)));
    }
}
