using System.Security.Cryptography;
using System.Text;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Domain.Payroll;

namespace Ecosys.Payroll.Infrastructure.Services;

public sealed record PaymentFile(string FileName, byte[] Content, string Hash, string ContentType);

/// <summary>
/// Downloadable bank-format writers (PRD §7: file-based first). Each format is a simple column layout
/// agreed with the bank; direct API integrations come later. Account numbers are written in full here
/// because the file goes to the bank — the API restricts who can download it.
/// </summary>
public static class PaymentFileWriter
{
    public static PaymentFile Write(PaymentFileFormat format, LegalEntity entity, PaymentBatch batch, IReadOnlyList<PayrollPayment> payments, PayrollPeriod period)
    {
        var sb = new StringBuilder();
        var narrative = $"SALARY {period.PeriodEnd:MMM yyyy}".ToUpperInvariant();
        switch (format)
        {
            case PaymentFileFormat.GenericCsv:
                sb.AppendLine("PaymentReference,EmployeeAccountName,BankCode,BranchCode,AccountNumber,Amount,Currency,Narrative,ValueDate");
                foreach (var p in payments)
                    sb.AppendLine(Csv(p.Reference, p.AccountName, p.BankCode, p.BranchCode, p.AccountNumber, p.Amount.ToString("0.00"), p.CurrencyCode, narrative, batch.ValueDate.ToString("yyyy-MM-dd")));
                break;

            case PaymentFileFormat.KcbBulk:
                // Debit account | Beneficiary name | Beneficiary account | Bank code | Branch code | Amount | Narrative | Reference
                foreach (var p in payments)
                    sb.AppendLine(Csv(entity.RegistrationNumber, p.AccountName, p.AccountNumber, p.BankCode, p.BranchCode, p.Amount.ToString("0.00"), narrative, p.Reference));
                break;

            case PaymentFileFormat.EquityBulk:
                sb.AppendLine("Beneficiary Name,Account Number,Bank Code,Branch Code,Amount,Payment Details,Reference");
                foreach (var p in payments)
                    sb.AppendLine(Csv(p.AccountName, p.AccountNumber, p.BankCode, p.BranchCode, p.Amount.ToString("0.00"), narrative, p.Reference));
                break;

            case PaymentFileFormat.CoopBulk:
                sb.AppendLine("ACCOUNT_NO,ACCOUNT_NAME,BANK_CODE,BRANCH_CODE,AMOUNT,NARRATION");
                foreach (var p in payments)
                    sb.AppendLine(Csv(p.AccountNumber, p.AccountName, p.BankCode, p.BranchCode, p.Amount.ToString("0.00"), narrative));
                break;

            case PaymentFileFormat.MpesaB2C:
                sb.AppendLine("MSISDN,Name,Amount,Remarks,Occasion");
                foreach (var p in payments)
                    sb.AppendLine(Csv(p.AccountNumber, p.AccountName, p.Amount.ToString("0"), narrative, p.Reference));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new PaymentFile($"{batch.Reference}-{format}.csv", bytes, hash, "text/csv");
    }

    private static string Csv(params string?[] fields) => string.Join(',', fields.Select(f =>
    {
        var v = f ?? "";
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }));
}
