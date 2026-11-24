using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>Workers paid a daily wage for the days they work, alongside monthly staff in the same run.</summary>
[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class DailyPayTests(PayrollApiFixture app)
{
    private const string B = "/api/payroll";
    private static int _n;

    private async Task<(HttpClient Admin, Guid TenantId, Guid Le, Guid Group)> NewCompanyAsync()
    {
        var n = Interlocked.Increment(ref _n);
        TenantProvisionResult r;
        using (var scope = app.Services.CreateScope())
            r = await scope.ServiceProvider.GetRequiredService<TenantProvisioner>().ProvisionAsync(
                new($"Daily Co {n}", $"daily-{n}-{Guid.NewGuid():N}"[..18].TrimEnd('-'), $"Daily Co {n} Ltd", $"P05{Random.Shared.Next(1000000, 9999999)}{(char)('K' + n % 10)}", $"admin{n}@daily.example", "Akinyi Admin"));
        await app.WithDbAsync(r.TenantId, async db =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == r.AdminUserId);
            u.PasswordHash = PasswordHasher.Hash(PayrollApiFixture.Password); u.MustChangePassword = false;
            return await db.SaveChangesAsync();
        });
        var admin = await app.LoginAsync(r.AdminEmail, r.Slug);
        var group = await app.WithDbAsync(r.TenantId, db => db.PayrollGroups.Select(g => g.Id).FirstAsync());
        return (admin, r.TenantId, r.LegalEntityId, group);
    }

    private static async Task ImportAsync(HttpClient c, Guid le, string csv)
    {
        var form = new MultipartFormDataContent { { new StringContent(le.ToString()), "legalEntityId" } };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv)); file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "staff.csv");
        await (await c.PostAsync($"{B}/imports/employees", form)).Json();
    }

    private const string Staff =
        "EmployeeNumber,FirstName,LastName,HireDate,KraPin,PayBasis,BasicSalary,Daily Rate\r\n" +
        "D1,Otieno,Ouma,01/01/2024,A100000001B,Daily,,1200\r\n" +            // daily-paid, 1,200 a day
        "D2,Wanjiku,Mwangi,01/01/2024,A100000002C,,,950\r\n" +              // basis taken from the daily rate
        "D3,Kiprop,Chirchir,01/01/2024,A100000003D,daily,,800\r\n" +        // will work no days
        "D4,Halima,Abdi,01/01/2024,A100000004E,Daily,,1000\r\n" +           // no attendance entered
        "M1,Mercy,Njeri,01/01/2024,A100000005F,Monthly,60000,\r\n";         // monthly, for contrast

    [Fact]
    public async Task Daily_paid_workers_earn_their_rate_for_days_worked_and_monthly_staff_are_unchanged()
    {
        var (admin, tenantId, le, group) = await NewCompanyAsync();
        await ImportAsync(admin, le, Staff);

        // The daily rate is kept, with its monthly equivalent (x 22 standard days) for notice pay, dues and reports.
        var assignments = await app.WithDbAsync(tenantId, db => db.EmployeeSalaryAssignments.Join(db.Employees, a => a.EmployeeId, e => e.Id, (a, e) => new { e.EmployeeNumber, a.PayBasis, a.DailyRate, a.BasicSalary }).ToListAsync());
        var d1 = assignments.Single(a => a.EmployeeNumber == "D1");
        Assert.Equal((PayBasis.Daily, 1200m, 26400m), (d1.PayBasis, d1.DailyRate!.Value, d1.BasicSalary));
        Assert.Equal(PayBasis.Daily, assignments.Single(a => a.EmployeeNumber == "D2").PayBasis);
        Assert.Equal((PayBasis.Monthly, (decimal?)null), (assignments.Single(a => a.EmployeeNumber == "M1").PayBasis, assignments.Single(a => a.EmployeeNumber == "M1").DailyRate));

        var period = await app.WithDbAsync(tenantId, db => db.PayrollPeriods.OrderBy(p => p.PeriodStart).Select(p => p.Id).FirstAsync());
        var run = (await admin.PostJson($"{B}/runs", new { legalEntityId = le, payrollGroupId = group, payrollPeriodId = period }, HttpStatusCode.Created))["id"].Id();
        await admin.PutJson($"{B}/runs/{run}/attendance", new[]
        {
            new { employeeNumber = "D1", daysWorked = 12m, absentDays = 3m, overtimeHours = 4m, restDayOvertimeHours = 0m, note = (string?)null },
            new { employeeNumber = "D2", daysWorked = 20m, absentDays = 0m, overtimeHours = 0m, restDayOvertimeHours = 0m, note = (string?)null },
            new { employeeNumber = "D3", daysWorked = 0m, absentDays = 0m, overtimeHours = 0m, restDayOvertimeHours = 0m, note = (string?)"off this month" },
            new { employeeNumber = "M1", daysWorked = 0m, absentDays = 2m, overtimeHours = 0m, restDayOvertimeHours = 0m, note = (string?)null },
        });
        await admin.PostJson($"{B}/runs/{run}/calculate");

        var rows = await app.WithDbAsync(tenantId, db => db.PayrollRunEmployees.Include(e => e.Lines).Where(e => e.PayrollRunId == run).ToListAsync());
        var o = rows.Single(r => r.EmployeeNumber == "D1");
        // 12 days x 1,200 = 14,400; absent days cost nothing more; overtime at 1,200 / (195 / 22) per hour x 1.5.
        Assert.Equal(14400m, o.BasicPay);
        Assert.Contains(o.Lines, l => l.Name.Contains("12 days x 1,200.00"));
        var hourly = 1200m / (195m / 22m);
        Assert.Equal(Math.Round(4 * hourly * 1.5m, 2), o.Lines.Single(l => l.Code == "OT").Amount);
        Assert.DoesNotContain(o.Lines, l => l.Code == "UNPAID_LEAVE");
        Assert.Equal(19000m, rows.Single(r => r.EmployeeNumber == "D2").BasicPay);

        // No days worked: left out of the run, not paid nil. No attendance at all: the run says what's missing.
        Assert.DoesNotContain(rows, r => r.EmployeeNumber == "D3");
        Assert.Contains(rows.Single(r => r.EmployeeNumber == "D4").Issues, i => i.Code == "DAYS_REQUIRED");

        // Monthly staff keep the monthly rules: two absent days deducted at 60,000 / 22.
        var m = rows.Single(r => r.EmployeeNumber == "M1");
        Assert.Equal(60000m, m.BasicPay);
        Assert.Contains(m.Lines, l => l.Code == "UNPAID_LEAVE" && l.Amount == -Math.Round(60000m / 22m * 2, 2));
    }

    [Fact]
    public async Task More_days_than_the_period_holds_is_an_error_and_the_salary_screen_takes_a_daily_rate()
    {
        var (admin, tenantId, le, group) = await NewCompanyAsync();
        await ImportAsync(admin, le, "EmployeeNumber,FirstName,LastName,HireDate,BasicSalary\r\nS1,Moraa,Kerubo,01/01/2024,40000\r\n");
        var id = await app.WithDbAsync(tenantId, db => db.Employees.Select(e => e.Id).FirstAsync());

        // Switch a monthly employee to a daily wage from next period.
        var periods = await app.WithDbAsync(tenantId, db => db.PayrollPeriods.OrderBy(p => p.PeriodStart).Select(p => new { p.Id, p.PeriodStart, p.PeriodEnd }).Take(2).ToListAsync());
        await admin.PostJson($"{B}/employees/{id}/salary", new { basicSalary = 0, payBasis = "Daily", dailyRate = 1500, effectiveFrom = periods[1].PeriodStart, changeReason = "Now casual" }, HttpStatusCode.Created);
        var noRate = await admin.PostAsJsonAsync($"{B}/employees/{id}/salary", new { basicSalary = 0, payBasis = "Daily", effectiveFrom = periods[1].PeriodStart.AddDays(5) }, PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.BadRequest, noRate.StatusCode);
        var current = await app.WithDbAsync(tenantId, db => db.EmployeeSalaryAssignments.Where(a => a.EmployeeId == id && a.EffectiveTo == null).SingleAsync());
        Assert.Equal((PayBasis.Daily, 1500m, 33000m), (current.PayBasis, current.DailyRate!.Value, current.BasicSalary));

        var run = (await admin.PostJson($"{B}/runs", new { legalEntityId = le, payrollGroupId = group, payrollPeriodId = periods[1].Id }, HttpStatusCode.Created))["id"].Id();
        await admin.PutJson($"{B}/runs/{run}/attendance", new[] { new { employeeNumber = "S1", daysWorked = 40m, absentDays = 0m, overtimeHours = 0m, restDayOvertimeHours = 0m, note = (string?)null } });
        await admin.PostJson($"{B}/runs/{run}/calculate");
        var row = await app.WithDbAsync(tenantId, db => db.PayrollRunEmployees.SingleAsync(e => e.PayrollRunId == run));
        Assert.Contains(row.Issues, i => i.Code == "DAYS_TOO_MANY");
    }
}
