using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecosys.Payroll.Infrastructure.Services.Migration;

public sealed record CompanyExport(byte[] Zip, string FileName, IReadOnlyDictionary<string, int> Counts);

/// <summary>
/// Everything an organisation keeps in Ecosys, as a zip a person or another payroll system can read: one CSV per table
/// (decrypted, the way the organisation entered it), the files people uploaded, and every issued payslip as a PDF.
/// For a rollback after go-live, a customer leaving, or a data-protection request covering the whole organisation.
/// Built from the database model, so a table added later is exported without anyone remembering to add it here.
/// Never included: password hashes, authenticator secrets, sign-in tokens, and the notification outbox.
/// </summary>
public sealed class CompanyExportService(PayrollDbContext db, ITenantContext tenant, AuditService audit, PayslipPdfService payslips)
{
    /// <summary>Tables that are machinery, not the organisation's records.</summary>
    private static readonly HashSet<string> SkippedTables = ["NotificationMessage"];

    /// <summary>Columns that are credentials, not data. Exporting them would hand out a way in.</summary>
    private static readonly HashSet<string> SecretColumns = ["PasswordHash", "MfaSecret", "SecurityStamp", "EmailVerificationTokenHash", "PasswordResetTokenHash", "LastTotpStep", "KeyHash", "Secret"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly MethodInfo SetMethod = typeof(DbContext).GetMethods().Single(m => m.Name == nameof(DbContext.Set) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);

    public async Task<CompanyExport> BuildAsync(bool includePayslips, CancellationToken ct = default)
    {
        tenant.Require(Permissions.OrganisationManage);
        tenant.Require(Permissions.SalaryRead);
        if (tenant.LegalEntityIds.Count > 0) throw new ForbiddenException("Exporting the whole organisation needs access to every company in it.");
        var tenantId = tenant.RequireTenant();
        var org = await db.Tenants.Where(t => t.Id == tenantId).Select(t => new { t.Name, t.Slug }).FirstAsync(ct);
        var counts = new SortedDictionary<string, int>();
        var at = DateTimeOffset.UtcNow;

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var et in db.Model.GetEntityTypes()
                         .Where(t => !t.IsOwned() && t.GetTableName() is not null && typeof(TenantEntity).IsAssignableFrom(t.ClrType) && !SkippedTables.Contains(t.ClrType.Name))
                         .OrderBy(t => t.GetTableName()))
            {
                var rows = await LoadAsync(et.ClrType, ct);
                if (rows.Count == 0) continue;
                var name = et.GetTableName()!;
                Write(zip, $"data/{name}.csv", Csv(et, rows));
                counts[name] = rows.Count;
            }

            // Uploaded documents (contracts, certificates, letterheads…) as the files themselves.
            var files = await db.Set<StoredFile>().AsNoTracking().ToListAsync(ct);
            foreach (var f in files)
            {
                var entry = zip.CreateEntry($"files/{f.Id:N}-{Safe(f.FileName)}", CompressionLevel.Fastest);
                await using var s = entry.Open();
                await s.WriteAsync(f.Content, ct);
            }
            if (files.Count > 0) counts["files"] = files.Count;

            if (includePayslips)
            {
                var slips = await db.Payslips.OrderBy(p => p.PeriodEnd).ThenBy(p => p.PayslipNumber).ToListAsync(ct);
                foreach (var slip in slips)
                {
                    var pdf = await payslips.RenderAsync(slip, ct);
                    var entry = zip.CreateEntry($"payslips/{slip.PeriodEnd:yyyy-MM}/{Safe(slip.PayslipNumber)}.pdf", CompressionLevel.NoCompression);
                    await using var s = entry.Open();
                    await s.WriteAsync(pdf, ct);
                }
                if (slips.Count > 0) counts["payslips"] = slips.Count;
            }

            Write(zip, "README.txt", Readme(org.Name, at, counts, includePayslips));
        }

