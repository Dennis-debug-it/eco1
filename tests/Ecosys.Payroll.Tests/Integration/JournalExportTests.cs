using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Seed;
using Ecosys.Payroll.Infrastructure.Services.Accounting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>Payroll journals as import files for QuickBooks (Online CSV, Desktop IIF) and Sage (Pastel, Evolution).</summary>
[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class JournalExportTests(PayrollApiFixture app)
{
    private const string B = "/api/payroll";
    private static int _n;

    /// <summary>A fresh company whose first payroll is finalised, so its journal exists.</summary>
    private async Task<(HttpClient Admin, Guid TenantId, Guid Le, Guid Run, DateOnly PeriodEnd)> FinalisedRunAsync()
    {
        var n = Interlocked.Increment(ref _n);
        TenantProvisionResult r;
        using (var scope = app.Services.CreateScope())
            r = await scope.ServiceProvider.GetRequiredService<TenantProvisioner>().ProvisionAsync(
                new($"Ledger Co {n}", $"ledger-{n}-{Guid.NewGuid():N}"[..18].TrimEnd('-'), $"Ledger Co {n} Ltd", $"P05{Random.Shared.Next(1000000, 9999999)}{(char)('B' + n % 10)}", $"admin{n}@ledger.example", "Wairimu Admin"));
        await app.WithDbAsync(r.TenantId, async db =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == r.AdminUserId);
            u.PasswordHash = PasswordHasher.Hash(PayrollApiFixture.Password); u.MustChangePassword = false;
            return await db.SaveChangesAsync();
        });
        var admin = await app.LoginAsync(r.AdminEmail, r.Slug);
        var form = new MultipartFormDataContent { { new StringContent(r.LegalEntityId.ToString()), "legalEntityId" } };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("EmployeeNumber,FirstName,LastName,HireDate,KraPin,BasicSalary,HOUSE,SACCO\r\nL1,Akoth,Onyango,01/01/2024,A300000001B,90000,15000,2000\r\nL2,Mutua,Kioko,01/01/2024,A300000002C,40000,,\r\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "staff.csv");
        await (await admin.PostAsync($"{B}/imports/employees", form)).Json();
        var (group, period, end) = await app.WithDbAsync(r.TenantId, async db => { var p = await db.PayrollPeriods.OrderBy(x => x.PeriodStart).FirstAsync(); return (p.PayrollGroupId, p.Id, p.PeriodEnd); });
        var run = (await admin.PostJson($"{B}/runs", new { legalEntityId = r.LegalEntityId, payrollGroupId = group, payrollPeriodId = period }, HttpStatusCode.Created))["id"].Id();
        await admin.PostJson($"{B}/runs/{run}/calculate");
        // A one-person company can't approve its own run (maker-checker); the approval is recorded directly, then finalised as normal.
        await app.WithDbAsync(r.TenantId, async db => { var x = await db.PayrollRuns.FirstAsync(y => y.Id == run); x.Status = PayrollRunStatus.Approved; x.ApprovedAt = DateTimeOffset.UtcNow; return await db.SaveChangesAsync(); });
        await admin.PostJson($"{B}/runs/{run}/finalise");
        return (admin, r.TenantId, r.LegalEntityId, run, end);
    }

    private static async Task<string> ExportAsync(HttpClient c, string format, string query)
    {
        var res = await c.GetAsync($"{B}/accounting/journals/export?format={format}&{query}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return Encoding.UTF8.GetString(await res.Content.ReadAsByteArrayAsync()).TrimStart('﻿');
    }

    private static decimal Num(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_finalised_runs_journal_exports_for_QuickBooks_and_Sage_and_balances_in_every_format()
    {
        var (admin, tenantId, _, run, end) = await FinalisedRunAsync();
        var q = $"runId={run}";
        var journal = await app.WithDbAsync(tenantId, db => db.PayrollJournals.Include(j => j.Lines).SingleAsync(j => j.PayrollRunId == run));
        var total = journal.Lines.Sum(l => l.Debit);
        Assert.True(total > 0);

        var check = await admin.GetJson($"{B}/accounting/journals/check?format=quickbooks-online&{q}");
        Assert.True(check["ready"]!.GetValue<bool>(), check.ToJsonString());

        // QuickBooks Online: a header, then one row per line; debits and credits in their own columns.
        var qbo = (await ExportAsync(admin, "quickbooks-online", q)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Journal No,Journal Date,Account,Debits,Credits,Description,Name,Class", qbo[0]);
        Assert.Equal(journal.Lines.Count, qbo.Length - 1);
        Assert.All(qbo.Skip(1), l => Assert.StartsWith($"{journal.Reference},{end:dd/MM/yyyy},", l));
        var cells = qbo.Skip(1).Select(l => l.Split(',')).ToList();
        Assert.Equal(total, cells.Sum(c => c[3] == "" ? 0 : Num(c[3])));
        Assert.Equal(total, cells.Sum(c => c[4] == "" ? 0 : Num(c[4])));

        // QuickBooks Desktop IIF: one GENERAL JOURNAL transaction whose amounts net to zero.
        var iif = (await ExportAsync(admin, "quickbooks-desktop", q)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["!TRNS", "!SPL", "!ENDTRNS"], iif.Take(3).Select(l => l.Split('\t')[0]));
        var body = iif.Skip(3).ToList();
        Assert.Equal("TRNS", body[0].Split('\t')[0]);
        Assert.Equal("ENDTRNS", body[^1]);
        Assert.All(body.Take(body.Count - 1), l => Assert.Equal("GENERAL JOURNAL", l.Split('\t')[2]));
        Assert.Equal(0m, body.Take(body.Count - 1).Sum(l => Num(l.Split('\t')[6])));

        // Sage Pastel batch: 18 fields per line, period from the financial year, seven-digit accounts, amounts net to zero.
        var pastel = (await ExportAsync(admin, "sage-pastel", q)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(',')).ToList();
        Assert.All(pastel, f => { Assert.Equal(18, f.Length); Assert.Equal(end.Month.ToString(), f[0]); Assert.Equal("G", f[2]); Assert.Matches("^[0-9]{7}$", f[3]); Assert.Equal($"PY{end:yyMM}01", f[4]); });
        Assert.Equal(0m, pastel.Sum(f => Num(f[6])));

        var evo = (await ExportAsync(admin, "sage-evolution", q)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Date,Reference,Description,Account,Debit,Credit,Project", evo[0]);
        Assert.Equal(journal.Lines.Count, evo.Length - 1);

        // Every export is recorded; doing it again warns about importing twice.
        var again = await admin.GetJson($"{B}/accounting/journals/check?format=sage-pastel&{q}");
        Assert.Contains(again["warnings"]!.AsArray(), w => w!.Str().Contains("Already exported"));
        Assert.Contains("journal.exported", await app.WithDbAsync(tenantId, db => db.PayrollAuditLogs.Select(a => a.Action).ToListAsync()));
    }

    [Fact]
    public async Task Month_export_covers_the_companys_journals_and_problems_stop_the_file()
    {
        var (admin, tenantId, le, run, end) = await FinalisedRunAsync();
        var month = $"legalEntityId={le}&from={new DateOnly(end.Year, end.Month, 1):yyyy-MM-dd}&to={end:yyyy-MM-dd}";
        var csv = (await ExportAsync(admin, "csv", month)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Journal,Source,Date,Line,Account", csv[0]);
        Assert.True(csv.Length > 2);

        // An account that isn't a Pastel number is caught before the file is made.
        await app.WithDbAsync(tenantId, async db => { (await db.PayrollJournalLines.FirstAsync(l => db.PayrollJournals.Any(j => j.Id == l.PayrollJournalId && j.PayrollRunId == run))).AccountCode = "Salaries"; return await db.SaveChangesAsync(); });
        var check = await admin.GetJson($"{B}/accounting/journals/check?format=sage-pastel&runId={run}");
        Assert.False(check["ready"]!.GetValue<bool>());
        Assert.Contains(check["problems"]!.AsArray(), p => p!["account"]?.Str() == "Salaries");
        Assert.Equal((HttpStatusCode)422, (await admin.GetAsync($"{B}/accounting/journals/export?format=sage-pastel&runId={run}")).StatusCode);
        // QuickBooks takes account names, so the same journal is fine there.
        Assert.True((await admin.GetJson($"{B}/accounting/journals/check?format=quickbooks-desktop&runId={run}"))["ready"]!.GetValue<bool>());

        // Journals are for finance: HR can't export them.
        var demoLe = (await (await app.LoginAsync("admin@demo.ecosys")).GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        Assert.Equal(HttpStatusCode.Forbidden, (await (await app.LoginAsync("hr@demo.ecosys")).GetAsync($"{B}/accounting/journals/check?legalEntityId={demoLe}&from=2025-01-01&to=2025-01-31")).StatusCode);
    }

    [Theory]
    [InlineData("6100", "6100000")]
    [InlineData("6100/010", "6100010")]
    [InlineData("2100-1", "2100001")]
    [InlineData("Salaries", null)]
    [InlineData("61000", null)]
    public void Pastel_account_numbers_are_seven_digits(string code, string? expected) =>
        Assert.Equal(expected, JournalExportService.PastelAccount(code));
}
