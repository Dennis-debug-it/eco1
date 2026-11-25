using System.Globalization;
using System.Text;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Finance;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Accounting;

/// <summary>Accounting systems a journal can be exported for.</summary>
public enum JournalExportFormat { QuickBooksOnline, QuickBooksDesktop, SagePastel, SageEvolution, GenericCsv }

public sealed record JournalExportProblem(string Journal, string? Account, string Message);

public sealed record JournalExportCheck(string Format, int Journals, int Lines, decimal Debits, decimal Credits, bool Ready,
    IReadOnlyList<JournalExportProblem> Problems, IReadOnlyList<string> Warnings);

public sealed record JournalExportFile(byte[] Content, string FileName, string ContentType, JournalExportCheck Check);

/// <summary>
/// Payroll journals as import files for the customer's accounting system: QuickBooks Online (journal CSV), QuickBooks
/// Desktop (IIF), Sage Pastel Partner/Xpress (batch import) and Sage 200 Evolution (journal CSV), plus a plain CSV.
/// Either one run's journals, or every journal a company posted in a date range (payroll, leave provision, training,
/// expenses). Account codes are written as mapped in Ecosys, so the mapping should use the accounting system's own
/// account names or numbers. Each export is recorded on the journal, and a second export of the same journal warns.
/// </summary>
public sealed class JournalExportService(PayrollDbContext db, ITenantContext tenant, AuditService audit)
{
    public static JournalExportFormat Parse(string? s) => (s ?? "").Replace("-", "").Replace("_", "").ToLowerInvariant() switch
    {
        "quickbooksonline" or "qbo" => JournalExportFormat.QuickBooksOnline,
        "quickbooksdesktop" or "qbd" or "iif" => JournalExportFormat.QuickBooksDesktop,
        "sagepastel" or "pastel" => JournalExportFormat.SagePastel,
        "sageevolution" or "evolution" => JournalExportFormat.SageEvolution,
        "csv" or "genericcsv" or "" => JournalExportFormat.GenericCsv,
        _ => throw new DomainException("Format is quickbooks-online, quickbooks-desktop, sage-pastel, sage-evolution or csv.", "VALIDATION"),
    };

    private sealed record Scope(Guid LegalEntityId, string Label, List<PayrollJournal> Journals);

    public async Task<JournalExportCheck> CheckAsync(JournalExportFormat format, Guid? runId, Guid? legalEntityId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var scope = await LoadAsync(runId, legalEntityId, from, to, ct);
        return await CheckAsync(format, scope, ct);
    }

    public async Task<JournalExportFile> ExportAsync(JournalExportFormat format, Guid? runId, Guid? legalEntityId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var scope = await LoadAsync(runId, legalEntityId, from, to, ct);
        var check = await CheckAsync(format, scope, ct);
        if (!check.Ready) throw new JournalExportNotReadyException(check);
        var entity = await db.LegalEntities.FirstAsync(e => e.Id == scope.LegalEntityId, ct);
        var centres = await db.CostCentres.Where(c => c.LegalEntityId == scope.LegalEntityId).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var (content, ext, type) = format switch
        {
            JournalExportFormat.QuickBooksOnline => (QuickBooksOnline(scope.Journals, centres), "csv", "text/csv"),
            JournalExportFormat.QuickBooksDesktop => (QuickBooksDesktop(scope.Journals, centres), "iif", "text/plain"),
            JournalExportFormat.SagePastel => (SagePastel(scope.Journals, entity.FinancialYearStartMonth), "csv", "text/csv"),
            JournalExportFormat.SageEvolution => (SageEvolution(scope.Journals, centres), "csv", "text/csv"),
            _ => (Generic(scope.Journals, centres), "csv", "text/csv"),
        };
        var stamp = $"{format} {BusinessClock.ToLocal(DateTimeOffset.UtcNow):yyyy-MM-dd HH:mm}";
        foreach (var j in scope.Journals) j.ExternalReference = $"Exported to {stamp}";
        audit.Record("journal.exported", nameof(PayrollJournal), runId ?? scope.LegalEntityId, new { format = format.ToString(), journals = scope.Journals.Select(j => j.Reference).ToList(), check.Debits }, scope.LegalEntityId);
        await db.SaveChangesAsync(ct);
        var name = $"Journals_{Safe(scope.Label)}_{format}.{ext}";
        // A byte-order mark helps Excel and QuickBooks Online read accented names; IIF and the Sage importers expect plain text.
        var bom = format is JournalExportFormat.QuickBooksOnline or JournalExportFormat.GenericCsv;
        var bytes = bom ? Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray() : Encoding.UTF8.GetBytes(content);
        return new JournalExportFile(bytes, name, type, check);
    }

