using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Domain.Organisation;

/// <summary>
/// A bank in the clearing directory. Platform rows (TenantId null) are shared by every tenant; a tenant may add
/// its own (a SACCO, a newly licensed bank) or override a platform row with the same code.
/// </summary>
public class BankDirectoryBank : Entity
{
    public Guid? TenantId { get; set; }
    /// <summary>Clearing code, e.g. "01" for KCB. Compared as text, so keep leading zeros.</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? SwiftCode { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A branch in the clearing directory. Once a bank has any branches listed, branch codes are checked against them.</summary>
public class BankDirectoryBranch : Entity
{
    public Guid? TenantId { get; set; }
    public required string BankCode { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// The company's own account that a payment batch is debited from. Bank bulk formats need it in the file,
/// and the bank's bulk-payment agreement names it.
/// </summary>
public class CompanyBankAccount : LegalEntityScopedEntity
{
    public required string Label { get; set; }
    public required string BankCode { get; set; }
    public string? BankName { get; set; }
    public string? BranchCode { get; set; }
    public string? BranchName { get; set; }
    public required string AccountName { get; set; }
    /// <summary>Encrypted at rest.</summary>
    public required string AccountNumber { get; set; }
    public string CurrencyCode { get; set; } = "KES";
    /// <summary>Used when a payment file is generated without choosing an account.</summary>
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}
