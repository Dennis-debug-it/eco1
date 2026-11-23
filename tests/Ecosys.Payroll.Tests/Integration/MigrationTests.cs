using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Migration;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>Moving a customer in from another payroll system: imports, undo, migration mode and P9 continuity.</summary>
[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class MigrationTests(PayrollApiFixture app)
{
    private const string B = "/api/payroll";
    private static int _n;

    private sealed record Company(HttpClient Admin, Guid TenantId, Guid LegalEntityId, Guid GroupId, string Slug);

    /// <summary>A fresh organisation with no employees, its administrator signed in.</summary>
    private async Task<Company> NewCompanyAsync()
    {
        var n = Interlocked.Increment(ref _n);
        var slug = $"migrate-{n}-{Guid.NewGuid():N}"[..20].TrimEnd('-');
        TenantProvisionResult r;
        using (var scope = app.Services.CreateScope())
            r = await scope.ServiceProvider.GetRequiredService<TenantProvisioner>().ProvisionAsync(
                new($"Migrating Co {n}", slug, $"Migrating Co {n} Ltd", $"P05{Random.Shared.Next(1000000, 9999999)}{(char)('A' + n % 26)}", $"admin{n}@migrate.example", "Wanjiku Admin"));
        await app.WithDbAsync(r.TenantId, async db =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == r.AdminUserId);
            u.PasswordHash = PasswordHasher.Hash(PayrollApiFixture.Password); u.MustChangePassword = false;
            return await db.SaveChangesAsync();
        });
        var admin = await app.LoginAsync(r.AdminEmail, r.Slug);
        var group = await app.WithDbAsync(r.TenantId, db => db.PayrollGroups.Select(g => g.Id).FirstAsync());
        return new Company(admin, r.TenantId, r.LegalEntityId, group, r.Slug);
    }

    private static MultipartFormDataContent Form(Guid legalEntityId, byte[] file, string name)
    {
        var form = new MultipartFormDataContent { { new StringContent(legalEntityId.ToString()), "legalEntityId" } };
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", name);
        return form;
    }

    private static async Task<JsonNode> Preview(Company c, string kind, string csv, string name = "file.csv") =>
        await (await c.Admin.PostAsync($"{B}/imports/{kind}/preview", Form(c.LegalEntityId, Encoding.UTF8.GetBytes(csv), name))).Json();

    private static async Task<HttpResponseMessage> Import(Company c, string kind, string csv, string name = "file.csv") =>
        await c.Admin.PostAsync($"{B}/imports/{kind}", Form(c.LegalEntityId, Encoding.UTF8.GetBytes(csv), name));

    private static List<string> Messages(JsonNode preview) => preview["problems"]!.AsArray().Select(p => $"row {p!["row"]}: {p["message"].Str()}").ToList();

    // Header names as Sage and spreadsheets write them; a semicolon-separated file; a full name to split; a bank by name.
    private const string StaffCsv =
        "Staff No;Surname;First Name;Date Joined;KRA PIN;NSSF No;Department;Basic Pay;House allowance;SACCO;Bank;Account No;Manager No;Job Title\r\n" +
        "M100;Kamau;Grace;01/02/2020;A123456789B;NS1001;;150000;30000;;KCB Bank;1100223344;;Finance manager\r\n" +
        "M101;Otieno;Brian;15/06/2023;A987654321C;NS1002;;60000;12000;2500;01;1100556677;M100;Accountant\r\n" +
        "M102;Wambui;Faith;\"3 Mar 2024\";;;;45000;;;;;M100;Clerk\r\n";

    [Fact]
    public async Task Employee_import_checks_every_row_first_and_loads_nothing_until_the_file_is_clean()
    {
        var c = await NewCompanyAsync();
        var bad = "EmployeeNumber,FirstName,LastName,HireDate,KraPin,Department,BankCode,AccountNumber,ManagerNumber\r\n" +
                  "A1,Ann,One,2024-01-05,P051234567A,,,,\r\n" +        // a company PIN
                  "A1,Ann,Again,2024-01-05,,,,,\r\n" +                 // duplicate number
                  "A2,Ben,Two,someday,,NOPE,,,\r\n" +                  // bad date, unknown department
                  "A3,Cy,Three,2024-01-05,,,9999,12345678,ZZ9\r\n";    // unknown bank, unknown manager
        var preview = await Preview(c, "employees", bad);
        Assert.False(preview["canImport"]!.GetValue<bool>());
        var msgs = Messages(preview);
        Assert.Contains(msgs, m => m.Contains("row 2") && m.Contains("belongs to a company"));
        Assert.Contains(msgs, m => m.Contains("row 3") && m.Contains("appears twice"));
        Assert.Contains(msgs, m => m.Contains("row 4") && m.Contains("isn't a date"));
        Assert.Contains(msgs, m => m.Contains("row 4") && m.Contains("Department 'NOPE'"));
        Assert.Contains(msgs, m => m.Contains("row 5") && m.Contains("Bank code '9999'"));
        Assert.Contains(msgs, m => m.Contains("row 5") && m.Contains("Manager ZZ9"));

        var refused = await Import(c, "employees", bad);
        Assert.Equal((HttpStatusCode)422, refused.StatusCode);
        Assert.Equal(0, await app.WithDbAsync(c.TenantId, db => db.Employees.CountAsync()));
        Assert.Equal(0, await app.WithDbAsync(c.TenantId, db => db.ImportBatches.CountAsync()));
    }

    [Fact]
    public async Task Clean_employee_file_loads_people_pay_and_unverified_bank_accounts_and_undo_removes_them()
    {
        var c = await NewCompanyAsync();
        var preview = await Preview(c, "employees", StaffCsv);
        Assert.True(preview["canImport"]!.GetValue<bool>(), string.Join("; ", Messages(preview)));
        Assert.Equal(3, preview["readyRows"]!.GetValue<int>());
        Assert.Contains(preview["notes"]!.AsArray(), n => n!.Str().Contains("House allowance → House allowance") && n.Str().Contains("SACCO"));

        var res = await (await Import(c, "employees", StaffCsv)).Json();
        var batchId = res["batch"]!["id"].Id();
        Assert.Equal(3, res["batch"]!["summary"]!["employees"]!.GetValue<int>());

        var loaded = await app.WithDbAsync(c.TenantId, async db => new
        {
            grace = await db.Employees.FirstAsync(e => e.EmployeeNumber == "M100"),
            brian = await db.Employees.FirstAsync(e => e.EmployeeNumber == "M101"),
            banks = await db.EmployeeBankAccounts.ToListAsync(),
            pins = await db.EmployeeStatutoryDetails.Select(s => s.KraPin).ToListAsync(),
            components = await db.SalaryComponents.ToDictionaryAsync(x => x.Id, x => x.Code),
            assignments = await db.EmployeeSalaryAssignments.Include(a => a.Lines).ToListAsync(),
        });
        Assert.Equal("Grace", loaded.grace.FirstName);
        Assert.Equal(new DateOnly(2020, 2, 1), loaded.grace.HireDate);
        Assert.Equal(loaded.grace.Id, loaded.brian.ReportingManagerId);
        Assert.Equal(2, loaded.banks.Count);
        Assert.All(loaded.banks, b => { Assert.False(b.IsVerified); Assert.Equal("01", b.BankCode); });
        Assert.Contains("A123456789B", loaded.pins);
        var brianPay = loaded.assignments.Single(a => a.EmployeeId == loaded.brian.Id);
        Assert.Equal(60000m, brianPay.BasicSalary);
        Assert.Equal(["HOUSE:12000", "SACCO:2500"], brianPay.Lines.Select(l => $"{loaded.components[l.SalaryComponentId]}:{l.Amount:0}").Order());

        // Undo removes every trace, and the same file can then be loaded again.
        await c.Admin.PostJson($"{B}/imports/{batchId}/undo");
        Assert.Equal(0, await app.WithDbAsync(c.TenantId, db => db.Employees.IgnoreQueryFilters().CountAsync(e => e.TenantId == c.TenantId)));
        Assert.Equal(0, await app.WithDbAsync(c.TenantId, db => db.EmployeeBankAccounts.CountAsync()));
        Assert.Equal(HttpStatusCode.OK, (await Import(c, "employees", StaffCsv)).StatusCode);
        var again = await c.Admin.PostAsJsonAsync($"{B}/imports/{batchId}/undo", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); // already undone
    }

    [Fact]
    public async Task Undo_goes_newest_first_and_stops_once_employees_are_in_use()
    {
        var c = await NewCompanyAsync();
        var first = (await (await Import(c, "employees", StaffCsv)).Json())["batch"]!["id"].Id();
        var balances = (await (await Import(c, "opening-balances", "EmployeeNumber,LeaveBalance\r\nM100,12\r\n")).Json())["batch"]!["id"].Id();

        var blocked = await c.Admin.PostAsJsonAsync($"{B}/imports/{first}/undo", new { });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("later import first", await blocked.Content.ReadAsStringAsync());
        await c.Admin.PostJson($"{B}/imports/{balances}/undo");

        // A loan entered by hand afterwards means the employees are in use: undo would orphan it.
        var grace = await app.WithDbAsync(c.TenantId, db => db.Employees.Where(e => e.EmployeeNumber == "M100").Select(e => e.Id).FirstAsync());
        var loanComponent = await app.WithDbAsync(c.TenantId, db => db.SalaryComponents.Where(x => x.Code == "LOAN").Select(x => x.Id).FirstAsync());
        await c.Admin.PostJson($"{B}/employees/{grace}/loans", new { salaryComponentId = loanComponent, reference = "HAND-1", principal = 1000, instalmentAmount = 100, startDate = "2026-01-01" }, HttpStatusCode.Created);
        var inUse = await c.Admin.PostAsJsonAsync($"{B}/imports/{first}/undo", new { });
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        Assert.Contains("staff loans", await inUse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Opening_balances_set_leave_loans_and_nssf_and_undo_puts_them_back()
    {
        var c = await NewCompanyAsync();
        await Import(c, "employees", StaffCsv);
        var year = DateTime.UtcNow.Year;
        var csv = "Emp No,Leave Type,Days Remaining,Loan Ref,Outstanding,Instalment,Principal,NSSF To Date\r\n" +
                  $"M100,Annual leave,17.5,,,,,48000\r\n" +
                  "M101,,,CAR-7,80000,10000,120000,\r\n" +
                  "M101,ANNUAL,3,,,,,\r\n";
        var preview = await Preview(c, "opening-balances", csv);
        Assert.True(preview["canImport"]!.GetValue<bool>(), string.Join("; ", Messages(preview)));
        var batch = (await (await Import(c, "opening-balances", csv)).Json())["batch"]!["id"].Id();

        var state = await app.WithDbAsync(c.TenantId, async db => new
        {
            leave = await db.EmployeeLeaveBalances.Join(db.Employees, b => b.EmployeeId, e => e.Id, (b, e) => new { e.EmployeeNumber, b.CarriedOver, b.Accrued, b.Taken, b.Year }).ToListAsync(),
            loan = await db.StaffLoans.SingleAsync(),
            nssf = await db.EmployeeStatutoryDetails.Join(db.Employees.Where(e => e.EmployeeNumber == "M100"), s => s.EmployeeId, e => e.Id, (s, e) => s.NssfOpeningBalance).SingleAsync(),
        });
        // Whatever the accrual formula gives, the days available come out at the figure the old system showed.
        Assert.Equal(17.5m, state.leave.Where(l => l.EmployeeNumber == "M100").Sum(l => l.CarriedOver + l.Accrued - l.Taken));
        Assert.Equal(3m, state.leave.Where(l => l.EmployeeNumber == "M101").Sum(l => l.CarriedOver + l.Accrued - l.Taken));
        Assert.All(state.leave, l => Assert.Equal(year, l.Year));
        Assert.Equal(("CAR-7", 120000m, 80000m, 10000m, LoanStatus.Active), (state.loan.Reference, state.loan.Principal, state.loan.Balance, state.loan.InstalmentAmount, state.loan.Status));
        Assert.Equal(48000m, state.nssf);

        await c.Admin.PostJson($"{B}/imports/{batch}/undo");
        Assert.Equal(0, await app.WithDbAsync(c.TenantId, db => db.StaffLoans.CountAsync()));
        Assert.Equal(0, await app.WithDbAsync(c.TenantId, db => db.EmployeeLeaveBalances.CountAsync()));
        Assert.Equal(0m, await app.WithDbAsync(c.TenantId, db => db.EmployeeStatutoryDetails.Join(db.Employees.Where(e => e.EmployeeNumber == "M100"), s => s.EmployeeId, e => e.Id, (s, e) => s.NssfOpeningBalance).SingleAsync()));
    }

    [Fact]
    public async Task Year_to_date_months_appear_on_the_P9_and_cannot_be_imported_twice_or_in_the_future()
    {
        var c = await NewCompanyAsync();
        await Import(c, "employees", StaffCsv);
        var year = DateTime.UtcNow.Year;
        var csv = "Employee Number,Month,Gross Pay,Taxable Pay,PAYE,Relief,NSSF,SHIF,AHL,Net Pay\r\n" +
                  "M100,Jan,180000,170000,45000,2400,4320,4950,2700,123030\r\n" +
                  "M100,February,180000,170000,45000,2400,4320,4950,2700,123030\r\n" +
                  "M101,1,72000,66000,14000,2400,4320,1980,1080,50620\r\n";
        var batch = await (await Import(c, "year-to-date", csv)).Json();
        Assert.Equal(3, batch["batch"]!["summary"]!["historyMonths"]!.GetValue<int>());

        var p9 = await c.Admin.GetJson($"{B}/reports/annual?legalEntityId={c.LegalEntityId}&year={year}");
        var grace = p9["employees"]!.AsArray().Single(e => e!["employeeNumber"].Str() == "M100")!;
        Assert.Equal(90000m, grace["totals"]!["paye"].Dec());
        Assert.Equal(180000m, grace["months"]![0]!["gross"].Dec());
        Assert.Equal(180000m, grace["months"]![1]!["gross"].Dec());
        Assert.Equal(0m, grace["months"]![2]!["gross"].Dec());
        Assert.Equal("A123456789B", grace["kraPin"].Str());

        var twice = await Preview(c, "year-to-date", "EmployeeNumber,Month,Gross,TaxablePay,PAYE\r\nM100,1,1,1,0\r\n");
        Assert.Contains(Messages(twice), m => m.Contains("already imported"));
        var future = await Preview(c, "year-to-date", $"EmployeeNumber,Year,Month,Gross,TaxablePay,PAYE\r\nM101,{year + 1},1,1,1,0\r\n");
        Assert.False(future["canImport"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_xlsx_workbook_is_read_like_a_csv()
    {
        var c = await NewCompanyAsync();
        var xlsx = Workbook(
            ["EmployeeNumber", "FirstName", "LastName", "HireDate", "BasicSalary"],
            ["X1", "Zawadi", "Mwende", 45292d, 52000d]); // 45292 = 2024-01-01 as an Excel date
        var preview = await (await c.Admin.PostAsync($"{B}/imports/employees/preview", Form(c.LegalEntityId, xlsx, "staff.xlsx"))).Json();
        Assert.True(preview["canImport"]!.GetValue<bool>(), string.Join("; ", Messages(preview)));
        await (await c.Admin.PostAsync($"{B}/imports/employees", Form(c.LegalEntityId, xlsx, "staff.xlsx"))).Json();
        var e = await app.WithDbAsync(c.TenantId, db => db.Employees.SingleAsync());
        Assert.Equal(("Zawadi", new DateOnly(2024, 1, 1)), (e.FirstName, e.HireDate));
    }

    [Fact]
    public async Task Migration_mode_blocks_paying_and_filing_until_go_live_and_discards_parallel_runs()
    {
        var c = await NewCompanyAsync();
        var started = await c.Admin.PostJson($"{B}/migration/start");
        Assert.True(started["inMigration"]!.GetValue<bool>());
        await Import(c, "employees", StaffCsv);

        // A parallel run calculates and can be compared, but goes no further.
        var period = await app.WithDbAsync(c.TenantId, db => db.PayrollPeriods.OrderBy(p => p.PeriodStart).Select(p => new { p.Id, p.PeriodStart }).FirstAsync());
        var run = (await c.Admin.PostJson($"{B}/runs", new { legalEntityId = c.LegalEntityId, payrollGroupId = c.GroupId, payrollPeriodId = period.Id }, HttpStatusCode.Created))["id"].Id();
        await c.Admin.PostJson($"{B}/runs/{run}/calculate");
        foreach (var path in new[] { "approve", "finalise", "payment-file" })
        {
            var res = await c.Admin.PostAsJsonAsync($"{B}/runs/{run}/{path}", new { });
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Contains("previous payroll system", await res.Content.ReadAsStringAsync());
        }
        var filing = await c.Admin.GetAsync($"{B}/reports/statutory?runId={run}&ruleCode=KE_PAYE&format=csv");
        Assert.Equal(HttpStatusCode.Conflict, filing.StatusCode);

        // Go-live with open checklist items needs an explicit yes; then the parallel run is gone and its pay items are free.
        var status = await c.Admin.GetJson($"{B}/migration");
        Assert.Contains(status["checklist"]!.AsArray(), x => x!["key"].Str() == "bank" && !x["done"]!.GetValue<bool>());
        Assert.Equal(1, status["parallelRunsToDiscard"]!.GetValue<int>());
        var nextMonth = period.PeriodStart.AddMonths(1);
        var refused = await c.Admin.PostAsJsonAsync($"{B}/migration/go-live", new { firstPeriodStart = nextMonth });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("CHECKLIST_OPEN", await refused.Content.ReadAsStringAsync());
        var live = await c.Admin.PostJson($"{B}/migration/go-live", new { firstPeriodStart = nextMonth, acceptOpenItems = true });
        Assert.False(live["inMigration"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Admin.GetAsync($"{B}/runs/{run}")).StatusCode);
        Assert.Contains("migration.golive.signed", await app.WithDbAsync(c.TenantId, db => db.PayrollAuditLogs.Select(a => a.Action).ToListAsync()));

        // Live: the guard no longer stands in the way (an unknown run is simply not found).
        Assert.Equal(HttpStatusCode.NotFound, (await c.Admin.PostAsJsonAsync($"{B}/runs/{Guid.NewGuid()}/approve", new { })).StatusCode);
    }

    [Fact]
    public async Task A_company_already_paid_through_Ecosys_cannot_enter_migration_mode()
    {
        var c = await NewCompanyAsync();
        await Import(c, "employees", StaffCsv);
        var period = await app.WithDbAsync(c.TenantId, db => db.PayrollPeriods.OrderBy(p => p.PeriodStart).Select(p => p.Id).FirstAsync());
        var run = (await c.Admin.PostJson($"{B}/runs", new { legalEntityId = c.LegalEntityId, payrollGroupId = c.GroupId, payrollPeriodId = period }, HttpStatusCode.Created))["id"].Id();
        await app.WithDbAsync(c.TenantId, async db => { (await db.PayrollRuns.FirstAsync(r => r.Id == run)).ApprovedAt = DateTimeOffset.UtcNow; return await db.SaveChangesAsync(); });
        var res = await c.Admin.PostAsJsonAsync($"{B}/migration/start", new { });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("already approved payroll", await res.Content.ReadAsStringAsync());
    }

    /// <summary>A minimal .xlsx: header as shared strings, values as inline strings or numbers.</summary>
    private static byte[] Workbook(string[] headers, object[] values)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Put(string path, string xml) { using var w = new StreamWriter(zip.CreateEntry(path).Open()); w.Write(xml); }
            const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Put("xl/workbook.xml", $"<workbook xmlns='{ns}' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Staff' sheetId='1' r:id='rId1'/></sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Target='worksheets/sheet1.xml'/></Relationships>");
            Put("xl/sharedStrings.xml", $"<sst xmlns='{ns}'>{string.Concat(headers.Select(h => $"<si><t>{h}</t></si>"))}</sst>");
            string Col(int i) => ((char)('A' + i)).ToString();
            var head = string.Concat(headers.Select((h, i) => $"<c r='{Col(i)}1' t='s'><v>{i}</v></c>"));
            var row = string.Concat(values.Select((v, i) => v is double d ? $"<c r='{Col(i)}2'><v>{d}</v></c>" : $"<c r='{Col(i)}2' t='inlineStr'><is><t>{v}</t></is></c>"));
            Put("xl/worksheets/sheet1.xml", $"<worksheet xmlns='{ns}'><sheetData><row r='1'>{head}</row><row r='2'>{row}</row></sheetData></worksheet>");
        }
        return ms.ToArray();
    }
}
