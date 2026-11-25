using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Domain.Identity;

/// <summary>Role names. Shared Ecosys identity issues these as role claims.</summary>
public static class Roles
{
    public const string PlatformAdministrator = "PlatformAdministrator";
    public const string TenantAdministrator = "TenantAdministrator";
    public const string HrAdministrator = "HrAdministrator";
    public const string PayrollAdministrator = "PayrollAdministrator";
    public const string PayrollReviewer = "PayrollReviewer";
    public const string PayrollApprover = "PayrollApprover";
    public const string FinanceOfficer = "FinanceOfficer";
    public const string Auditor = "Auditor";
    public const string Employee = "Employee";

    /// <summary>Roles that must use multi-factor authentication.</summary>
    public static readonly string[] Privileged = [PlatformAdministrator, TenantAdministrator, PayrollAdministrator, PayrollApprover, FinanceOfficer];

    public static readonly string[] All =
    [
        PlatformAdministrator, TenantAdministrator, HrAdministrator, PayrollAdministrator, PayrollReviewer,
        PayrollApprover, FinanceOfficer, Auditor, Employee,
    ];
}

/// <summary>Fine-grained permissions. Roles map to permission sets; salary visibility is independent.</summary>
public static class Permissions
{
    public const string OrganisationManage = "organisation.manage";
    public const string EmployeesRead = "employees.read";
    public const string EmployeesWrite = "employees.write";
    public const string SalaryRead = "salary.read";
    public const string SalaryWrite = "salary.write";
    public const string CompensationManage = "compensation.manage";
    public const string StatutoryManage = "statutory.manage";
    public const string StatutoryApprove = "statutory.approve";
    public const string PayrollPrepare = "payroll.prepare";
    public const string PayrollReview = "payroll.review";
    public const string PayrollApprove = "payroll.approve";
    public const string PayrollFinalise = "payroll.finalise";
    public const string PayrollReverse = "payroll.reverse";
    public const string PaymentsManage = "payments.manage";
    public const string JournalsRead = "journals.read";
    public const string ReportsRead = "reports.read";
    public const string AuditRead = "audit.read";
    public const string LeaveApprove = "leave.approve";
    public const string SelfService = "self.service";
    public const string HrPerformance = "hr.performance";
    public const string HrLearning = "hr.learning";
    public const string HrRecruitment = "hr.recruitment";
    public const string HrCases = "hr.cases";
    public const string HrDocuments = "hr.documents";
    public const string HrAssets = "hr.assets";
    public const string HrBenefits = "hr.benefits";
    public const string HrSeparations = "hr.separations";
    public const string ExpensesFinance = "expenses.finance";
    /// <summary>Subscription, plan and invoices. Tenant administrators only: HR administrators manage the organisation but not the contract.</summary>
    public const string BillingManage = "billing.manage";
    /// <summary>API keys and webhooks. Tenant administrators only: a key can read and change records without anyone signing in.</summary>
    public const string IntegrationsManage = "integrations.manage";

    public static readonly string[] Hr = [HrPerformance, HrLearning, HrRecruitment, HrCases, HrDocuments, HrAssets, HrBenefits, HrSeparations];

    public static IReadOnlySet<string> ForRoles(IEnumerable<string> roles)
    {
        var set = new HashSet<string>();
        foreach (var r in roles)
        {
            switch (r)
            {
                case Roles.PlatformAdministrator:
                case Roles.TenantAdministrator:
                    set.UnionWith([OrganisationManage, EmployeesRead, EmployeesWrite, SalaryRead, SalaryWrite, CompensationManage,
                        StatutoryManage, StatutoryApprove, PayrollPrepare, PayrollReview, PayrollApprove, PayrollFinalise, PayrollReverse,
                        PaymentsManage, JournalsRead, ReportsRead, AuditRead, LeaveApprove, SelfService, ExpensesFinance, BillingManage, IntegrationsManage, .. Hr]);
                    break;
                case Roles.HrAdministrator:
                    set.UnionWith([EmployeesRead, EmployeesWrite, OrganisationManage, LeaveApprove, ReportsRead, SelfService, .. Hr]);
                    break;
                case Roles.PayrollAdministrator:
                    set.UnionWith([EmployeesRead, SalaryRead, SalaryWrite, CompensationManage, StatutoryManage, PayrollPrepare, PaymentsManage, ReportsRead, SelfService]);
                    break;
                case Roles.PayrollReviewer:
                    set.UnionWith([EmployeesRead, SalaryRead, PayrollReview, ReportsRead, SelfService]);
                    break;
                case Roles.PayrollApprover:
                    set.UnionWith([EmployeesRead, SalaryRead, PayrollReview, PayrollApprove, PayrollFinalise, PayrollReverse, StatutoryApprove, ReportsRead, SelfService]);
                    break;
                case Roles.FinanceOfficer:
                    set.UnionWith([SalaryRead, JournalsRead, PaymentsManage, ReportsRead, SelfService, ExpensesFinance]);
                    break;
                case Roles.Auditor:
                    set.UnionWith([EmployeesRead, SalaryRead, JournalsRead, ReportsRead, AuditRead]);
                    break;
                case Roles.Employee:
                    set.Add(SelfService);
                    break;
            }
        }
        return set;
    }
}

/// <summary>Local projection of a platform identity, used for role assignment and self-service linkage.</summary>
public class User : TenantEntity
{
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    /// <summary>Subject identifier from the shared Ecosys identity provider.</summary>
    public string? ExternalSubject { get; set; }
    /// <summary>PBKDF2 hash — only used by the built-in development identity provider.</summary>
    public string? PasswordHash { get; set; }
    public List<string> Roles { get; set; } = [];
    /// <summary>Legal entities the user may act on; empty means all in the tenant.</summary>
    public List<Guid> LegalEntityIds { get; set; } = [];
    public Guid? EmployeeId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MfaEnabled { get; set; }
    /// <summary>Base32 TOTP secret, encrypted at rest.</summary>
    public string? MfaSecret { get; set; }
    public string? Phone { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Changes whenever credentials or access change; tokens carrying an older stamp are rejected.</summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
    /// <summary>Set for administrator-issued passwords; the user must choose their own before reaching payroll data.</summary>
    public bool MustChangePassword { get; set; }
    /// <summary>Last TOTP time step accepted at sign-in, so a code cannot be replayed.</summary>
    public long? LastTotpStep { get; set; }
    /// <summary>SHA-256 of the pending email verification token. While set, the user cannot sign in (self-service sign-ups only).</summary>
    public string? EmailVerificationTokenHash { get; set; }
    public DateTimeOffset? EmailVerificationExpiresAt { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    /// <summary>SHA-256 of the pending "forgot password" token. Single use; cleared when used or when the password changes.</summary>
    public string? PasswordResetTokenHash { get; set; }
    public DateTimeOffset? PasswordResetExpiresAt { get; set; }

    public void RotateSecurityStamp() => SecurityStamp = Guid.NewGuid();
}
