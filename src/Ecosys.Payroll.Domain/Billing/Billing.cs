using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Domain.Billing;

/// <summary>Features a plan can switch on. Core payroll, leave, self-service, exits and expenses are in every plan.</summary>
public static class PlanFeatures
{
    /// <summary>Talent modules: recruitment, performance, learning, cases, positions, benefits and assets.</summary>
    public const string HrSuite = "hr_suite";

    public static readonly string[] All = [HrSuite];
}

/// <summary>
/// A price list entry offered by the platform operator. Platform-level (not tenant-owned). Prices exclude VAT.
/// Changing a plan affects the next invoice for every subscriber on it; issued invoices keep their own copy of the price.
/// </summary>
public class Plan : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string Currency { get; set; } = "KES";
    /// <summary>Flat monthly fee, which covers <see cref="IncludedEmployees"/>.</summary>
    public decimal MonthlyBaseFee { get; set; }
    public int IncludedEmployees { get; set; }
    /// <summary>Monthly price for each employee above <see cref="IncludedEmployees"/>.</summary>
    public decimal PricePerAdditionalEmployee { get; set; }
    /// <summary>Hard cap on employees on the books; null means no cap.</summary>
    public int? MaxEmployees { get; set; }
    /// <summary>Hard cap on legal entities (companies); null means no cap.</summary>
    public int? MaxLegalEntities { get; set; }
    public List<string> Features { get; set; } = [];
    /// <summary>Shown on the sign-up page and offered to customers. Private plans are assigned by the operator.</summary>
    public bool IsPublic { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public bool Has(string feature) => Features.Contains(feature);
}

public enum SubscriptionStatus
{
    /// <summary>Free trial; full use of the plan until <see cref="Subscription.TrialEndsOn"/>.</summary>
    Trialing,
    /// <summary>Paying customer in good standing.</summary>
    Active,
    /// <summary>An invoice is overdue. Still fully usable during the grace period.</summary>
    PastDue,
    /// <summary>Read-only: the trial ended without activation, or an invoice is overdue beyond the grace period.</summary>
    Suspended,
    /// <summary>Read-only: the customer cancelled. Data stays available for export until it is purged under the retention policy.</summary>
    Cancelled,
}

/// <summary>One per tenant. A tenant without a subscription is operator-managed (licensed install or contract) and is not billed or restricted.</summary>
public class Subscription : TenantEntity
{
    /// <summary>Suspension reason when a trial ends without billing details. The customer can reactivate this one themselves.</summary>
    public const string TrialEndedReason = "Trial ended";

    public Guid PlanId { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trialing;
    public DateOnly? TrialEndsOn { get; set; }
    /// <summary>Set when the customer confirms billing details. A trial that ends without activation is suspended.</summary>
    public DateTimeOffset? ActivatedAt { get; set; }
    /// <summary>Current monthly billing period. Billed in arrears when it ends.</summary>
    public DateOnly? CurrentPeriodStart { get; set; }
    public DateOnly? CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }
    public string? SuspensionReason { get; set; }
    public string? BillingName { get; set; }
    public string? BillingEmail { get; set; }
    /// <summary>Customer's KRA PIN, printed on tax invoices.</summary>
    public string? BillingTaxPin { get; set; }
    public string? BillingAddress { get; set; }

    /// <summary>Suspended and cancelled tenants can read and export, but not change anything.</summary>
    public bool IsReadOnly => Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled;

    /// <summary>
    /// Still within the free-trial stage: a trial was given and no paid billing period has started (conversion,
    /// activation and operator-started subscriptions all set one). True while trialing, and also while suspended or
    /// cancelled during the trial, so reopening the account returns it to the trial rather than starting billing.
    /// </summary>
    public bool InTrialStage => TrialEndsOn is not null && CurrentPeriodStart is null;
}

public enum InvoiceStatus { Open, Paid, Void }

public sealed class InvoiceLine
{
    public required string Description { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>The operator's invoice to a customer for one billing period. Immutable once issued except for payment and voiding.</summary>
public class SubscriptionInvoice : TenantEntity
{
    public required string Number { get; set; }
    public Guid SubscriptionId { get; set; }
    public required string PlanCode { get; set; }
    public required string PlanName { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public DateOnly IssuedOn { get; set; }
    public DateOnly DueOn { get; set; }
    public string Currency { get; set; } = "KES";
    public List<InvoiceLine> Lines { get; set; } = [];
    /// <summary>Employees counted for the period (headcount when the invoice was issued).</summary>
    public int BillableEmployees { get; set; }
    public decimal Subtotal { get; set; }
    public decimal VatRate { get; set; }
    public decimal Vat { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Open;
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public string? BillingName { get; set; }
    public string? BillingTaxPin { get; set; }

    public decimal Balance => Status == InvoiceStatus.Open ? Total - AmountPaid : 0m;
}

public enum BillingPaymentMethod { MPesa, BankTransfer, Card, Cheque, Other }

/// <summary>Money received against an invoice. Recorded by the operator, or by a payment provider callback.</summary>
public class SubscriptionPayment : TenantEntity
{
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
    public BillingPaymentMethod Method { get; set; }
    public required string Reference { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? RecordedByName { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Raised when an action would exceed what the tenant's plan allows, or the subscription is read-only.</summary>
public sealed class PlanLimitException(string message, string code = "PLAN_LIMIT") : DomainException(message, code);
