using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Ecosys.Payroll.Tests.Integration;

public partial class SaasTests
{
    [Fact]
    public async Task The_API_is_a_plan_feature_and_a_read_only_account_keeps_reading_through_it()
    {
        var (admin, _, tenantId) = await SignUpAsync("Starter Api Ltd", plan: "starter");
        var refused = await admin.PostAsJsonAsync($"{B}/integrations/keys", new { name = "ERP", permissions = new[] { "employees.read", "employees.write" } });
        Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
        Assert.Equal("FEATURE_NOT_IN_PLAN", await Code(refused));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"{B}/billing/plan", new { planCode = "growth" })).StatusCode);
        var key = (await admin.PostJson($"{B}/integrations/keys", new { name = "ERP", permissions = new[] { "employees.read", "employees.write" } }, HttpStatusCode.Created))["key"].Str();
        var api = app.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/employees")).StatusCode);

        // Suspended: the key still reads (and the customer can take their data), but can't change anything.
        await (await OperatorAsync()).PostJson($"{P}/tenants/{tenantId}/suspend", new { reason = "Unpaid invoice" });
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/employees")).StatusCode);
        var write = await api.PostAsJsonAsync("/api/v1/employees", new { employeeNumber = "S1", firstName = "A", lastName = "B", hireDate = "2025-01-01" });
        Assert.Equal(HttpStatusCode.PaymentRequired, write.StatusCode);
        Assert.Equal("SUBSCRIPTION_INACTIVE", await Code(write));
    }
}
