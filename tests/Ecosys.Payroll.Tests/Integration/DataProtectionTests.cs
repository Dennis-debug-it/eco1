using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ecosys.Payroll.Domain.Employees;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Tests.Integration;

[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class DataProtectionTests(PayrollApiFixture app)
{
    private const string B = "/api/payroll";

    [Fact]
    public async Task Employees_can_download_their_own_data_but_not_someone_elses()
    {
        var brian = await app.LoginAsync("brian.mwangi@demo.ecosys");
        var res = await brian.GetAsync($"{B}/self-service/data-export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("attachment; filename=personal-data-SL006", res.Content.Headers.ContentDisposition!.ToString());
        var data = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal("Brian", data["personal"]!["firstName"]!.GetValue<string>());
        foreach (var section in new[] { "statutory", "bankAccounts", "salary", "leaveBalances", "userAccount", "recordOfChanges" }) Assert.NotNull(data[section]);

        var other = await app.WithDbAsync(null, db => db.Employees.Where(e => e.EmployeeNumber == "SL001").Select(e => e.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await brian.GetAsync($"{B}/data-protection/employees/{other}/export")).StatusCode);
        Assert.True(await app.WithDbAsync(null, db => db.PayrollAuditLogs.AnyAsync(a => a.Action == "dpa.subject_access.exported")));
    }

    [Fact]
    public async Task Former_employees_are_anonymised_only_after_the_retention_period()
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var (oldId, recentId) = await app.WithDbAsync(null, async db =>
        {
            var template = await db.Employees.FirstAsync(e => e.EmployeeNumber == "SL001");
            Employee Leaver(string number, DateOnly left) => new()
            {
                TenantId = template.TenantId, LegalEntityId = template.LegalEntityId, PayrollGroupId = template.PayrollGroupId, EmployeeNumber = number,
                FirstName = "Achieng", LastName = "Odera", NationalId = "23456789", Email = "achieng@example.com", Phone = "+254733000111",
                HireDate = new DateOnly(2012, 3, 1), TerminationDate = left, Status = EmploymentStatus.Terminated,
            };
            var old = Leaver("OLD001", new DateOnly(2017, 6, 30));
            var recent = Leaver("OLD002", new DateOnly(2023, 6, 30));
            db.Employees.AddRange(old, recent);
            db.EmployeeStatutoryDetails.Add(new EmployeeStatutoryDetails { TenantId = old.TenantId, LegalEntityId = old.LegalEntityId, EmployeeId = old.Id, KraPin = "A001122334K" });
            db.Dependants.Add(new Domain.Hr.Dependant { TenantId = old.TenantId, LegalEntityId = old.LegalEntityId, EmployeeId = old.Id, FullName = "Baby Odera", Relationship = "Child" });
            await db.SaveChangesAsync();
            return (old.Id, recent.Id);
        });

        var due = await admin.GetJson($"{B}/data-protection/retention");
        Assert.Equal(7, due["employeeRetentionYears"]!.GetValue<int>());
        var ids = due["employees"]!.AsArray().Select(e => e!["employeeId"].Id()).ToList();
        Assert.Contains(oldId, ids);
        Assert.DoesNotContain(recentId, ids);

        var early = await admin.PostAsJsonAsync($"{B}/data-protection/employees/{recentId}/anonymise", new { text = "Asked by employee" });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("must be kept until", await early.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{B}/data-protection/employees/{oldId}/anonymise", new { text = " " })).StatusCode);

        var ok = await admin.PostAsJsonAsync($"{B}/data-protection/employees/{oldId}/anonymise", new { text = "Retention period ended" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        var (e, stat, deps) = await app.WithDbAsync(null, async db => (
            await db.Employees.AsNoTracking().FirstAsync(x => x.Id == oldId),
            await db.EmployeeStatutoryDetails.AsNoTracking().FirstAsync(x => x.EmployeeId == oldId),
            await db.Dependants.CountAsync(x => x.EmployeeId == oldId)));
        Assert.Equal("Former employee", e.FirstName);
        Assert.Null(e.NationalId); Assert.Null(e.Email); Assert.Null(e.Phone); Assert.NotNull(e.AnonymisedAt);
        Assert.Null(stat.KraPin);
        Assert.Equal(0, deps);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{B}/data-protection/employees/{oldId}/anonymise", new { text = "again" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.LoginAsync("payroll@demo.ecosys")).GetAsync($"{B}/data-protection/retention")).StatusCode);
    }

    [Fact]
    public async Task Breach_register_tracks_the_72_hour_deadline_and_requires_a_decision_before_closing()
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var detected = DateTimeOffset.UtcNow.AddHours(-80);
        object Body(string status, DateTimeOffset? reported = null, string? notNotified = null) => new
        {
            detectedAt = detected, description = "Payroll register emailed to the wrong address", dataCategories = new[] { "Salaries", "KRA PINs" },
            subjectsAffected = 16, risk = "High", status, reportedToCommissionerAt = reported, commissionerReference = reported is null ? null : "ODPC/BR/2026/0412",
            subjectsNotNotifiedReason = notNotified,
        };
        var created = await admin.PostJson($"{B}/data-protection/breaches", Body("Open"), HttpStatusCode.Created);
        var id = created["id"].Id();
        Assert.StartsWith("BR-", created["reference"].Str());

        var list = await admin.GetJson($"{B}/data-protection/breaches");
        var row = list.AsArray().Single(r => r!["breach"]!["id"].Id() == id)!;
        Assert.True(row["commissionerOverdue"]!.GetValue<bool>());

        var close = await admin.PutAsJsonAsync($"{B}/data-protection/breaches/{id}", Body("Closed"), PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.BadRequest, close.StatusCode);
        Assert.Contains("Data Commissioner", await close.Content.ReadAsStringAsync());

        await admin.PutJson($"{B}/data-protection/breaches/{id}", Body("Closed", DateTimeOffset.UtcNow.AddHours(-10), "Recipient confirmed deletion in writing; risk assessed as low"));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"{B}/data-protection/breaches/{id}", Body("Open"), PayrollApiFixture.Json)).StatusCode);
    }
}
