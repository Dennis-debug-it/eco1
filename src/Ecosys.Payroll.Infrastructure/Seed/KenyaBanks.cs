namespace Ecosys.Payroll.Infrastructure.Seed;

/// <summary>
/// Kenyan bank clearing codes seeded as platform rows of the bank directory. Banks merge, rename and close, so this
/// is a starting list: check it against the Kenya Bankers Association's current bank and branch code list before
/// production, and load branch codes from that list (Organisation → Banks → Import).
/// </summary>
public static class KenyaBanks
{
    public static readonly IReadOnlyList<(string Code, string Name)> All =
    [
        ("01", "KCB Bank Kenya"),
        ("02", "Standard Chartered Bank Kenya"),
        ("03", "Absa Bank Kenya"),
        ("05", "Bank of India"),
        ("06", "Bank of Baroda (Kenya)"),
        ("07", "NCBA Bank Kenya"),
        ("10", "Prime Bank"),
        ("11", "Co-operative Bank of Kenya"),
        ("12", "National Bank of Kenya"),
        ("16", "Citibank N.A. Kenya"),
        ("17", "Habib Bank AG Zurich"),
        ("18", "Middle East Bank Kenya"),
        ("19", "Bank of Africa Kenya"),
        ("23", "Consolidated Bank of Kenya"),
        ("25", "Credit Bank"),
        ("26", "Access Bank Kenya"),
        ("31", "Stanbic Bank Kenya"),
        ("35", "African Banking Corporation"),
        ("43", "Ecobank Kenya"),
        ("50", "Paramount Bank"),
        ("51", "Kingdom Bank"),
        ("53", "Guaranty Trust Bank Kenya"),
        ("54", "Victoria Commercial Bank"),
        ("55", "Guardian Bank"),
        ("57", "I&M Bank"),
        ("59", "Development Bank of Kenya"),
        ("60", "SBM Bank Kenya"),
        ("61", "HFC"),
        ("63", "Diamond Trust Bank Kenya"),
        ("66", "Sidian Bank"),
        ("68", "Equity Bank Kenya"),
        ("70", "Family Bank"),
        ("72", "Gulf African Bank"),
        ("74", "First Community Bank"),
        ("75", "DIB Bank Kenya"),
        ("76", "UBA Kenya Bank"),
    ];

    /// <summary>The bank whose account a bank-specific bulk format must be debited from.</summary>
    public static string? FormatBankCode(Domain.Payroll.PaymentFileFormat format) => format switch
    {
        Domain.Payroll.PaymentFileFormat.KcbBulk => "01",
        Domain.Payroll.PaymentFileFormat.CoopBulk => "11",
        Domain.Payroll.PaymentFileFormat.EquityBulk => "68",
        _ => null,
    };
}
