using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Domain.Employees;

public enum EmploymentType { Permanent, Contract, Casual, Intern, Probation, Expatriate }
public enum EmploymentStatus { Active, OnLeave, Suspended, Terminated, Resigned, Retired, Deceased }
public enum Gender { Unspecified, Female, Male, Other }
public enum PaymentMethod { BankTransfer, MobileMoney, Cash, Cheque }

public class Employee : LegalEntityScopedEntity
{
    public required string EmployeeNumber { get; set; }
    public required string FirstName { get; set; }
    public string? MiddleName { get; set; }
    public required string LastName { get; set; }
    public string? NationalId { get; set; }
    public string? PassportNumber { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public Gender Gender { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? PhysicalAddress { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    /// <summary>Set when personal data was removed after the statutory retention period (Data Protection Act s.39).</summary>
    public DateTimeOffset? AnonymisedAt { get; set; }

    // Current employment (history lives in EmploymentRecord)
    public EmploymentType EmploymentType { get; set; } = EmploymentType.Permanent;
    public EmploymentStatus Status { get; set; } = EmploymentStatus.Active;
    public DateOnly HireDate { get; set; }
    public DateOnly? ContractEndDate { get; set; }
    public DateOnly? TerminationDate { get; set; }
    public string? JobTitle { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? CostCentreId { get; set; }
    public Guid PayrollGroupId { get; set; }
    public Guid? ReportingManagerId { get; set; }
    public string? WorkLocation { get; set; }
    public Guid? PositionId { get; set; }
    /// <summary>Contractual notice period in days (Employment Act s.35 minimum: 28 days for monthly-paid staff).</summary>
    public int NoticePeriodDays { get; set; } = 30;
    public DateOnly? ProbationEndDate { get; set; }

    /// <summary>Linked platform user for self-service, if any.</summary>
    public Guid? UserId { get; set; }

    public string FullName => string.Join(' ', new[] { FirstName, MiddleName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>
    /// Employed for at least part of the period. Leavers are paid up to their termination date whatever their status,
    /// so the final (pro-rated) month and final dues are processed; a non-active status without a date is not paid.
    /// </summary>
    public bool IsPayable(DateOnly periodStart, DateOnly periodEnd) =>
        HireDate <= periodEnd &&
        (TerminationDate is { } leaving
            ? leaving >= periodStart
            : Status is EmploymentStatus.Active or EmploymentStatus.OnLeave or EmploymentStatus.Suspended);
}

public class EmploymentRecord : LegalEntityScopedEntity, IEffectiveDated
{
    public Guid EmployeeId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public EmploymentType EmploymentType { get; set; }
    public EmploymentStatus Status { get; set; }
    public string? JobTitle { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? CostCentreId { get; set; }
    public Guid PayrollGroupId { get; set; }
    public string? ChangeReason { get; set; }
}

public class EmployeeBankAccount : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public PaymentMethod Method { get; set; } = PaymentMethod.BankTransfer;
    public string? BankName { get; set; }
    public string? BankCode { get; set; }
    public string? BranchName { get; set; }
    public string? BranchCode { get; set; }
    public required string AccountName { get; set; }
    /// <summary>Encrypted at rest (see Infrastructure value converter).</summary>
    public required string AccountNumber { get; set; }
    public bool IsPrimary { get; set; } = true;
    /// <summary>Only verified accounts are paid. A different person from the one who entered it must verify (maker-checker).</summary>
    public bool IsVerified { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    /// <summary>Percent of net pay to this account; primary gets the remainder.</summary>
    public decimal SplitPercent { get; set; } = 100m;
}

public class EmployeeStatutoryDetails : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    /// <summary>Encrypted at rest.</summary>
    public string? KraPin { get; set; }
    public string? NssfNumber { get; set; }
    public string? ShifNumber { get; set; }
    public string? NhifNumber { get; set; }
    public bool HasDisabilityExemption { get; set; }
    public string? DisabilityExemptionCertificate { get; set; }
    public decimal DisabilityExemptAmount { get; set; }
    public DateOnly? DisabilityExemptionExpiry { get; set; }
    /// <summary>Statutory rule codes this employee is exempt from, e.g. KE.NSSF for expatriates on home-country schemes.</summary>
    public List<string> StatutoryExemptions { get; set; } = [];
    /// <summary>
    /// NSSF contributions (employee + employer) made before this employer's payroll moved to Ecosys, so the
    /// "NSSF contributions to date" on payslips starts from the right figure.
    /// </summary>
    public decimal NssfOpeningBalance { get; set; }
}
