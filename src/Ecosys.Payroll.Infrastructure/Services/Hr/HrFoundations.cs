using System.Security.Cryptography;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Finance;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Hr;

/// <summary>Who may act on an employee's HR records: themselves, their line manager, or HR with the permission.</summary>
public sealed class HrAccess(PayrollDbContext db, ITenantContext tenant)
{
    public bool IsSelf(Guid employeeId) => tenant.EmployeeId == employeeId;

    public async Task<bool> IsLineManagerOfAsync(Guid employeeId, CancellationToken ct = default) =>
        tenant.EmployeeId is { } me && await db.Employees.AnyAsync(e => e.Id == employeeId && e.ReportingManagerId == me, ct);

    public Task<List<Guid>> DirectReportsAsync(CancellationToken ct = default) =>
        tenant.EmployeeId is { } me ? db.Employees.Where(e => e.ReportingManagerId == me).Select(e => e.Id).ToListAsync(ct) : Task.FromResult(new List<Guid>());

    /// <summary>Self, line manager, or the given HR permission.</summary>
    public async Task RequireSelfManagerOrAsync(Guid employeeId, string permission, CancellationToken ct = default)
    {
        if (IsSelf(employeeId) || tenant.Has(permission) || await IsLineManagerOfAsync(employeeId, ct)) return;
        throw new ForbiddenException("You can only act on your own records, your direct reports, or with HR permission.");
    }

    /// <summary>Line manager or the given HR permission (not self).</summary>
    public async Task RequireManagerOrAsync(Guid employeeId, string permission, CancellationToken ct = default)
    {
        if (IsSelf(employeeId) && !tenant.IsSystem) throw new ForbiddenException("You cannot approve or assess your own record.");
        if (tenant.Has(permission) || await IsLineManagerOfAsync(employeeId, ct)) return;
        throw new ForbiddenException("Only the employee's line manager or HR can do this.");
    }
}

/// <summary>Database-backed file storage with type and size limits. Content is never served without an access check.</summary>
public sealed class FileStore(PayrollDbContext db, ITenantContext tenant)
{
    public const long MaxBytes = 5 * 1024 * 1024;
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [".csv"] = "text/csv", [".txt"] = "text/plain",
    };

    public async Task<StoredFile> SaveAsync(string fileName, byte[] content, string ownerType, Guid? ownerId, Guid? employeeId, CancellationToken ct = default)
    {
        if (content.Length == 0) throw new DomainException("The file is empty.", "VALIDATION");
        if (content.Length > MaxBytes) throw new DomainException($"Files are limited to {MaxBytes / 1024 / 1024} MB.", "VALIDATION");
        var ext = Path.GetExtension(fileName);
        if (!Allowed.TryGetValue(ext, out var type)) throw new DomainException($"File type '{ext}' is not allowed. Use PDF, image, Word, Excel or CSV.", "VALIDATION");
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) && !(content.Length > 4 && content[0] == '%' && content[1] == 'P' && content[2] == 'D' && content[3] == 'F'))
            throw new DomainException("The file is not a valid PDF.", "VALIDATION");
        var f = new StoredFile
        {
            TenantId = tenant.RequireTenant(), FileName = Path.GetFileName(fileName), ContentType = type, Size = content.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)), Content = content, OwnerType = ownerType, OwnerId = ownerId, EmployeeId = employeeId,
        };
        db.StoredFiles.Add(f);
        await db.SaveChangesAsync(ct);
        return f;
    }

    public Task<StoredFile?> FindAsync(Guid id, CancellationToken ct = default) => db.StoredFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
}

/// <summary>
/// Working-day calendar per legal entity: weekends and gazetted public holidays (Public Holidays Act, Cap 110).
/// A holiday on a Sunday is observed on the next day that is not already a holiday.
/// </summary>
public sealed class WorkCalendar(PayrollDbContext db)
{
    public async Task<HashSet<DateOnly>> HolidaysAsync(Guid legalEntityId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        (await db.PublicHolidays.Where(h => h.LegalEntityId == legalEntityId && h.ObservedDate >= from && h.ObservedDate <= to).Select(h => h.ObservedDate).ToListAsync(ct)).ToHashSet();

    public static IEnumerable<DateOnly> WorkingDates(DateOnly from, DateOnly to, ISet<DateOnly> holidays)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d)) yield return d;
    }

    public async Task<decimal> WorkingDaysAsync(Guid legalEntityId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        WorkingDates(from, to, await HolidaysAsync(legalEntityId, from, to, ct)).Count();

    /// <summary>Kenyan statutory holidays for a year (Eid dates are gazetted annually and must be added by HR).</summary>
    public static List<(DateOnly Date, string Name)> KenyaHolidays(int year)
    {
        var easter = EasterSunday(year);
        return
        [
            (new DateOnly(year, 1, 1), "New Year's Day"),
            (easter.AddDays(-2), "Good Friday"),
            (easter.AddDays(1), "Easter Monday"),
            (new DateOnly(year, 5, 1), "Labour Day"),
            (new DateOnly(year, 6, 1), "Madaraka Day"),
            (new DateOnly(year, 10, 10), "Mazingira Day"),
            (new DateOnly(year, 10, 20), "Mashujaa Day"),
            (new DateOnly(year, 12, 12), "Jamhuri Day"),
            (new DateOnly(year, 12, 25), "Christmas Day"),
            (new DateOnly(year, 12, 26), "Utamaduni Day"),
        ];
    }

    /// <summary>Applies the Sunday rule: observed on the next day that is neither a Sunday nor already a holiday.</summary>
    public static List<(DateOnly Date, DateOnly Observed, string Name)> WithObservedDates(IEnumerable<(DateOnly Date, string Name)> holidays)
    {
        var list = holidays.OrderBy(h => h.Date).ToList();
        var taken = list.Select(h => h.Date).ToHashSet();
        var result = new List<(DateOnly, DateOnly, string)>();
        foreach (var (date, name) in list)
        {
            var observed = date;
            if (date.DayOfWeek == DayOfWeek.Sunday)
            {
                observed = date.AddDays(1);
                while (taken.Contains(observed) || observed.DayOfWeek == DayOfWeek.Sunday) observed = observed.AddDays(1);
                taken.Add(observed);
            }
            result.Add((date, observed, name));
        }
        return result;
    }

    /// <summary>Gregorian Easter Sunday (anonymous Gregorian algorithm).</summary>
    public static DateOnly EasterSunday(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(y, month, day);
    }

    public async Task<int> SeedKenyaAsync(Guid tenantId, Guid legalEntityId, int year, CancellationToken ct = default)
    {
        var existing = await db.PublicHolidays.IgnoreQueryFilters().Where(h => h.LegalEntityId == legalEntityId && h.Date.Year == year && !h.IsDeleted).Select(h => h.Date).ToListAsync(ct);
        var added = 0;
        foreach (var (date, observed, name) in WithObservedDates(KenyaHolidays(year)))
        {
            if (existing.Contains(date)) continue;
            db.PublicHolidays.Add(new PublicHoliday { TenantId = tenantId, LegalEntityId = legalEntityId, Date = date, ObservedDate = observed, Name = name });
            added++;
        }
        return added;
    }
}

