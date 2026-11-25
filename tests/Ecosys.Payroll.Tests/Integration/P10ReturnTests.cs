using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Seed;
using Ecosys.Payroll.Infrastructure.Services.Migration;
using Ecosys.Payroll.Infrastructure.Services.Returns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>KRA P10 monthly PAYE return: one row per employee, pay split into the P10 columns, reconciled to the run.</summary>
[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class P10ReturnTests(PayrollApiFixture app)
{
    private const string B = "/api/payroll";
    private static int _n;

    private sealed record Co(HttpClient Admin, Guid TenantId, Guid Le, Guid Group, Guid Period, int Year, int Month);

    /// <summary>A fresh company with two staff, one approved run for its first period.</summary>
    private async Task<(Co Co, Guid Run)> CompanyWithApprovedRunAsync(string staff)
    {
        var n = Interlocked.Increment(ref _n);
        TenantProvisionResult r;
        using (var scope = app.Services.CreateScope())
            r = await scope.ServiceProvider.GetRequiredService<TenantProvisioner>().ProvisionAsync(
                new($"P10 Co {n}", $"p10-{n}-{Guid.NewGuid():N}"[..16].TrimEnd('-'), $"P10 Co {n} Ltd", $"P05{Random.Shared.Next(1000000, 9999999)}{(char)('M' + n % 10)}", $"admin{n}@p10.example", "Njoki Admin"));
        await app.WithDbAsync(r.TenantId, async db =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == r.AdminUserId);
            u.PasswordHash = PasswordHasher.Hash(PayrollApiFixture.Password); u.MustChangePassword = false;
            return await db.SaveChangesAsync();
        });
        var admin = await app.LoginAsync(r.AdminEmail, r.Slug);
        var form = new MultipartFormDataContent { { new StringContent(r.LegalEntityId.ToString()), "legalEntityId" } };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(staff)); file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "staff.csv");
        await (await admin.PostAsync($"{B}/imports/employees", form)).Json();

        var (group, period, end) = await app.WithDbAsync(r.TenantId, async db =>
        {
            var p = await db.PayrollPeriods.OrderBy(x => x.PeriodStart).FirstAsync();
            return (p.PayrollGroupId, p.Id, p.PeriodEnd);
        });
        var run = (await admin.PostJson($"{B}/runs", new { legalEntityId = r.LegalEntityId, payrollGroupId = group, payrollPeriodId = period }, HttpStatusCode.Created))["id"].Id();
        await admin.PostJson($"{B}/runs/{run}/calculate");
        // One administrator can't approve their own run (maker-checker), so the approval is recorded directly.
        await app.WithDbAsync(r.TenantId, async db =>
        {
            var x = await db.PayrollRuns.FirstAsync(y => y.Id == run);
            x.Status = PayrollRunStatus.Approved; x.ApprovedAt = DateTimeOffset.UtcNow;
            return await db.SaveChangesAsync();
        });
        return (new Co(admin, r.TenantId, r.LegalEntityId, group, period, end.Year, end.Month), run);
    }

    private const string Staff =
        "EmployeeNumber,FirstName,LastName,HireDate,KraPin,BasicSalary,HOUSE,TRANSPORT,LIFE\r\n" +
        "P1,Atieno,Odera,01/01/2024,A200000001B,120000,25000,8000,3000\r\n" +
        "P2,Kamau,Njuguna,01/01/2024,A200000002C,45000,,,\r\n";

    private static string Url(Co c, string path) => $"{B}/reports/{path}?legalEntityId={c.Le}&year={c.Year}&month={c.Month}";

    [Fact]
    public async Task P10_splits_pay_into_KRA_columns_and_matches_the_run_to_the_shilling()
    {
        var (c, run) = await CompanyWithApprovedRunAsync(Staff);
        var check = await c.Admin.GetJson(Url(c, "p10/check"));
        Assert.True(check["ready"]!.GetValue<bool>(), check.ToJsonString());
        Assert.Equal(2, check["employees"]!.GetValue<int>());

        var res = await c.Admin.GetAsync(Url(c, "p10"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var rows = TabularReader.ReadXlsx(await res.Content.ReadAsByteArrayAsync());
        Assert.Equal(PayeReturnService.Columns.Select(x => x.Header), rows[0]);
        Assert.Equal(3, rows.Count);
        int Col(string h) => rows[0].IndexOf(h);
        decimal D(List<string> r, string h) => decimal.Parse(r[Col(h)], System.Globalization.CultureInfo.InvariantCulture);

        var p1 = rows.Single(r => r[0] == "A200000001B");
        Assert.Equal("ATIENO ODERA", p1[1]);
        Assert.Equal(("Resident", "Primary Employee"), (p1[2], p1[3]));
        Assert.Equal((120000m, 25000m, 8000m), (D(p1, "Basic Salary"), D(p1, "Housing Allowance"), D(p1, "Transport Allowance")));
        Assert.Equal(153000m, D(p1, "Total Cash Pay (A)"));
        Assert.Equal(153000m, D(p1, "Total Gross Pay"));

        // Everything else is the run's own figures, line for line.
        var result = await app.WithDbAsync(c.TenantId, db => db.PayrollRunEmployees.Include(e => e.Lines).SingleAsync(e => e.PayrollRunId == run && e.EmployeeNumber == "P1"));
        Assert.Equal(result.TaxablePay, D(p1, "Taxable Pay"));
        Assert.Equal(result.GrossTax, D(p1, "Tax Payable"));
        Assert.Equal(result.Paye, D(p1, "PAYE Tax"));
        Assert.Equal(result.Paye, D(p1, "Self Assessed PAYE Tax"));
        Assert.Equal(result.Shif, D(p1, "Social Health Insurance Fund (SHIF)"));
        Assert.Equal(result.HousingLevy, D(p1, "Affordable Housing Levy (AHL)"));
        Assert.Equal(result.Nssf, D(p1, "Actual Contribution"));
        Assert.Equal(result.Lines.Where(l => l.Code == "RELIEF.INSURANCE").Sum(l => l.Amount), D(p1, "Amount of Insurance Relief"));
        Assert.True(D(p1, "Amount of Insurance Relief") > 0);
        Assert.Equal(D(p1, "Tax Payable") - D(p1, "Monthly Personal Relief") - D(p1, "Amount of Insurance Relief"), D(p1, "PAYE Tax"));
        Assert.True(D(p1, "Permissible Limit") > 0);
        Assert.Contains("report.p10.exported", await app.WithDbAsync(c.TenantId, db => db.PayrollAuditLogs.Select(a => a.Action).ToListAsync()));
    }

    [Fact]
    public async Task A_components_P10_column_moves_its_pay_and_bad_pins_stop_the_file()
    {
        var (c, _) = await CompanyWithApprovedRunAsync(Staff);
        // Report transport as a director's fee: the run's figures don't change, the P10 column does.
        await app.WithDbAsync(c.TenantId, async db => { (await db.SalaryComponents.FirstAsync(x => x.Code == "TRANSPORT")).PayeReturnCategory = PayeReturnCategory.DirectorsFee; return await db.SaveChangesAsync(); });
        var rows = TabularReader.ReadXlsx(await (await c.Admin.GetAsync(Url(c, "p10"))).Content.ReadAsByteArrayAsync());
        var p1 = rows.Single(r => r[0] == "A200000001B");
        Assert.Equal("0", p1[rows[0].IndexOf("Transport Allowance")]);
        Assert.Equal("8000", p1[rows[0].IndexOf("Directors Fee")]);

        await app.WithDbAsync(c.TenantId, async db => { (await db.EmployeeStatutoryDetails.Join(db.Employees.Where(e => e.EmployeeNumber == "P2"), s => s.EmployeeId, e => e.Id, (s, e) => s).FirstAsync()).KraPin = "P051234567Z"; return await db.SaveChangesAsync(); });
        var check = await c.Admin.GetJson(Url(c, "p10/check"));
        Assert.False(check["ready"]!.GetValue<bool>());
        Assert.Contains(check["problems"]!.AsArray(), p => p!["employeeNumber"].Str() == "P2" && p["message"].Str().Contains("KRA PIN"));
        var refused = await c.Admin.GetAsync(Url(c, "p10"));
        Assert.Equal((HttpStatusCode)422, refused.StatusCode);
    }

    [Fact]
    public async Task Unapproved_runs_are_left_out_with_a_warning_and_HR_cannot_see_the_return()
    {
        var (c, run) = await CompanyWithApprovedRunAsync(Staff);
        await app.WithDbAsync(c.TenantId, async db => { var x = await db.PayrollRuns.FirstAsync(y => y.Id == run); x.Status = PayrollRunStatus.Calculated; x.ApprovedAt = null; return await db.SaveChangesAsync(); });
        var check = await c.Admin.GetJson(Url(c, "p10/check"));
        Assert.False(check["ready"]!.GetValue<bool>());
        Assert.Contains(check["problems"]!.AsArray(), p => p!["message"].Str().Contains("No approved payroll"));
        Assert.Contains(check["warnings"]!.AsArray(), w => w!.Str().Contains("Not yet approved"));

        var hr = await app.LoginAsync("hr@demo.ecosys");
        var demoLe = (await (await app.LoginAsync("admin@demo.ecosys")).GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.GetAsync($"{B}/reports/p10/check?legalEntityId={demoLe}&year=2025&month=1")).StatusCode);
    }
}
