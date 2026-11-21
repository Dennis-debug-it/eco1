using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>Company paying accounts, the bank directory, and maker-checker on employee bank details.</summary>
[Collection(PayrollApiCollection.Name)]
[Trait("Category", "Integration")]
public class BankingTests(PayrollApiFixture app)
{
    private const string B = "/api/payroll";
    private const string DemoDebitAccount = "1107654321";

    private async Task<Guid> ApprovedRunAsync(string periodName)
    {
        var payroll = await app.LoginAsync("payroll@demo.ecosys");
        var le = (await payroll.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        var group = (await payroll.GetJson($"{B}/organisation/payroll-groups?legalEntityId={le}"))[0]!["id"].Id();
        var period = (await payroll.GetJson($"{B}/organisation/payroll-groups/{group}/periods")).AsArray().First(p => p!["name"].Str() == periodName)!["id"].Id();
        var run = (await payroll.PostJson($"{B}/runs", new { legalEntityId = le, payrollGroupId = group, payrollPeriodId = period }, HttpStatusCode.Created))["id"].Id();
        Assert.Equal(0, (await payroll.PostJson($"{B}/runs/{run}/calculate"))["errorCount"]!.GetValue<int>());
        await payroll.PostJson($"{B}/runs/{run}/submit", new { text = periodName });
        await (await app.LoginAsync("reviewer@demo.ecosys")).PostJson($"{B}/runs/{run}/review", new { text = "ok" });
        await (await app.LoginAsync("approver@demo.ecosys")).PostJson($"{B}/runs/{run}/approve", new { text = "ok" });
        return run;
    }

    private static async Task<Guid> EmployeeIdAsync(HttpClient c, string number) =>
        (await c.GetJson($"{B}/employees?limit=500"))["items"]!.AsArray().First(e => e!["employeeNumber"].Str() == number)!["id"].Id();

    [Fact]
    public async Task Bank_files_are_debited_from_the_company_paying_account()
    {
        var finance = await app.LoginAsync("finance@demo.ecosys");
        var accounts = (await finance.GetJson($"{B}/organisation/paying-accounts")).AsArray();
        var kcb = Assert.Single(accounts)!;
        Assert.Equal("******4321", kcb["accountNumber"].Str()); // masked on read
        Assert.True(kcb["isDefault"]!.GetValue<bool>());

        var run = await ApprovedRunAsync("January 2025");

        // An Equity bulk file can't be debited from a KCB account.
        var equity = await finance.PostAsJsonAsync($"{B}/runs/{run}/payment-file", new { format = "EquityBulk" }, PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, equity.StatusCode);
        Assert.Contains("must be debited from an account at that bank", await equity.Content.ReadAsStringAsync());

        // The KCB file carries the company's real debit account, not its registration number.
        var file = await finance.PostAsJsonAsync($"{B}/runs/{run}/payment-file", new { format = "KcbBulk" }, PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        var lines = (await file.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Equal(DemoDebitAccount, l.Split(',')[0]));

        var batch = (await finance.GetJson($"{B}/runs/{run}"))["batches"]![0]!;
        Assert.Equal(kcb["id"].Id(), batch["debitAccountId"].Id());
        var audited = await app.WithDbAsync(null, db => db.PayrollAuditLogs.Where(a => a.Action == "payroll.payment.file" && a.EntityId == batch["id"].Id()).Select(a => a.Details).SingleAsync());
        Assert.DoesNotContain(DemoDebitAccount, audited);
        Assert.Contains("4321", audited);
    }

    [Fact]
    public async Task Paying_accounts_need_a_listed_bank_and_organisation_permission()
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var le = (await admin.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        var bad = await admin.PostAsJsonAsync($"{B}/organisation/paying-accounts", new { legalEntityId = le, label = "X", bankCode = "99", accountName = "Savanna", accountNumber = "12345678" }, PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("isn't in the bank directory", await bad.Content.ReadAsStringAsync());

        var finance = await app.LoginAsync("finance@demo.ecosys");
        Assert.Equal(HttpStatusCode.Forbidden, (await finance.PostAsJsonAsync($"{B}/organisation/paying-accounts", new { legalEntityId = le, label = "X", bankCode = "68", accountName = "S", accountNumber = "12345678" }, PayrollApiFixture.Json)).StatusCode);

        var rival = await app.LoginAsync("admin@rival.test", "rival");
        Assert.Empty((await rival.GetJson($"{B}/organisation/paying-accounts")).AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await rival.PostAsJsonAsync($"{B}/organisation/paying-accounts", new { legalEntityId = le, label = "X", bankCode = "68", accountName = "S", accountNumber = "12345678" }, PayrollApiFixture.Json)).StatusCode);
    }

    [Fact]
    public async Task Employee_bank_details_are_checked_against_the_directory()
    {
        var hr = await app.LoginAsync("hr@demo.ecosys");
        var emp = await EmployeeIdAsync(hr, "SL011");
        async Task<HttpResponseMessage> Add(object body) => await hr.PostAsJsonAsync($"{B}/employees/{emp}/bank-accounts", body, PayrollApiFixture.Json);

        var unknownBank = await Add(new { method = "BankTransfer", bankCode = "99", accountName = "A", accountNumber = "12345678" });
        Assert.Equal(HttpStatusCode.BadRequest, unknownBank.StatusCode);
        Assert.Contains("isn't in the bank directory", await unknownBank.Content.ReadAsStringAsync());
        var letters = await Add(new { method = "BankTransfer", bankCode = "68", accountName = "A", accountNumber = "12AB5678" });
        Assert.Contains("6–20 digits", await letters.Content.ReadAsStringAsync());

        // Once a bank's branches are loaded, branch codes must match.
        var import = await hr.PostJson($"{B}/organisation/banks/import-branches", new { csv = "bank_code,branch_code,branch_name\n68,001,Head Office\n68,077,\"Kenyatta Avenue\"\n99,001,Nowhere\n" });
        Assert.Equal(2, import["added"]!.GetValue<int>());
        Assert.Single(import["errors"]!.AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsJsonAsync($"{B}/organisation/banks/import-branches", new { csv = "68,002,X", platform = true }, PayrollApiFixture.Json)).StatusCode);

        var badBranch = await Add(new { method = "BankTransfer", bankCode = "68", branchCode = "555", accountName = "A", accountNumber = "12345678" });
        Assert.Contains("isn't a listed Equity Bank Kenya branch", await badBranch.Content.ReadAsStringAsync());

        // Names come from the directory, not the request.
        var ok = await (await Add(new { method = "BankTransfer", bankCode = "68", bankName = "Fake Bank", branchCode = "077", accountName = "A", accountNumber = "0123 4567 89", isPrimary = false, splitPercent = 10 })).Json(HttpStatusCode.Created);
        Assert.Equal("Equity Bank Kenya", ok["bankName"].Str());
        Assert.Equal("Kenyatta Avenue", ok["branchName"].Str());
        Assert.Equal("0123456789", ok["accountNumber"].Str());
        Assert.False(ok["isVerified"]!.GetValue<bool>());

        var mobile = await (await Add(new { method = "MobileMoney", accountName = "A", accountNumber = "0712 345 678", isPrimary = false, splitPercent = 5 })).Json(HttpStatusCode.Created);
        Assert.Equal("+254712345678", mobile["accountNumber"].Str());

        // A tenant's branch list is its own.
        var rival = await app.LoginAsync("admin@rival.test", "rival");
        Assert.Empty((await rival.GetJson($"{B}/organisation/banks/68/branches")).AsArray());
        Assert.Contains((await rival.GetJson($"{B}/organisation/banks")).AsArray(), b => b!["code"].Str() == "68");
    }

    [Fact]
    public async Task A_second_person_verifies_bank_details_and_a_new_account_is_flagged_before_payment()
    {
        // Pay everyone once so the accounts on file are proven.
        var run = await ApprovedRunAsync("February 2025");
        var finance = await app.LoginAsync("finance@demo.ecosys");
        Assert.Equal(HttpStatusCode.OK, (await finance.PostAsJsonAsync($"{B}/runs/{run}/payment-file", new { format = "GenericCsv" }, PayrollApiFixture.Json)).StatusCode);
        var batch = (await finance.GetJson($"{B}/runs/{run}"))["batches"]![0]!["id"].Id();
        await finance.PostJson($"{B}/runs/{run}/payments/confirm", new { batchId = batch, bankReference = "FEB-25" });

        var hr = await app.LoginAsync("hr@demo.ecosys");
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var brian = await EmployeeIdAsync(hr, "SL006");

        // HR enters a new account: it starts unverified and the employee is told.
        var acct = await hr.PostJson($"{B}/employees/{brian}/bank-accounts", new { method = "BankTransfer", bankCode = "01", branchCode = "091", accountName = "Brian Mwangi", accountNumber = "1188776655", isPrimary = true }, HttpStatusCode.Created);
        var acctId = acct["id"].Id();
        Assert.False(acct["isVerified"]!.GetValue<bool>());
        var notice = await app.WithDbAsync(null, db => db.NotificationMessages.Where(m => m.EntityId == acctId && m.Template == "employee.bank.changed").Select(m => m.Body).FirstAsync());
        Assert.Contains("******6655", notice);
        Assert.DoesNotContain("1188776655", notice);

        // The maker can't verify their own entry; a colleague can.
        var own = await hr.PostAsJsonAsync($"{B}/employees/{brian}/bank-accounts/{acctId}/verify", new { }, PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Contains("different person", await own.Content.ReadAsStringAsync());
        var verified = await admin.PostJson($"{B}/employees/{brian}/bank-accounts/{acctId}/verify");
        Assert.True(verified["isVerified"]!.GetValue<bool>());
        Assert.NotNull(verified["verifiedAt"]);
        var accounts = (await admin.GetJson($"{B}/employees/{brian}"))["bankAccounts"]!.AsArray();
        Assert.Equal(acctId, Assert.Single(accounts, a => a!["isPrimary"]!.GetValue<bool>())!["id"].Id());

        // Nobody verifies their own details, even with employees.write.
        var amina = (await hr.GetJson($"{B}/auth/me"))["employeeId"].Id();
        var mine = await hr.PostJson($"{B}/employees/{amina}/bank-accounts", new { method = "BankTransfer", bankCode = "11", accountName = "Amina Hassan", accountNumber = "0112233445", isPrimary = false, splitPercent = 10 }, HttpStatusCode.Created);
        var selfVerify = await hr.PostAsJsonAsync($"{B}/employees/{amina}/bank-accounts/{mine["id"].Id()}/verify", new { }, PayrollApiFixture.Json);
        Assert.Equal(HttpStatusCode.Forbidden, selfVerify.StatusCode);
        Assert.Contains("your own bank details", await selfVerify.Content.ReadAsStringAsync());

        // The next run warns that Brian's pay goes to an account never paid before; the proven accounts don't.
        var payroll = await app.LoginAsync("payroll@demo.ecosys");
        var le = (await payroll.GetJson($"{B}/organisation/legal-entities"))[0]!["id"].Id();
        var group = (await payroll.GetJson($"{B}/organisation/payroll-groups?legalEntityId={le}"))[0]!["id"].Id();
        var period = (await payroll.GetJson($"{B}/organisation/payroll-groups/{group}/periods")).AsArray().First(p => p!["name"].Str() == "February 2026")!["id"].Id();
        var next = (await payroll.PostJson($"{B}/runs", new { legalEntityId = le, payrollGroupId = group, payrollPeriodId = period }, HttpStatusCode.Created))["id"].Id();
        await payroll.PostJson($"{B}/runs/{next}/calculate");
        var issues = (await admin.PostJson($"{B}/runs/{next}/validate"))["issues"]!.AsArray().Where(i => i!["code"].Str() == "BANK_CHANGED").ToList();
        var flagged = Assert.Single(issues)!;
        Assert.Equal("SL006", flagged["employeeNumber"].Str());
        Assert.Contains("******6655", flagged["message"].Str());
        Assert.Contains("Amina Hassan", flagged["message"].Str());
    }

    [Fact]
    public async Task A_legal_entity_can_allow_the_maker_to_verify()
    {
        var admin = await app.LoginAsync("admin@demo.ecosys");
        var le = (await admin.GetJson($"{B}/organisation/legal-entities"))[0]!.AsObject();
        Assert.False(le["settings"]!["allowBankDetailSelfVerification"]!.GetValue<bool>());
        var hr = await app.LoginAsync("hr@demo.ecosys");
        var emp = await EmployeeIdAsync(hr, "SL012");
        var acct = await hr.PostJson($"{B}/employees/{emp}/bank-accounts", new { method = "BankTransfer", bankCode = "03", accountName = "A", accountNumber = "2030405060", isPrimary = false, splitPercent = 10 }, HttpStatusCode.Created);

        var updated = JsonNode.Parse(le.ToJsonString())!.AsObject();
        updated["settings"]!["allowBankDetailSelfVerification"] = true;
        await admin.PutJson($"{B}/organisation/legal-entities/{le["id"].Id()}", updated);
        try
        {
            Assert.True((await hr.PostJson($"{B}/employees/{emp}/bank-accounts/{acct["id"].Id()}/verify"))["isVerified"]!.GetValue<bool>());
        }
        finally
        {
            updated["settings"]!["allowBankDetailSelfVerification"] = false;
            await admin.PutJson($"{B}/organisation/legal-entities/{le["id"].Id()}", updated);
        }
    }
}
