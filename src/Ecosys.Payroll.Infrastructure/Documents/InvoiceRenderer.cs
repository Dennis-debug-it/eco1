using Ecosys.Payroll.Domain.Billing;
using Ecosys.Payroll.Infrastructure.Services.Saas;

namespace Ecosys.Payroll.Infrastructure.Documents;

/// <summary>Tax invoice PDF for a subscription invoice.</summary>
public static class InvoiceRenderer
{
    public static byte[] Render(SubscriptionInvoice inv, string customerName, SaasOptions seller)
    {
        var pdf = new PdfWriter();
        float left = 50, right = pdf.Width - 50, y = 60;
        pdf.Text(left, y, seller.SellerName, 16, bold: true);
        pdf.Text(right, y, inv.Vat > 0 ? "TAX INVOICE" : "INVOICE", 14, bold: true, align: TextAlign.Right);
        y += 16;
        if (!string.IsNullOrWhiteSpace(seller.SellerAddress)) { pdf.Text(left, y, seller.SellerAddress, 9); y += 12; }
        if (!string.IsNullOrWhiteSpace(seller.SellerTaxPin)) { pdf.Text(left, y, $"KRA PIN: {seller.SellerTaxPin}", 9); y += 12; }
        if (!string.IsNullOrWhiteSpace(seller.SupportEmail)) { pdf.Text(left, y, seller.SupportEmail, 9); }

        y = 110;
        pdf.Text(left, y, "Bill to", 9, bold: true);
        pdf.Text(330, y, "Invoice", 9, bold: true); pdf.Text(right, y, inv.Number, 9, align: TextAlign.Right);
        y += 13;
        pdf.Text(left, y, inv.BillingName ?? customerName, 10, maxWidth: 260);
        pdf.Text(330, y, "Issued", 9); pdf.Text(right, y, inv.IssuedOn.ToString("d MMM yyyy"), 9, align: TextAlign.Right);
        y += 13;
        if (!string.IsNullOrWhiteSpace(inv.BillingTaxPin)) pdf.Text(left, y, $"KRA PIN: {inv.BillingTaxPin}", 9);
        pdf.Text(330, y, "Due", 9); pdf.Text(right, y, inv.DueOn.ToString("d MMM yyyy"), 9, align: TextAlign.Right);
        y += 13;
        pdf.Text(330, y, "Status", 9); pdf.Text(right, y, inv.Status == InvoiceStatus.Open && inv.AmountPaid > 0 ? "Part paid" : inv.Status.ToString(), 9, bold: true, align: TextAlign.Right);

        y = 190;
        pdf.Rect(left, y - 11, right - left, 16, 0.92f);
        pdf.Text(left + 4, y, "Description", 9, bold: true);
        pdf.Text(380, y, "Qty", 9, bold: true, align: TextAlign.Right);
        pdf.Text(460, y, "Unit price", 9, bold: true, align: TextAlign.Right);
        pdf.Text(right - 4, y, $"Amount ({inv.Currency})", 9, bold: true, align: TextAlign.Right);
        y += 20;
        foreach (var l in inv.Lines)
        {
            pdf.Text(left + 4, y, l.Description, 9, maxWidth: 290);
            pdf.Text(380, y, l.Quantity.ToString("0.##"), 9, align: TextAlign.Right);
            pdf.Text(460, y, l.UnitPrice.ToString("N2"), 9, align: TextAlign.Right);
            pdf.Text(right - 4, y, l.Amount.ToString("N2"), 9, align: TextAlign.Right);
            y += 16;
        }
        pdf.Line(left, y - 6, right, y - 6);
        y += 8;
        void Total(string label, decimal v, bool bold = false) { pdf.Text(460, y, label, 9, bold, TextAlign.Right); pdf.Text(right - 4, y, v.ToString("N2"), 9, bold, TextAlign.Right); y += 14; }
        Total("Subtotal", inv.Subtotal);
        Total($"VAT {inv.VatRate * 100:0.##}%", inv.Vat);
        Total("Total", inv.Total, bold: true);
        if (inv.AmountPaid > 0) { Total("Paid", inv.AmountPaid); Total("Balance due", inv.Balance, bold: true); }

        y += 20;
        pdf.Text(left, y, $"Billing period {inv.PeriodStart:d MMM yyyy} – {inv.PeriodEnd:d MMM yyyy}. {inv.BillableEmployees} employees on the books when issued.", 8);
        y += 18;
        pdf.Text(left, y, "How to pay", 9, bold: true);
        y += 13;
        foreach (var line in Wrap(seller.PaymentInstructions + $" Use {inv.Number} as the payment reference.", 95)) { pdf.Text(left, y, line, 9); y += 12; }
        return pdf.ToBytes();
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length + word.Length + 1 > width) { yield return line; line = word; }
            else line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) yield return line;
    }
}
