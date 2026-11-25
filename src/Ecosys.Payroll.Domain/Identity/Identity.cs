using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Domain.Identity;

/// <summary>Role names. Shared Ecosys identity issues these as role claims.</summary>
public static class Roles
{
    /// <summary>Operator: runs the whole platform, including pricing, settings and other operator accounts.</summary>
    public const string PlatformAdministrator = "PlatformAdministrator";
    /// <summary>Operator: onboards and supports organisations and watches system health. No billing or pricing.</summary>
    public const string PlatformOperations = "PlatformOperations";
    /// <summary>Operator: subscriptions, invoices, payments and revenue. No account administration.</summary>
    public const string PlatformFinance = "PlatformFinance";
    public const string TenantAdministrator = "TenantAdministrator";
    public const string HrAdministrator = "HrAdministrator";
    public const string PayrollAdministrator = "PayrollAdministrator";
    public const string PayrollReviewer = "PayrollReviewer";
    public const string PayrollApprover = "PayrollApprover";
    public const string FinanceOfficer = "FinanceOfficer";
    public const string Auditor = "Auditor";
    public const string Employee = "Employee";

    /// <summary>
    /// Operator roles. They exist only in the platform organisation, carry only <see cref="PlatformPermissions"/>
    /// (never a tenant permission), and are managed in the operator console, not by tenant administrators.
    /// </summary>
    public static readonly string[] Platform = [PlatformAdministrator, PlatformOperations, PlatformFinance];

    /// <summary>Roles an organisation's administrators can give their own users.</summary>
    public static readonly string[] Tenant =
    [
        TenantAdministrator, HrAdministrator, PayrollAdministrator, PayrollReviewer, PayrollApprover, FinanceOfficer, Auditor, Employee,
    ];

    /// <summary>Roles that must use multi-factor authentication.</summary>
    public static readonly string[] Privileged = [.. Platform, TenantAdministrator, PayrollAdministrator, PayrollApprover, FinanceOfficer];

    public static readonly string[] All = [.. Platform, .. Tenant];

    public static bool IsPlatform(string role) => Platform.Contains(role);
}

/// <summary>
/// What operators may do in the operator console (<c>/api/platform</c>). Separate from tenant <see cref="Permissions"/>:
/// no operator role holds a tenant permission, so an operator can't read or change an organisation's records.
/// </summary>
public static class PlatformPermissions
{
    /// <summary>The dashboard and the list and profile of organisations (account data and counts, never payroll).</summary>
    public const string TenantsRead = "platform.tenants.read";
    /// <summary>Create organisations, edit their details, block sign-in, manage their administrators.</summary>
    public const string TenantsManage = "platform.tenants.manage";
    /// <summary>Revenue, balances, invoices, payments and the price list.</summary>
    public const string BillingRead = "platform.billing.read";
    /// <summary>Start, change, suspend and reactivate subscriptions, extend trials, record payments, void invoices, run billing.</summary>
    public const string BillingManage = "platform.billing.manage";
    /// <summary>Create and change plans and prices.</summary>
    public const string PlansManage = "platform.plans.manage";
    /// <summary>Invite operators, change their roles, deactivate them, reset their sign-in.</summary>
    public const string OperatorsManage = "platform.operators.manage";
    /// <summary>Platform-wide settings and shared reference data such as the bank directory.</summary>
    public const string ConfigManage = "platform.config.manage";
    /// <summary>System health, background jobs and delivery failures.</summary>
    public const string MonitorRead = "platform.monitor.read";
    /// <summary>Operator activity, sign-in history and security events across the platform.</summary>
    public const string AuditRead = "platform.audit.read";

    public static readonly string[] All = [TenantsRead, TenantsManage, BillingRead, BillingManage, PlansManage, OperatorsManage, ConfigManage, MonitorRead, AuditRead];

    public static IReadOnlySet<string> ForRoles(IEnumerable<string> roles)
    {
        var set = new HashSet<string>();
        foreach (var r in roles)
        {
            switch (r)
            {
                case Roles.PlatformAdministrator:
                    set.UnionWith(All);
                    break;
                case Roles.PlatformOperations:
                    set.UnionWith([TenantsRead, TenantsManage, MonitorRead]);
                    break;
                case Roles.PlatformFinance:
                    set.UnionWith([TenantsRead, BillingRead, BillingManage]);
                    break;
            }
        }
        return set;
    }
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

    /// <summary>Tenant permissions plus, for operators, <see cref="PlatformPermissions"/>.</summary>
    public static IReadOnlySet<string> ForRoles(IEnumerable<string> roles)
    {
        var list = roles as ICollection<string> ?? roles.ToList();
        var set = new HashSet<string>(PlatformPermissions.ForRoles(list));
        foreach (var r in list)
        {
            switch (r)
            {
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
