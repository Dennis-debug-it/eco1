using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Finance;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Engine.Jurisdictions.Kenya;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services;

/// <summary>
/// Builds a balanced accounting journal from stored result lines (PRD §8). Expense is debited per
/// cost centre; liabilities are credited per payee (employees, KRA, NSSF, SHA, other).
/// Account codes come from <see cref="GlAccountMapping"/> with sensible defaults.
/// </summary>
public sealed class JournalBuilder(PayrollDbContext db)
{
    public static class DefaultAccounts
    {
        public const string SalaryExpense = "6100";
        public const string EmployerContributionExpense = "6150";
        public const string NetSalaryPayable = "2100";
        public const string PayePayable = "2110";
        public const string NssfPayable = "2120";
        public const string ShifPayable = "2130";
        public const string HousingLevyPayable = "2140";
        public const string NitaPayable = "2150";
        public const string OtherDeductionsPayable = "2190";
    }

    public async Task<PayrollJournal> BuildAsync(PayrollRun run, PayrollPeriod period, CancellationToken ct)
    {
        var mappings = await db.GlAccountMappings.Where(m => m.LegalEntityId == run.LegalEntityId).AsNoTracking().ToDictionaryAsync(m => m.SourceCode, StringComparer.OrdinalIgnoreCase, ct);
        var empIds = run.Employees.Select(e => e.Id).ToList();
        var lines = await db.PayrollResultLines.Where(l => empIds.Contains(l.PayrollRunEmployeeId)).AsNoTracking().ToListAsync(ct);
        var byEmp = run.Employees.ToDictionary(e => e.Id);

        string Expense(string code, string fallback) => mappings.TryGetValue(code, out var m) ? m.ExpenseAccount : fallback;
        string Liability(string code, string fallback) => mappings.TryGetValue(code, out var m) && m.LiabilityAccount is not null ? m.LiabilityAccount : fallback;

        var journal = new PayrollJournal
        {
            TenantId = run.TenantId, LegalEntityId = run.LegalEntityId, PayrollRunId = run.Id,
            Reference = $"PJ-{run.Reference}", PostingDate = period.PeriodEnd, CurrencyCode = run.CurrencyCode, Status = JournalStatus.Posted,
        };

        // Debits: earnings by cost centre; employer contributions by cost centre.
        var debits = new Dictionary<(string Account, Guid? Cc, string Desc), decimal>();
        var credits = new Dictionary<(string Account, string Desc), decimal>();

        foreach (var l in lines)
        {
            var emp = byEmp[l.PayrollRunEmployeeId];
            switch (l.Type)
            {
                case ResultLineType.Earning:
                    Add(debits, (l.GlAccountCode ?? Expense(l.Code, DefaultAccounts.SalaryExpense), emp.CostCentreId, l.Code == "UNPAID_LEAVE" ? "Unpaid leave recovery" : DebitName(l.Name)), l.Amount);
                    break;
                case ResultLineType.EmployerContribution:
                    Add(debits, (Expense(l.Code, DefaultAccounts.EmployerContributionExpense), emp.CostCentreId, $"Employer {DebitName(l.Name.Replace(" (employer)", ""))} expense"), l.Amount);
                    Add(credits, (LiabilityFor(l, Liability), $"{l.Name} payable"), l.Amount);
                    break;
                case ResultLineType.StatutoryDeduction:
                    Add(credits, (LiabilityFor(l, Liability), $"{l.Name} payable"), l.Amount);
                    break;
                case ResultLineType.Deduction:
                    Add(credits, (l.GlAccountCode ?? Liability(l.Code, DefaultAccounts.OtherDeductionsPayable), $"{DebitName(l.Name)} payable"), l.Amount);
                    break;
            }
        }
        Add(credits, (Liability("NET", DefaultAccounts.NetSalaryPayable), "Net salaries payable"), run.Employees.Sum(e => e.NetPay));

        var seq = 0;
        foreach (var ((account, cc, desc), amount) in debits.OrderBy(d => d.Key.Account).ThenBy(d => d.Key.Cc))
            journal.Lines.Add(new PayrollJournalLine { TenantId = run.TenantId, PayrollJournalId = journal.Id, AccountCode = account, Description = desc, Debit = amount, CostCentreId = cc, Sequence = ++seq });
        foreach (var ((account, desc), amount) in credits.Where(c => c.Value != 0).OrderBy(c => c.Key.Account))
            journal.Lines.Add(new PayrollJournalLine { TenantId = run.TenantId, PayrollJournalId = journal.Id, AccountCode = account, Description = desc, Credit = amount, Sequence = ++seq });

        journal.TotalDebit = journal.Lines.Sum(l => l.Debit);
        journal.TotalCredit = journal.Lines.Sum(l => l.Credit);
        if (!journal.IsBalanced)
            throw new InvalidStateException($"Payroll journal does not balance: Dr {journal.TotalDebit:N2} vs Cr {journal.TotalCredit:N2}.");
        return journal;
    }

    /// <summary>Strips per-instance suffixes (loan references, adjustment notes) so lines aggregate by component.</summary>
    private static string DebitName(string name)
    {
        var i = name.IndexOf(" (", StringComparison.Ordinal);
        return i > 0 ? name[..i] : name;
    }

    private static string LiabilityFor(PayrollResultLineRecord l, Func<string, string, string> liability) => l.StatutoryRuleCode switch
    {
        KenyaRuleCodes.Paye => liability(l.StatutoryRuleCode, DefaultAccounts.PayePayable),
        KenyaRuleCodes.Nssf => liability(l.StatutoryRuleCode, DefaultAccounts.NssfPayable),
        KenyaRuleCodes.Shif => liability(l.StatutoryRuleCode, DefaultAccounts.ShifPayable),
        KenyaRuleCodes.HousingLevy => liability(l.StatutoryRuleCode, DefaultAccounts.HousingLevyPayable),
        KenyaRuleCodes.Nita => liability(l.StatutoryRuleCode, DefaultAccounts.NitaPayable),
        _ => l.GlAccountCode ?? liability(l.Code, DefaultAccounts.OtherDeductionsPayable),
    };

    public static PayrollJournal Reverse(PayrollJournal original, DateOnly postingDate, string reason)
    {
        var contra = new PayrollJournal
        {
            TenantId = original.TenantId, LegalEntityId = original.LegalEntityId, PayrollRunId = original.PayrollRunId,
            Reference = original.Reference + "-REV", PostingDate = postingDate, CurrencyCode = original.CurrencyCode,
            Status = JournalStatus.Posted, ReversalOfJournalId = original.Id, TotalDebit = original.TotalCredit, TotalCredit = original.TotalDebit,
        };
        foreach (var l in original.Lines.OrderBy(l => l.Sequence))
            contra.Lines.Add(new PayrollJournalLine
            {
                TenantId = l.TenantId, PayrollJournalId = contra.Id, AccountCode = l.AccountCode, Description = $"Reversal: {l.Description} ({reason})",
                Debit = l.Credit, Credit = l.Debit, CostCentreId = l.CostCentreId, DepartmentId = l.DepartmentId, BranchId = l.BranchId, Sequence = l.Sequence,
            });
        return contra;
    }

    private static void Add<TKey>(Dictionary<TKey, decimal> d, TKey key, decimal amount) where TKey : notnull =>
        d[key] = d.GetValueOrDefault(key) + amount;
}
