using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services;

public sealed record DirectoryBank(string Code, string Name, string? SwiftCode, bool IsPlatform, int BranchCount);
public sealed record DirectoryBranch(string BankCode, string Code, string Name, bool IsPlatform);
public sealed record ResolvedBankDetails(string? BankCode, string? BankName, string? BranchCode, string? BranchName, string AccountNumber);
public sealed record BranchImportResult(int Added, int Updated, IReadOnlyList<string> Errors);

/// <summary>
/// The bank and branch codes payment files route on. Platform rows are shared; a tenant's own row with the same
/// code takes precedence. Account details are checked against it so a typo in a code fails at entry, not at the bank.
/// </summary>
public sealed class BankDirectoryService(PayrollDbContext db, ITenantContext tenant, AuditService audit)
{
    public async Task<IReadOnlyList<DirectoryBank>> ListBanksAsync(CancellationToken ct = default)
    {
        var banks = Effective(await db.BankDirectoryBanks.AsNoTracking().ToListAsync(ct), b => b.Code);
        var branches = Effective(await db.BankDirectoryBranches.AsNoTracking().ToListAsync(ct), b => $"{b.BankCode}|{b.Code}")
            .Where(b => b.IsActive).GroupBy(b => b.BankCode).ToDictionary(g => g.Key, g => g.Count());
        return banks.Where(b => b.IsActive).OrderBy(b => b.Name)
            .Select(b => new DirectoryBank(b.Code, b.Name, b.SwiftCode, b.TenantId is null, branches.GetValueOrDefault(b.Code))).ToList();
    }

    public async Task<IReadOnlyList<DirectoryBranch>> ListBranchesAsync(string bankCode, CancellationToken ct = default) =>
        Effective(await db.BankDirectoryBranches.Where(b => b.BankCode == bankCode).AsNoTracking().ToListAsync(ct), b => b.Code)
            .Where(b => b.IsActive).OrderBy(b => b.Name).Select(b => new DirectoryBranch(b.BankCode, b.Code, b.Name, b.TenantId is null)).ToList();

    /// <summary>
    /// Normalises account details and checks them against the directory. Bank transfers need a listed bank code and,
    /// once that bank's branches are loaded, a listed branch code; names come from the directory, not the request.
    /// Mobile money needs a valid phone number. Throws a validation error listing every problem.
    /// </summary>
    public async Task<ResolvedBankDetails> ResolveAsync(PaymentMethod method, string? bankCode, string? branchCode, string? accountNumber, CancellationToken ct = default)
    {
        var raw = accountNumber?.Trim() ?? "";
        switch (method)
        {
            case PaymentMethod.MobileMoney:
            {
                var phone = KenyaIdentifiers.NormalisePhone(raw);
                if (phone is null) throw new DomainException($"Mobile money number '{raw}' isn't a valid phone number. Use 07XX XXX XXX or 01XX XXX XXX.", "VALIDATION");
                return new ResolvedBankDetails(null, "M-PESA", null, null, phone);
            }
            case PaymentMethod.BankTransfer:
            {
                var problems = new List<string>();
                var number = new string(raw.Where(c => c is not (' ' or '-')).ToArray());
                if (number.Length is < 6 or > 20 || !number.All(char.IsAsciiDigit)) problems.Add("Account number should be 6–20 digits.");
                var code = bankCode?.Trim() ?? "";
                var bank = code.Length == 0 ? null : await FindBankAsync(code, ct);
                if (code.Length == 0) problems.Add("Choose the bank.");
                else if (bank is null) problems.Add($"Bank code '{code}' isn't in the bank directory. Pick the bank from the list, or ask an administrator to add it.");

                string? branchName = null;
                var branch = string.IsNullOrWhiteSpace(branchCode) ? null : branchCode.Trim();
                if (bank is not null)
                {
                    var branches = Effective(await db.BankDirectoryBranches.Where(b => b.BankCode == bank.Code).AsNoTracking().ToListAsync(ct), b => b.Code)
                        .Where(b => b.IsActive).ToList();
                    if (branches.Count > 0)
                    {
                        var match = branch is null ? null : branches.FirstOrDefault(b => b.Code == branch);
                        if (branch is null) problems.Add($"Choose the {bank.Name} branch.");
                        else if (match is null) problems.Add($"Branch code '{branch}' isn't a listed {bank.Name} branch.");
                        else branchName = match.Name;
                    }
                }
                if (problems.Count > 0) throw new DomainException(string.Join(" ", problems), "VALIDATION");
                return new ResolvedBankDetails(bank!.Code, bank.Name, branch, branchName, number);
            }
            default:
                return new ResolvedBankDetails(bankCode?.Trim(), null, branchCode?.Trim(), null, raw);
        }
    }