    private async Task<Scope> LoadAsync(Guid? runId, Guid? legalEntityId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        tenant.Require(Permissions.JournalsRead);
        if (runId is { } rid)
        {
            var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == rid, ct) ?? throw new NotFoundException("PayrollRun", rid);
            tenant.RequireLegalEntity(run.LegalEntityId);
            var journals = await db.PayrollJournals.Include(j => j.Lines.OrderBy(l => l.Sequence)).Where(j => j.PayrollRunId == rid).OrderBy(j => j.PostingDate).ThenBy(j => j.CreatedAt).ToListAsync(ct);
            return new Scope(run.LegalEntityId, run.Reference, journals);
        }
        if (legalEntityId is not { } le || from is not { } f || to is not { } t) throw new DomainException("Give a payroll run, or a company with a from and to date.", "VALIDATION");
        if (t < f) throw new DomainException("The to date is before the from date.", "VALIDATION");
        tenant.RequireLegalEntity(le);
        var range = await db.PayrollJournals.Include(j => j.Lines.OrderBy(l => l.Sequence))
            .Where(j => j.LegalEntityId == le && j.Status != JournalStatus.Draft && j.PostingDate >= f && j.PostingDate <= t).OrderBy(j => j.PostingDate).ThenBy(j => j.CreatedAt).ToListAsync(ct);
        return new Scope(le, $"{f:yyyyMMdd}-{t:yyyyMMdd}", range);
    }

    private async Task<JournalExportCheck> CheckAsync(JournalExportFormat format, Scope scope, CancellationToken ct)
    {
        var problems = new List<JournalExportProblem>();
        var warnings = new List<string>();
        if (scope.Journals.Count == 0) problems.Add(new("", null, "There are no posted journals here yet. A run's journal is made when the run is finalised."));
        foreach (var j in scope.Journals)
        {
            var dr = j.Lines.Sum(l => l.Debit); var cr = j.Lines.Sum(l => l.Credit);
            if (dr != cr) problems.Add(new(j.Reference, null, $"Doesn't balance: debits {dr:N2}, credits {cr:N2}."));
            foreach (var l in j.Lines.Where(l => string.IsNullOrWhiteSpace(l.AccountCode)))
                problems.Add(new(j.Reference, null, $"Line \"{l.Description}\" has no account. Set it under Organisation, GL account mapping."));
            if (format == JournalExportFormat.SagePastel)
                foreach (var a in j.Lines.Select(l => l.AccountCode).Distinct().Where(a => !string.IsNullOrWhiteSpace(a) && PastelAccount(a) is null))
                    problems.Add(new(j.Reference, a, $"Account '{a}' isn't a Pastel account number (four digits, optionally with a three-digit sub-account, e.g. 6100 or 6100/010)."));
        }
        var already = scope.Journals.Where(j => j.ExternalReference is not null).Select(j => $"{j.Reference} ({j.ExternalReference})").ToList();
        if (already.Count > 0) warnings.Add($"Already exported: {string.Join("; ", already)}. Importing them again would post them twice.");
        var accounts = scope.Journals.SelectMany(j => j.Lines).Select(l => l.AccountCode).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().Order().ToList();
        if (accounts.Count > 0) warnings.Add($"Accounts used: {string.Join(", ", accounts)}. Each must exist in your accounting system{format switch
        {
            JournalExportFormat.QuickBooksOnline => " under that name, or that number with account numbers switched on",
            JournalExportFormat.QuickBooksDesktop => " under exactly that account name",
            JournalExportFormat.SagePastel => " as a general ledger account",
            _ => "",
        }}. Change the codes under Organisation, GL account mapping if they don't.");
        await Task.CompletedTask;
        return new JournalExportCheck(format.ToString(), scope.Journals.Count, scope.Journals.Sum(j => j.Lines.Count),
            scope.Journals.Sum(j => j.Lines.Sum(l => l.Debit)), scope.Journals.Sum(j => j.Lines.Sum(l => l.Credit)), problems.Count == 0, problems, warnings);
    }

    private static string Memo(PayrollJournal j, PayrollJournalLine l) => $"{j.Reference} {l.Description}".Trim();
    private static string Centre(PayrollJournalLine l, IReadOnlyDictionary<Guid, string> centres) => l.CostCentreId is { } c ? centres.GetValueOrDefault(c) ?? "" : "";
    private static string Amount(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>QuickBooks Online journal entry import: one row per line, grouped by Journal No; Class carries the cost centre.</summary>
    public static string QuickBooksOnline(IEnumerable<PayrollJournal> journals, IReadOnlyDictionary<Guid, string> centres)
    {
        var sb = new StringBuilder("Journal No,Journal Date,Account,Debits,Credits,Description,Name,Class\r\n");
        foreach (var j in journals)
            foreach (var l in j.Lines)
                sb.AppendJoin(',', Csv(j.Reference), j.PostingDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), Csv(l.AccountCode),
                    l.Debit > 0 ? Amount(l.Debit) : "", l.Credit > 0 ? Amount(l.Credit) : "", Csv(Memo(j, l)), "", Csv(Centre(l, centres))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// QuickBooks Desktop IIF: one GENERAL JOURNAL transaction per journal, tab-separated; debits positive, credits negative.
    /// Dates are day/month/year, as Kenyan Windows settings expect.
    /// </summary>
    public static string QuickBooksDesktop(IEnumerable<PayrollJournal> journals, IReadOnlyDictionary<Guid, string> centres)
    {
        var sb = new StringBuilder();
        sb.Append("!TRNS\tTRNSID\tTRNSTYPE\tDATE\tACCNT\tCLASS\tAMOUNT\tDOCNUM\tMEMO\r\n");
        sb.Append("!SPL\tSPLID\tTRNSTYPE\tDATE\tACCNT\tCLASS\tAMOUNT\tDOCNUM\tMEMO\r\n");
        sb.Append("!ENDTRNS\r\n");
        foreach (var j in journals)
        {
            var first = true;
            foreach (var l in j.Lines)
            {
                var amount = l.Debit - l.Credit;
                sb.AppendJoin('\t', first ? "TRNS" : "SPL", "", "GENERAL JOURNAL", j.PostingDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    Iif(l.AccountCode), Iif(Centre(l, centres)), Amount(amount), Iif(j.Reference), Iif(Memo(j, l))).Append("\r\n");
                first = false;
            }
            sb.Append("ENDTRNS\r\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Sage Pastel Partner/Xpress batch import, one line per journal line: period, date, G (general ledger), account,
    /// reference, description, amount (debit positive, credit negative), then the tax, cost-code and exchange-rate fields
    /// left at their neutral values. The period counts from the company's first financial month.
    /// </summary>
    public static string SagePastel(IEnumerable<PayrollJournal> journals, int financialYearStartMonth)
    {
        var start = financialYearStartMonth is >= 1 and <= 12 ? financialYearStartMonth : 1;
        var sb = new StringBuilder();
        var n = 0;
        foreach (var j in journals)
        {
            var period = (j.PostingDate.Month - start + 12) % 12 + 1;
            // Pastel's reference holds 8 characters: "PY" + year and month + this journal's number in the file, so each
            // journal stays distinct; the full Ecosys reference leads the description.
            var reference = $"PY{j.PostingDate:yyMM}{++n % 100:00}";
            foreach (var l in j.Lines)
            {
                var amount = l.Debit - l.Credit;
                sb.AppendJoin(',', period.ToString(CultureInfo.InvariantCulture), j.PostingDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), "G", PastelAccount(l.AccountCode),
                    reference, Csv(Cut($"{j.Reference.Replace("PJ-", "")} {l.Description}", 36)), Amount(amount), "0", "0.00", "", "", "", "1", "1", "", "0", "0.00", Amount(amount)).Append("\r\n");
            }
        }
        return sb.ToString();
    }

    /// <summary>Sage 200 Evolution journal batch import: date, reference, description, account, debit, credit, project (cost centre).</summary>
    public static string SageEvolution(IEnumerable<PayrollJournal> journals, IReadOnlyDictionary<Guid, string> centres)
    {
        var sb = new StringBuilder("Date,Reference,Description,Account,Debit,Credit,Project\r\n");
        foreach (var j in journals)
            foreach (var l in j.Lines)
                sb.AppendJoin(',', j.PostingDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), Csv(j.Reference), Csv(l.Description), Csv(l.AccountCode),
                    Amount(l.Debit), Amount(l.Credit), Csv(Centre(l, centres))).Append("\r\n");
        return sb.ToString();
    }

    public static string Generic(IEnumerable<PayrollJournal> journals, IReadOnlyDictionary<Guid, string> centres)
    {
        var sb = new StringBuilder("Journal,Source,Date,Line,Account,Description,Debit,Credit,CostCentre,Currency\r\n");
        foreach (var j in journals)
            foreach (var l in j.Lines)
                sb.AppendJoin(',', Csv(j.Reference), Csv(j.Source), j.PostingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), l.Sequence, Csv(l.AccountCode), Csv(l.Description),
                    Amount(l.Debit), Amount(l.Credit), Csv(Centre(l, centres)), j.CurrencyCode).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>"6100" → "6100000", "6100/010" → "6100010": Pastel's seven-digit account number. Null when it isn't one.</summary>
    public static string? PastelAccount(string code)
    {
        var parts = code.Trim().Split('/', '-', '.');
        if (parts.Length is < 1 or > 2 || parts[0].Length != 4 || !parts[0].All(char.IsAsciiDigit)) return null;
        var sub = parts.Length == 2 ? parts[1] : "000";
        if (sub.Length is < 1 or > 3 || !sub.All(char.IsAsciiDigit)) return null;
        return parts[0] + sub.PadLeft(3, '0');
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private static string Csv(string s)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@') s = "'" + s; // a spreadsheet would run it as a formula
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    private static string Iif(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Replace("\"", "");

    private static string Safe(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
}

public sealed class JournalExportNotReadyException(JournalExportCheck check) : DomainException("The journals have problems to fix first.", "EXPORT_NOT_READY")
{
    public JournalExportCheck Check { get; } = check;
}
