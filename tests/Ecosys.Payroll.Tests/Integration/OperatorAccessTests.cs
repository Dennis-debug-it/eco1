using System.Net;
using System.Net.Http.Json;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>Operator roles: each sees and changes only its part of the console, and none can reach a tenant's records.</summary>
public partial class SaasTests
{
    [Fact]
    public void Operator_roles_carry_only_platform_permissions_and_need_mfa()
    {
        foreach (var role in Roles.Platform)
        {
            Assert.All(Permissions.ForRoles([role]), p => Assert.StartsWith("platform.", p));
            Assert.Contains(role, Roles.Privileged);
            Assert.DoesNotContain(role, Roles.Tenant);
        }
        Assert.DoesNotContain(Permissions.ForRoles([Roles.TenantAdministrator]), p => p.StartsWith("platform."));
    }

    [Fact]
    public async Task Each_operator_role_sees_and_changes_only_its_part_of_the_console()
    {
        var (_, _, tenantId) = await SignUpAsync("Role Split Ltd");
        var ops = await OperatorAsync(Roles.PlatformOperations);
        var finance = await OperatorAsync(Roles.PlatformFinance);

        // Operations: organisations and onboarding, but no money and no pricing.
        var opsMe = await ops.GetJson($"{P}/me");
        Assert.Contains(opsMe["permissions"]!.AsArray(), p => p.Str() == PlatformPermissions.TenantsManage);
        Assert.DoesNotContain(opsMe["permissions"]!.AsArray(), p => p.Str() == PlatformPermissions.BillingRead);
        Assert.Null((await ops.GetJson($"{P}/overview"))["mrrExVat"]);
        var row = (await ops.GetJson($"{P}/tenants")).AsArray().First(t => t!["id"].Id() == tenantId)!;
        Assert.Null(row["balance"]);
        Assert.Empty((await ops.GetJson($"{P}/tenants/{tenantId}"))["invoices"]!.AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync($"{P}/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.PostAsJsonAsync($"{P}/tenants/{tenantId}/suspend", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.PostAsJsonAsync($"{P}/billing/run", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync($"{P}/operators")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ops.GetAsync($"{P}/plans")).StatusCode); // to choose a plan when onboarding

        // Finance: revenue, invoices and subscriptions, but not account administration or pricing.
        Assert.NotNull((await finance.GetJson($"{P}/overview"))["mrrExVat"]);
        Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync($"{P}/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await finance.PostAsJsonAsync($"{P}/tenants/{tenantId}/access", new { enabled = false, reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await finance.PostAsJsonAsync($"{P}/tenants", new { organisationName = "Nope Ltd", slug = "nope-ltd", kraPin = NextPin(), adminEmail = "a@nope.example", adminName = "A B" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await finance.PostAsJsonAsync($"{P}/plans", new { code = "nope", name = "Nope", monthlyBaseFee = 1, includedEmployees = 1, pricePerAdditionalEmployee = 1, isPublic = false, isActive = true, sortOrder = 9 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await finance.GetAsync($"{P}/operators")).StatusCode);
        await finance.PostJson($"{P}/tenants/{tenantId}/extend-trial", new { until = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30) });

        // No operator role holds a tenant permission, not even in the operator's own organisation.
        var admin = await OperatorAsync();
        foreach (var c in new[] { ops, finance, admin })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"{B}/employees")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"{B}/auth/users")).StatusCode);
        }
    }

    [Fact]
    public async Task An_operator_role_outside_the_platform_organisation_grants_nothing()
    {
        var (tenantAdmin, _, tenantId) = await SignUpAsync("Smuggled Role Ltd");
        // Tenant administrators can't hand out operator roles...
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.PostAsJsonAsync($"{B}/auth/users", new { email = "x@smuggled.example", displayName = "X Y", password = StrongPassword, roles = new[] { Roles.PlatformFinance } }, PayrollApiFixture.Json)).StatusCode);
        // ...and a role planted in a customer organisation's data doesn't open the console.
        var email = "planted@smuggled-role-ltd.example";
        await app.WithDbAsync(tenantId, async db =>
        {
            db.Users.Add(new User { TenantId = tenantId, Email = email, DisplayName = "Planted", PasswordHash = PasswordHasher.Hash(StrongPassword), Roles = [Roles.PlatformAdministrator] });
            return await db.SaveChangesAsync();
        });
        var slug = await app.WithDbAsync(null, db => db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Slug).FirstAsync());
        var planted = await LoginAsync(slug, email, StrongPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await planted.GetAsync($"{P}/tenants")).StatusCode);
    }

    [Fact]
    public async Task Platform_administrators_manage_operator_accounts()
    {
        var admin = await OperatorAsync();
        var adminId = (await admin.GetJson($"{P}/me"))["userId"].Id();

        // Invite: a one-time password for the platform organisation, which must be changed at first sign-in.
        var email = $"new{Guid.NewGuid():N}"[..11] + "@operator.example";
        var invited = await admin.PostJson($"{P}/operators", new { email, name = "New Finance", roles = new[] { Roles.PlatformFinance } });
        Assert.Equal(PlatformAdminProvisioner.PlatformSlug, invited["signInCode"].Str());
        var newcomer = await LoginAsync(PlatformAdminProvisioner.PlatformSlug, email, invited["temporaryPassword"].Str());
        Assert.Equal("PASSWORD_CHANGE_REQUIRED", await Code(await newcomer.GetAsync($"{P}/tenants")));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{P}/operators", new { email, name = "Again", roles = new[] { Roles.PlatformFinance } }, PayrollApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{P}/operators", new { email = "t@operator.example", name = "Tenant Role", roles = new[] { Roles.TenantAdministrator } }, PayrollApiFixture.Json)).StatusCode);
        var id = invited["operatorId"].Id();
        Assert.Contains("platform.operator.invited", await app.WithDbAsync(null, db => db.PayrollAuditLogs.Where(a => a.EntityId == id).Select(a => a.Action).ToListAsync()));

        // Changing roles takes effect at once.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"{P}/operators/{id}/roles", new { roles = new[] { Roles.PlatformOperations } }, PayrollApiFixture.Json)).StatusCode);
        var listed = (await admin.GetJson($"{P}/operators")).AsArray().First(o => o!["id"].Id() == id)!;
        Assert.Equal(Roles.PlatformOperations, listed["roles"]!.AsArray().Single().Str());

        // Nobody changes, deactivates or resets their own account here.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{P}/operators/{adminId}/roles", new { roles = new[] { Roles.PlatformFinance } }, PayrollApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{P}/operators/{adminId}/deactivate", new { reason = "x" }, PayrollApiFixture.Json)).StatusCode);

        // Deactivating ends the operator's sessions straight away.
        var finance = await OperatorAsync(Roles.PlatformFinance);
        var financeId = (await finance.GetJson($"{P}/me"))["userId"].Id();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{P}/operators/{financeId}/deactivate", new { reason = "" }, PayrollApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"{P}/operators/{financeId}/deactivate", new { reason = "Left the company" }, PayrollApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await finance.GetAsync($"{P}/invoices")).StatusCode);

        // A password reset issues a new one-time password and ends sessions too.
        var ops = await OperatorAsync(Roles.PlatformOperations);
        var opsId = (await ops.GetJson($"{P}/me"))["userId"].Id();
        Assert.False(string.IsNullOrEmpty((await admin.PostJson($"{P}/operators/{opsId}/password"))["temporaryPassword"].Str()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ops.GetAsync($"{P}/tenants")).StatusCode);

        // Tenant user administration can't touch operators.
        var demoAdmin = await app.LoginAsync("admin@demo.ecosys");
        Assert.Equal(HttpStatusCode.NotFound, (await demoAdmin.PostAsJsonAsync($"{B}/auth/users/{id}/password", new { }, PayrollApiFixture.Json)).StatusCode);
    }
}