/// <summary>System pay components used by HR modules; created on first use per legal entity.</summary>
public sealed class SystemComponents(PayrollDbContext db)
{
    public static readonly Dictionary<string, (string Name, ComponentType Type, bool Taxable, DeductionTreatment Treatment, string Gl)> Catalogue = new()
    {
        ["NOTICE_PAY"] = ("Pay in lieu of notice", ComponentType.Earning, true, DeductionTreatment.Ordinary, "6170"),
        ["LEAVE_PAY"] = ("Leave pay (untaken days)", ComponentType.Earning, true, DeductionTreatment.Ordinary, "6171"),
        ["SEVERANCE"] = ("Severance pay", ComponentType.Earning, true, DeductionTreatment.Ordinary, "6172"),
        ["BONUS"] = ("Bonus", ComponentType.Earning, true, DeductionTreatment.Ordinary, "6146"),
        ["REIMB"] = ("Expense reimbursement", ComponentType.Earning, false, DeductionTreatment.Ordinary, "6190"),
        ["REIMB_TAXABLE"] = ("Taxable allowance (per diem excess)", ComponentType.Earning, true, DeductionTreatment.Ordinary, "6191"),
        ["LEAVE_RECOVERY"] = ("Leave taken in advance (recovered)", ComponentType.Deduction, false, DeductionTreatment.Ordinary, "6171"),
        ["NOTICE_RECOVERY"] = ("Notice shortfall recovery", ComponentType.Deduction, false, DeductionTreatment.Ordinary, "6170"),
        ["BOND_RECOVERY"] = ("Training bond recovery", ComponentType.Deduction, false, DeductionTreatment.Ordinary, "6180"),
        ["ASSET_RECOVERY"] = ("Unreturned company property", ComponentType.Deduction, false, DeductionTreatment.Ordinary, "1500"),
        ["MEDICAL"] = ("Medical cover (employee share)", ComponentType.Deduction, false, DeductionTreatment.QualifyingInsurancePremium, "2181"),
    };

    public async Task<SalaryComponent> EnsureAsync(Guid tenantId, Guid legalEntityId, string code, CancellationToken ct = default)
    {
        var existing = await db.SalaryComponents.FirstOrDefaultAsync(c => c.LegalEntityId == legalEntityId && c.Code == code, ct)
                       ?? db.ChangeTracker.Entries<SalaryComponent>().Select(e => e.Entity).FirstOrDefault(c => c.LegalEntityId == legalEntityId && c.Code == code);
        if (existing is not null) return existing;
        var (name, type, taxable, treatment, gl) = Catalogue[code];
        var c = new SalaryComponent
        {
            TenantId = tenantId, LegalEntityId = legalEntityId, Code = code, Name = name, Type = type, Method = CalculationMethod.Variable,
            Taxable = taxable, DeductionTreatment = treatment, GlAccountCode = gl, SortOrder = 90,
        };
        db.SalaryComponents.Add(c);
        return c;
    }
}

/// <summary>Balanced non-payroll journals (leave provision, training cost, expenses).</summary>
public static class GeneralJournal
{
    public static PayrollJournal Build(Guid tenantId, Guid legalEntityId, string source, string reference, DateOnly postingDate, string narrative,
        IEnumerable<(string Account, string Description, decimal Debit, decimal Credit, Guid? CostCentreId)> lines)
    {
        var j = new PayrollJournal
        {
            TenantId = tenantId, LegalEntityId = legalEntityId, Source = source, Reference = reference, PostingDate = postingDate,
            Narrative = narrative, Status = JournalStatus.Posted,
        };
        var seq = 0;
        foreach (var (account, description, debit, credit, cc) in lines.Where(l => l.Debit != 0 || l.Credit != 0))
            j.Lines.Add(new PayrollJournalLine { TenantId = tenantId, PayrollJournalId = j.Id, AccountCode = account, Description = description, Debit = debit, Credit = credit, CostCentreId = cc, Sequence = ++seq });
        j.TotalDebit = j.Lines.Sum(l => l.Debit);
        j.TotalCredit = j.Lines.Sum(l => l.Credit);
        if (!j.IsBalanced) throw new InvalidStateException($"Journal {reference} does not balance.");
        if (j.Lines.Count == 0) throw new InvalidStateException("Nothing to post.");
        return j;
    }
}
