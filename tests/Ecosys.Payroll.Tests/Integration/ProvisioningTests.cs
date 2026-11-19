using System.Net;
using System.Net.Http.Json;
using Ecosys.Payroll.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecosys.Payroll.Tests.Integration;

[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class ProvisioningTests(PayrollApiFixture app)
{
    [Fact]
    public async Task Provisioned_tenant_has_kenyan_defaults_and_an_admin_who_must_set_a_password()
    {
        TenantProvisionResult result;
        using (var scope = app.Services.CreateScope())
            result = await scope.ServiceProvider.GetRequiredService<TenantProvisioner>().ProvisionAsync(
                new("Kisii Tea Growers", "kisii-tea", "Kisii Tea Growers Ltd", "p051444555m", "Admin@KisiiTea.example", "Moraa Nyaboke"));

        var counts = await app.WithDbAsync(result.TenantId, async db => new
        {
            holidays = await db.PublicHolidays.CountAsync(),
            leave = await db.LeaveTypes.Select(l => l.Code).ToListAsync(),
            periods = await db.PayrollPeriods.CountAsync(),
            employees = await db.Employees.CountAsync(),
            pin = await db.LegalEntities.Select(l => l.TaxPin).FirstAsync(),
        });
        Assert.True(counts.holidays >= 20);
        Assert.Equal(["ANNUAL", "COMP", "MAT", "PAT", "SICK", "UNPAID"], counts.leave.Order());
        Assert.Equal(24, counts.periods);
        Assert.Equal(0, counts.employees);
        Assert.Equal("P051444555M", counts.pin);

        var login = await app.CreateClient().PostAsJsonAsync("/api/payroll/auth/login", new { tenant = "kisii-tea", email = "admin@kisiitea.example", password = result.TemporaryPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("\"mustChangePassword\":true", await login.Content.ReadAsStringAsync());

        using var again = app.Services.CreateScope();
        await Assert.ThrowsAsync<Ecosys.Payroll.Domain.Common.DomainException>(() => again.ServiceProvider.GetRequiredService<TenantProvisioner>().ProvisionAsync(
            new("Copy", "kisii-tea", "Copy Ltd", "P051444555M", "x@y.example", "X")));
    }
}
