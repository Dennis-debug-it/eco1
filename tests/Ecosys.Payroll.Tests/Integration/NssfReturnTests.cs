using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ecosys.Payroll.Infrastructure.Services.Migration;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>NSSF upload files laid out as NSSF's portal template (Rel 03.02.18) and returns-file specification require.</summary>
[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class NssfReturnTests(PayrollApiFixture app)
{
    private const string B = "/api/payroll";

    // SHIF starts in October 2024 and other tests use October and November, so the three tests share one December run.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Guid? _run;

    private async Task<Guid> ApprovedRunAsync()
    {
        await Gate.WaitAsync();
        try { return _run ??= await CreateApprovedRunAsync("December"); }
        finally { Gate.Release(); }
    }

    /// <summary>An approved run on a 2024 period no other test uses.</summary>
    private async Task<Guid> CreateApprovedRunAsync(string month)
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var le = (await admin.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        var group = (await admin.GetJson($"{B}/organisation/payroll-groups?legalEntityId={le}"))[0]!["id"].Id();
        await admin.PostJson($"{B}/organisation/payroll-groups/{group}/periods/generate", new { year = 2024, payDay = 28 });
        var period = (await admin.GetJson($"{B}/organisation/payroll-groups/{group}/periods")).AsArray().First(p => p!["name"].Str() == $"{month} 2024")!["id"].Id();
        var payroll = await app.LoginAsync("payroll@demo.ecosys");
        var id = (await payroll.PostJson($"{B}/runs", new { legalEntityId = le, payrollGroupId = group, payrollPeriodId = period }, HttpStatusCode.Created))["id"].Id();
        await payroll.PostJson($"{B}/runs/{id}/calculate");
        var check = await payroll.GetJson($"{B}/reports/nssf-file/check?runId={id}");
        Assert.Contains(check["problems"]!.AsArray(), p => p!["message"].Str().Contains("Approve the run first"));
        await payroll.PostJson($"{B}/runs/{id}/submit", new { text = "ready" });
        await (await app.LoginAsync("reviewer@demo.ecosys")).PostJson($"{B}/runs/{id}/review", new { text = "checked" });
        await (await app.LoginAsync("approver@demo.ecosys")).PostJson($"{B}/runs/{id}/approve", new { text = "ok" });
        return id;
    }

    private static (List<List<string>> Rows, string SheetXml) Read(byte[] xlsx)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx));
        Assert.Single(Regex.Matches(new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open()).ReadToEnd(), "<sheet ")); // one sheet only
        var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
        Assert.DoesNotContain("<f>", sheet); // no formulas
        return (TabularReader.ReadXlsx(xlsx), sheet);
    }

    [Fact]
    public async Task Portal_template_file_matches_NSSFs_layout_and_carries_what_was_deducted()
    {
        var run = await ApprovedRunAsync();
        var payroll = await app.LoginAsync("payroll@demo.ecosys");
        var check = await payroll.GetJson($"{B}/reports/nssf-file/check?runId={run}");
        Assert.True(check["ready"]!.GetValue<bool>(), check.ToJsonString());
        var members = check["members"]!.GetValue<int>();
        Assert.True(members > 5);

        var res = await payroll.GetAsync($"{B}/reports/nssf-file?runId={run}&layout=portal");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var (rows, sheet) = Read(await res.Content.ReadAsByteArrayAsync());
        Assert.Equal(["PAYROLL NUMBER", "SURNAME", "OTHER NAMES", "ID NO", "KRA PIN", "NSSF NO", "GROSS PAY", "VOLUNTARY"], rows[0]);
        Assert.Equal(members + 1, rows.Count); // nothing after the last record
        Assert.All(rows.Skip(1), r =>
        {
            Assert.Matches("^[A-Z0-9]{9,10}$", r[5]);
            Assert.DoesNotMatch("[/,\"-]", string.Join("|", r.Take(6)));
            Assert.True(decimal.Parse(r[6], System.Globalization.CultureInfo.InvariantCulture) > 0);
            Assert.Equal("0", r[7]);
        });
        // Columns A to F stored as text (style 1 = Text), so leading zeros survive; G and H plain numbers.
        Assert.Matches("<c r=\"F2\" s=\"1\" t=\"s\">", sheet);
        Assert.Matches("<c r=\"G2\"><v>", sheet);
        // Pensionable pay in the file reproduces the NSSF the run deducted.
        var totals = await app.WithDbAsync(null, db => db.PayrollRuns.Where(r => r.Id == run).Select(r => new { r.TotalNssfEmployee }).FirstAsync());
        Assert.Equal(totals.TotalNssfEmployee, check["member"].Dec());
        Assert.Contains("report.nssf.exported", await app.WithDbAsync(null, db => db.PayrollAuditLogs.Where(a => a.EntityId == run).Select(a => a.Action).ToListAsync()));
    }

    [Fact]
    public async Task Returns_file_has_the_header_block_and_a_row_per_contribution_tier()
    {
        var run = await ApprovedRunAsync();
        var payroll = await app.LoginAsync("payroll@demo.ecosys");
        var (rows, sheet) = Read(await (await payroll.GetAsync($"{B}/reports/nssf-file?runId={run}&layout=returns")).Content.ReadAsByteArrayAsync());

        Assert.Equal(["NSSF CONTRIBUTIONS", "01"], rows[0]);
        Assert.Equal("100234567", rows[2][1]);
        Assert.Equal("122024", rows[4][1]);
        Assert.Empty(rows[10]);                               // row 11 blank
        Assert.DoesNotContain("<row r=\"11\"", sheet);
        Assert.Equal("PAYROLL NUMBER", rows[11][0]);           // row 12 titles
        Assert.Equal("TOTAL", rows[11][11]);
        var details = rows.Skip(12).ToList();                  // from row 13
        Assert.Equal(details.Count, int.Parse(rows[9][1]));    // B10 = number of records
        decimal Col(int c) => details.Sum(r => decimal.Parse(r[c], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Col(9), decimal.Parse(rows[6][1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Col(10), decimal.Parse(rows[7][1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Col(11), decimal.Parse(rows[8][1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.All(details, r => { Assert.Contains(r[6], new[] { "101", "102" }); Assert.Equal("1", r[8]); Assert.Equal(r[11], (decimal.Parse(r[9]) + decimal.Parse(r[10])).ToString()); });
        // Every member has a Tier I row; Tier II rows only above the lower earnings limit; whole shillings only.
        Assert.Equal(details.Select(r => r[5]).Distinct().Count(), details.Count(r => r[6] == "101"));
        Assert.All(details, r => Assert.DoesNotContain(".", r[9] + r[10]));
        var deducted = await app.WithDbAsync(null, db => db.PayrollRuns.Where(r => r.Id == run).Select(r => r.TotalNssfEmployee).FirstAsync());
        Assert.InRange(Col(9) - deducted, 0m, details.Count); // rounding up adds under a shilling per amount
    }

    [Fact]
    public async Task Members_with_bad_details_are_listed_and_no_file_is_made()
    {
        var run = await ApprovedRunAsync();
        var target = await app.WithDbAsync(null, db => db.EmployeeStatutoryDetails.Join(db.Employees.Where(e => e.EmployeeNumber == "SL002"), s => s.EmployeeId, e => e.Id, (s, e) => s).Select(s => new { s.Id, s.NssfNumber }).FirstOrDefaultAsync());
        Assert.NotNull(target);
        await app.WithDbAsync(null, async db => { (await db.EmployeeStatutoryDetails.FirstAsync(s => s.Id == target!.Id)).NssfNumber = "12345"; return await db.SaveChangesAsync(); });
        try
        {
            var payroll = await app.LoginAsync("payroll@demo.ecosys");
            var check = await payroll.GetJson($"{B}/reports/nssf-file/check?runId={run}");
            Assert.False(check["ready"]!.GetValue<bool>());
            Assert.Contains(check["problems"]!.AsArray(), p => p!["employeeNumber"].Str() == "SL002" && p["message"].Str().Contains("9–10"));
            var res = await payroll.GetAsync($"{B}/reports/nssf-file?runId={run}&layout=portal");
            Assert.Equal((HttpStatusCode)422, res.StatusCode);
            Assert.Contains("RETURN_NOT_READY", await res.Content.ReadAsStringAsync());

            // Pay figures need salary access: HR can't export the file.
            Assert.Equal(HttpStatusCode.Forbidden, (await (await app.LoginAsync("hr@demo.ecosys")).GetAsync($"{B}/reports/nssf-file/check?runId={run}")).StatusCode);
        }
        finally
        {
            await app.WithDbAsync(null, async db => { (await db.EmployeeStatutoryDetails.FirstAsync(s => s.Id == target!.Id)).NssfNumber = target!.NssfNumber; return await db.SaveChangesAsync(); });
        }
    }
}
