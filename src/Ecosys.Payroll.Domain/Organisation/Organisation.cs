using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Domain.Organisation;

public class Tenant : Entity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string DefaultCurrency { get; set; } = "KES";
    public string DefaultJurisdiction { get; set; } = "KE";
    public bool IsActive { get; set; } = true;
}

public class LegalEntity : TenantEntity
{
    public required string Name { get; set; }
    public required string RegistrationNumber { get; set; }
    public required string TaxPin { get; set; }
    public string? NssfEmployerNumber { get; set; }
    public string? ShifEmployerNumber { get; set; }
    public string? NitaRegistrationNumber { get; set; }
    public string Jurisdiction { get; set; } = "KE";
    public string CurrencyCode { get; set; } = "KES";
    public string? Address { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    /// <summary>Month (1-12) in which the financial year starts.</summary>
    public int FinancialYearStartMonth { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public PayrollSettings Settings { get; set; } = new();
}

public enum LetterheadLayout
{
    /// <summary>Logo (optional) on the left, company name and details on the right, a rule in the brand colour.</summary>
    Standard,
    /// <summary>The company's own designed header artwork across the top of the page.</summary>
    HeaderImage,
}

/// <summary>
/// How a company's documents are headed: letters (offer, certificate of service, final dues) and payslips. One per legal
/// entity; without one, documents use a plain text letterhead from the legal entity's details.
/// </summary>
public class Letterhead : LegalEntityScopedEntity
{
    public LetterheadLayout Layout { get; set; } = LetterheadLayout.Standard;
    /// <summary>Stored files (owner type Letterhead). PNG or JPG.</summary>
    public Guid? LogoFileId { get; set; }
    public Guid? HeaderImageFileId { get; set; }
    /// <summary>#RRGGBB, used for the company name and rules.</summary>
    public string? BrandColour { get; set; }
    public string? Tagline { get; set; }
    public string? Website { get; set; }
    /// <summary>e.g. "P.O. Box 12345-00100, Nairobi".</summary>
    public string? PostalAddress { get; set; }
    /// <summary>Small print at the foot of every letter, e.g. registered office or directors.</summary>
    public string? FooterText { get; set; }
    public bool ShowRegistrationDetails { get; set; } = true;
}

/// <summary>Per-legal-entity payroll behaviour. Stored as jsonb.</summary>
public sealed class PayrollSettings
{
    /// <summary>
    /// One person may prepare, review and approve (small employers with a single payroll officer). Also lets them approve
    /// their own post-approval adjustments, exit final dues and dependant/beneficiary records. Self-approvals are flagged in the audit log.
    /// </summary>
    public bool AllowPreparerToApprove { get; set; }
    /// <summary>A payroll reviewer signs off a submitted run before anyone can approve it.</summary>
    public bool RequireReviewBeforeApproval { get; set; } = true;
    /// <summary>Different approvers needed for each submission (1–5).</summary>
    public int MinimumApprovals { get; set; } = 1;

    public const string WhereToChange = "An administrator can change the approval steps under Organisation → Legal entities → Edit → Approval workflow.";
    public decimal SalaryVarianceWarningPercent { get; set; } = 20m;
    public string PayslipFooter { get; set; } = "This is a computer-generated payslip.";

    /// <summary>Standard hours per month used to derive the hourly rate for overtime (45h x 52 / 12 = 195).</summary>
    public decimal StandardMonthlyHours { get; set; } = 195m;
    /// <summary>Working days per month used to value unpaid absence days.</summary>
    public decimal StandardMonthlyWorkingDays { get; set; } = 22m;
    public decimal OvertimeMultiplier { get; set; } = 1.5m;
    public decimal RestDayOvertimeMultiplier { get; set; } = 2.0m;
    /// <summary>Notify employees by email/WhatsApp when payslips are issued.</summary>
    public bool NotifyEmployeesOnPayslip { get; set; } = true;
    /// <summary>
    /// Lets the person who entered an employee's bank details also verify them. Off by default: changing pay
    /// to a fraudster's account is the classic payroll fraud, so a second person should check.
    /// </summary>
    public bool AllowBankDetailSelfVerification { get; set; }
}

public class Branch : LegalEntityScopedEntity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Location { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Department : LegalEntityScopedEntity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public Guid? ParentDepartmentId { get; set; }
    public Guid? ManagerEmployeeId { get; set; }
    public Guid? DefaultCostCentreId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class CostCentre : LegalEntityScopedEntity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? GlSegment { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum PayFrequency { Monthly, SemiMonthly, Weekly }

public class PayrollGroup : LegalEntityScopedEntity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public PayFrequency Frequency { get; set; } = PayFrequency.Monthly;
    /// <summary>Day of month employees in this group are normally paid.</summary>
    public int PayDay { get; set; } = 28;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum PeriodStatus { Open, Closed }

public class PayrollPeriod : LegalEntityScopedEntity
{
    public Guid PayrollGroupId { get; set; }
    public required string Name { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public DateOnly PayDate { get; set; }
    public PeriodStatus Status { get; set; } = PeriodStatus.Open;
}