    public async Task<BankDirectoryBank?> FindBankAsync(string code, CancellationToken ct = default) =>
        Effective(await db.BankDirectoryBanks.Where(b => b.Code == code).ToListAsync(ct), b => b.Code).FirstOrDefault(b => b.IsActive);

    /// <summary>Adds or renames a bank. Platform rows need a platform administrator; otherwise the row is the tenant's own.</summary>
    public async Task<BankDirectoryBank> UpsertBankAsync(string code, string name, string? swiftCode, bool platform, CancellationToken ct = default)
    {
        tenant.Require(Permissions.OrganisationManage);
        code = code?.Trim() ?? "";
        if (code.Length is 0 or > 6 || !code.All(char.IsAsciiDigit)) throw new DomainException("Bank code should be 1–6 digits.", "VALIDATION");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Bank name is required.", "VALIDATION");
        var owner = Owner(platform);
        var row = await db.BankDirectoryBanks.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Code == code && b.TenantId == owner && !b.IsDeleted, ct);
        if (row is null) db.BankDirectoryBanks.Add(row = new BankDirectoryBank { TenantId = owner, Code = code, Name = name.Trim() });
        row.Name = name.Trim();
        row.SwiftCode = string.IsNullOrWhiteSpace(swiftCode) ? null : swiftCode.Trim().ToUpperInvariant();
        row.IsActive = true;
        audit.Record("bank_directory.bank.saved", nameof(BankDirectoryBank), row.Id, new { code, row.Name, platform });
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>
    /// Imports branches from CSV lines of <c>bank code, branch code, branch name</c> (a header row is skipped), as
    /// published in the bankers' association list. Existing rows are renamed; nothing is deleted.
    /// </summary>
    public async Task<BranchImportResult> ImportBranchesAsync(string csv, bool platform, CancellationToken ct = default)
    {
        tenant.Require(Permissions.OrganisationManage);
        var owner = Owner(platform);
        var banks = (await ListBanksAsync(ct)).Select(b => b.Code).ToHashSet();
        var existing = await db.BankDirectoryBranches.IgnoreQueryFilters().Where(b => b.TenantId == owner && !b.IsDeleted).ToDictionaryAsync(b => $"{b.BankCode}|{b.Code}", ct);
        var errors = new List<string>();
        int added = 0, updated = 0, lineNo = 0;
        foreach (var line in (csv ?? "").Split('\n'))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = SplitCsv(line.TrimEnd('\r'));
            if (lineNo == 1 && f.Count > 0 && !f[0].All(char.IsAsciiDigit)) continue; // header
            if (f.Count < 3 || f.Take(3).Any(string.IsNullOrWhiteSpace)) { errors.Add($"Line {lineNo}: expected bank code, branch code, branch name."); continue; }
            var (bank, code, name) = (f[0], f[1], f[2]);
            if (!bank.All(char.IsAsciiDigit) || !code.All(char.IsAsciiDigit)) { errors.Add($"Line {lineNo}: codes should be digits."); continue; }
            if (!banks.Contains(bank)) { errors.Add($"Line {lineNo}: bank code '{bank}' isn't in the directory. Add the bank first."); continue; }
            if (existing.TryGetValue($"{bank}|{code}", out var row))
            {
                if (row.Name != name || !row.IsActive) { row.Name = name; row.IsActive = true; updated++; }
            }
            else
            {
                row = new BankDirectoryBranch { TenantId = owner, BankCode = bank, Code = code, Name = name };
                db.BankDirectoryBranches.Add(row);
                existing[$"{bank}|{code}"] = row;
                added++;
            }
        }
        audit.Record("bank_directory.branches.imported", nameof(BankDirectoryBranch), null, new { added, updated, errors = errors.Count, platform });
        await db.SaveChangesAsync(ct);
        return new BranchImportResult(added, updated, errors);
    }

    private Guid? Owner(bool platform)
    {
        if (!platform) return tenant.RequireTenant();
        if (!tenant.IsPlatformAdministrator) throw new ForbiddenException("Only a platform administrator can change the shared bank directory.");
        return null;
    }

    /// <summary>A tenant's own row replaces the platform row with the same key.</summary>
    private static List<T> Effective<T>(List<T> rows, Func<T, string> key) where T : Entity =>
        rows.GroupBy(key).Select(g => g.OrderByDescending(r => TenantOf(r) is not null).First()).ToList();

    private static Guid? TenantOf(Entity e) => e switch { BankDirectoryBank b => b.TenantId, BankDirectoryBranch b => b.TenantId, _ => null };

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { result.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString().Trim());
        return result;
    }
}
