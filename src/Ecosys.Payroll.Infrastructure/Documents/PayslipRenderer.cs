using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Infrastructure.Security;

namespace Ecosys.Payroll.Infrastructure.Documents;

/// <summary>
/// Renders the PRD §6 payslip layout to PDF. Bank account numbers are masked. Shows the employer's statutory
/// contributions but not the employer's total cost of employment, which is for payroll and finance only.
/// </summary>
public static class PayslipRenderer
{
    public static byte[] Render(LegalEntity entity, Employee employee, PayrollRunEmployee result, PayrollRun run, PayrollPeriod period,
        Payslip payslip, EmployeeBankAccount? bank, string? department, string? kraPinMasked, decimal? nssfToDate = null, IReadOnlyList<PayslipLeaveBalance>? leave = null, Brand? brand = null)
    {
        brand ??= Brand.Plain;
        var pdf = new PdfWriter();
        const float left = 40, right = PdfWriter.A4Width - 40;
        var y = 48f;

        // Compact letterhead: the company's header artwork, or its logo beside the name.
        var nameX = left;
        if (brand.Layout == LetterheadLayout.HeaderImage && brand.Header is { } art)
        {
            var h = Math.Min((right - left) / art.Aspect, 70f);
            pdf.Image(art, left, 26, h * art.Aspect, h);
            y = 26 + h + 20;
        }
        else if (brand.Logo is { } logo)
        {
            var h = Math.Min(34f, 110f / logo.Aspect);
            pdf.Image(logo, left, 28, h * logo.Aspect, h);
            nameX = left + h * logo.Aspect + 10;
            y = Math.Max(48f, 28 + h / 2 + 5);
        }
        pdf.Text(nameX, y, entity.Name, 16, bold: true, maxWidth: right - nameX - 110, color: brand.TextColour);
        pdf.Text(right, y, "PAYSLIP", 16, bold: true, TextAlign.Right);
        y += 16;
        pdf.Text(nameX, y, $"PIN {entity.TaxPin}   {entity.Address ?? ""}", 8.5f, maxWidth: right - nameX - 150);
        pdf.Text(right, y, payslip.PayslipNumber, 9, align: TextAlign.Right);
        y += 12;
        pdf.Text(right, y, $"Period {period.PeriodStart:dd MMM yyyy} – {period.PeriodEnd:dd MMM yyyy}", 9, align: TextAlign.Right);
        y += 10;
        if (brand.Colour is { } rule) pdf.Line(left, y, right, y, 1.2f, rule); else pdf.Line(left, y, right, y, 1);
        y += 14;

        // Employee block
        var col2 = left + 270;
        void Kv(float x, float yy, string k, string v) { pdf.Text(x, yy, k, 8.5f); pdf.Text(x + 95, yy, v, 9, bold: true, maxWidth: 160); }
        Kv(left, y, "Employee", employee.FullName); Kv(col2, y, "Employee No.", employee.EmployeeNumber); y += 13;
        Kv(left, y, "Job title", employee.JobTitle ?? "—"); Kv(col2, y, "Department", department ?? "—"); y += 13;
        Kv(left, y, "KRA PIN", kraPinMasked ?? "—"); Kv(col2, y, "Pay date", period.PayDate.ToString("dd MMM yyyy")); y += 13;
        Kv(left, y, "Payment", bank is null ? "—" : $"{bank.BankName ?? bank.Method.ToString()} {FieldEncryptor.Mask(bank.AccountNumber)}"); Kv(col2, y, "Currency", run.CurrencyCode); y += 18;

        var lines = result.Lines.OrderBy(l => l.Sequence).ToList();
        var earnings = lines.Where(l => l.Type == ResultLineType.Earning).ToList();
        var deductions = lines.Where(l => l.Type is ResultLineType.StatutoryDeduction or ResultLineType.Deduction).ToList();
        var employer = lines.Where(l => l.Type == ResultLineType.EmployerContribution).ToList();

        // Two-column table: earnings | deductions
        var mid = left + (right - left) / 2;
        var top = y;
        y = Section(pdf, left, mid - 8, top, "EARNINGS", earnings, result.GrossPay, "Gross pay");
        var y2 = Section(pdf, mid + 8, right, top, "DEDUCTIONS", deductions, result.TotalEmployeeDeductions, "Total deductions");
        y = Math.Max(y, y2) + 10;

        pdf.Rect(left, y, right - left, 22, 0.92f);
        pdf.Text(left + 8, y + 15, "NET PAY", 11, bold: true);
        pdf.Text(right - 8, y + 15, $"{run.CurrencyCode} {result.NetPay:N2}", 12, bold: true, TextAlign.Right);
        y += 36;

        // Tax computation summary
        pdf.Text(left, y, "TAX COMPUTATION", 9, bold: true); y += 12;
        foreach (var (k, v) in new[]
        {
            ("Taxable earnings", result.TaxableEarnings), ("Allowable deductions", result.TaxableEarnings - result.TaxablePay),
            ("Taxable pay", result.TaxablePay), ("Tax on taxable pay", result.GrossTax), ("Reliefs", result.TotalReliefs), ("PAYE", result.Paye),
        })
        {
            pdf.Text(left, y, k, 8.5f); pdf.Text(left + 250, y, v.ToString("N2"), 8.5f, align: TextAlign.Right); y += 11;
        }

        if (employer.Count > 0)
        {
            var ex = mid + 8;
            var start = y - 11 * 7;
            pdf.Text(ex, start, "EMPLOYER CONTRIBUTIONS", 9, bold: true);
            var yy = start + 12;
            foreach (var l in employer) { pdf.Text(ex, yy, l.Name, 8.5f, maxWidth: 170); pdf.Text(right, yy, l.Amount.ToString("N2"), 8.5f, align: TextAlign.Right); yy += 11; }
            y = Math.Max(y, yy);
        }

        // Running balances.
        if (nssfToDate is not null || leave is { Count: > 0 })
        {
            y += 10;
            pdf.Text(left, y, "BALANCES", 9, bold: true); y += 12;
            if (nssfToDate is { } nssf)
            {
                pdf.Text(left, y, "NSSF contributions to date (employee + employer)", 8.5f); pdf.Text(left + 250, y, nssf.ToString("N2"), 8.5f, bold: true, TextAlign.Right); y += 11;
            }
            foreach (var l in leave ?? [])
            {
                pdf.Text(left, y, $"{l.LeaveType} remaining", 8.5f, maxWidth: 180); pdf.Text(left + 250, y, $"{l.DaysRemaining:0.##} days", 8.5f, bold: true, TextAlign.Right); y += 11;
            }
            if (nssfToDate is not null)
            {
                pdf.Text(left, y + 2, "NSSF: contributions recorded by your employer. Your NSSF member statement is the official balance.", 7f);
                y += 11;
            }
        }

        y += 14;
        pdf.Line(left, y, right, y, 0.5f, 0.6f); y += 12;
        pdf.Text(left, y, entity.Settings.PayslipFooter, 7.5f);
        pdf.Text(right, y, $"Issued {BusinessClock.ToLocal(payslip.IssuedAt):dd MMM yyyy HH:mm} {BusinessClock.Abbreviation} · Run {run.Reference}", 7.5f, align: TextAlign.Right);
        return pdf.ToBytes();
    }

    private static float Section(PdfWriter pdf, float x1, float x2, float y, string title, List<PayrollResultLineRecord> rows, decimal total, string totalLabel)
    {
        pdf.Rect(x1, y, x2 - x1, 16, 0.85f);
        pdf.Text(x1 + 6, y + 11.5f, title, 9, bold: true);
        pdf.Text(x2 - 6, y + 11.5f, "Amount", 8, align: TextAlign.Right);
        y += 16;
        foreach (var r in rows)
        {
            y += 12;
            pdf.Text(x1 + 6, y, r.Name, 8.5f, maxWidth: x2 - x1 - 90);
            pdf.Text(x2 - 6, y, r.Amount.ToString("N2"), 8.5f, align: TextAlign.Right);
        }
        y += 6;
        pdf.Line(x1, y, x2, y, 0.5f);
        y += 12;
        pdf.Text(x1 + 6, y, totalLabel, 9, bold: true);
        pdf.Text(x2 - 6, y, total.ToString("N2"), 9, bold: true, TextAlign.Right);
        return y;
    }
}