        audit.Record("company.export.downloaded", "Tenant", tenantId, new { tables = counts.Count, rows = counts.Where(c => c.Key is not ("files" or "payslips")).Sum(c => c.Value), payslips = counts.GetValueOrDefault("payslips"), files = counts.GetValueOrDefault("files") });
        await db.SaveChangesAsync(ct); // the audit row, and balances captured on payslips rendered for the first time
        return new CompanyExport(ms.ToArray(), $"{org.Slug}-export-{BusinessClock.ToLocal(at):yyyyMMdd-HHmm}.zip", counts);
    }

    private async Task<List<object>> LoadAsync(Type type, CancellationToken ct)
    {
        // db.Set<T>() goes through the tenant and soft-delete filters, so only this organisation's live rows come back.
        var set = (IQueryable)SetMethod.MakeGenericMethod(type).Invoke(db, null)!;
        var noTracking = typeof(EntityFrameworkQueryableExtensions).GetMethod(nameof(EntityFrameworkQueryableExtensions.AsNoTracking))!.MakeGenericMethod(type);
        var query = (IQueryable)noTracking.Invoke(null, [set])!;
        var list = new List<object>();
        await foreach (var row in ((IAsyncEnumerable<object>)query).WithCancellation(ct)) list.Add(row);
        return list;
    }

    private static string Csv(IEntityType et, List<object> rows)
    {
        var props = et.GetProperties().Where(p => p.PropertyInfo is not null && !SecretColumns.Contains(p.Name) && p.ClrType != typeof(byte[])).ToList();
        var owned = et.GetNavigations().Where(n => n.TargetEntityType.IsOwned() && n.PropertyInfo is not null).ToList();
        var sb = new StringBuilder();
        sb.AppendJoin(',', props.Select(p => p.Name).Concat(owned.Select(n => n.Name))).Append("\r\n");
        foreach (var row in rows)
        {
            sb.AppendJoin(',', props.Select(p => Cell(p.PropertyInfo!.GetValue(row))).Concat(owned.Select(n => Cell(n.PropertyInfo!.GetValue(row)))));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Cell(object? v)
    {
        var s = v switch
        {
            null => "",
            string x => x,
            DateTimeOffset x => x.ToString("O", CultureInfo.InvariantCulture),
            DateTime x => x.ToString("O", CultureInfo.InvariantCulture),
            DateOnly x => x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            decimal x => x.ToString(CultureInfo.InvariantCulture),
            bool x => x ? "true" : "false",
            Enum x => x.ToString(),
            Guid x => x.ToString(),
            IFormattable x when v.GetType().IsPrimitive => x.ToString(null, CultureInfo.InvariantCulture),
            _ => JsonSerializer.Serialize(v, v.GetType(), Json), // lists, settings and other structured values
        };
        // Quote what needs it; text starting = + - @ is prefixed so a spreadsheet shows it rather than running it as a formula.
        if (v is string && s.Length > 0 && s[0] is '=' or '+' or '-' or '@') s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    private static string Safe(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return cleaned.Length > 80 ? cleaned[^80..] : cleaned;
    }

    private static void Write(ZipArchive zip, string path, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(true));
        w.Write(text);
    }

    private static string Readme(string org, DateTimeOffset at, IDictionary<string, int> counts, bool payslips) => $"""
        {org}: export from Ecosys Payroll
        Made {BusinessClock.ToLocal(at):dd MMM yyyy HH:mm} {BusinessClock.Abbreviation}.

        data/      One CSV per table, UTF-8, first row = column names. Ids link the tables: EmployeeId in any
                   table is the Id in data/employees.csv. Values are as the organisation entered them (account
                   numbers, KRA PINs and ID numbers in full), so store this file as securely as the payroll itself.
                   Dates are ISO 8601; amounts use a dot for decimals and no thousands separator.
        files/     Documents people uploaded (contracts, certificates, letterheads), named <file id>-<name>.
        payslips/  {(payslips ? "Every issued payslip as a PDF, by pay month." : "Not included in this export.")}

        Not included: password hashes, authenticator secrets, sign-in tokens, and the queue of outgoing emails.

        Contents:
        {string.Join("\n", counts.Select(c => $"  {c.Key,-40} {c.Value,8:N0}"))}
        """;
}
