using Microsoft.Extensions.Configuration;

namespace Ecosys.Payroll.Infrastructure.Services.Saas;

/// <summary>
/// Settings for running as a multi-customer service (<c>Saas:*</c>). Off by default, so a licensed single-customer
/// install behaves exactly as before: no public sign-up, no plans, no billing, no restrictions.
/// </summary>
public sealed record SaasOptions
{
    public bool Enabled { get; init; }
    /// <summary>Allow organisations to create their own account from the sign-in page.</summary>
    public bool SignupEnabled { get; init; } = true;
    /// <summary>Self-service sign-ups confirm their email address before they can sign in.</summary>
    public bool RequireEmailVerification { get; init; } = true;
    public int TrialDays { get; init; } = 14;
    /// <summary>Days after an invoice's due date before the account becomes read-only.</summary>
    public int GraceDays { get; init; } = 7;
    public int InvoiceDueDays { get; init; } = 14;
    /// <summary>VAT charged on subscription invoices (Kenya standard rate 16% under the VAT Act 2013). Recorded on each invoice.</summary>
    public decimal VatRate { get; init; } = 0.16m;
    /// <summary>Plan offered by default on the sign-up page.</summary>
    public string DefaultPlan { get; init; } = "growth";
    /// <summary>The operator's legal details, printed on invoices.</summary>
    public string SellerName { get; init; } = "Ecosys";
    public string? SellerTaxPin { get; init; }
    public string? SellerAddress { get; init; }
    /// <summary>How customers pay, printed on invoices and the billing page, e.g. "M-Pesa Paybill 123456, account: your invoice number".</summary>
    public string PaymentInstructions { get; init; } = "Pay by bank transfer or M-Pesa using your invoice number as the reference. Contact billing support for account details.";
    public string? SupportEmail { get; init; }
    /// <summary>Sign-in codes that sign-ups may not take.</summary>
    public string[] ReservedSlugs { get; init; } = ["admin", "api", "app", "billing", "demo", "help", "login", "platform", "signup", "status", "support", "www"];

    public static SaasOptions From(IConfiguration config)
    {
        var o = config.GetSection("Saas").Get<SaasOptions>() ?? new SaasOptions();
        // Blank values (e.g. unset variables in docker compose) fall back to the defaults.
        var d = new SaasOptions();
        if (string.IsNullOrWhiteSpace(o.SellerName)) o = o with { SellerName = d.SellerName };
        if (string.IsNullOrWhiteSpace(o.PaymentInstructions)) o = o with { PaymentInstructions = d.PaymentInstructions };
        if (string.IsNullOrWhiteSpace(o.DefaultPlan)) o = o with { DefaultPlan = d.DefaultPlan };
        if (string.IsNullOrWhiteSpace(o.SupportEmail)) o = o with { SupportEmail = null };
        if (string.IsNullOrWhiteSpace(o.SellerTaxPin)) o = o with { SellerTaxPin = null };
        if (string.IsNullOrWhiteSpace(o.SellerAddress)) o = o with { SellerAddress = null };
        // Sign-up creates local passwords; with the shared Ecosys identity provider, accounts are created there instead.
        return config["Auth:Mode"] == "Ecosys" ? o with { SignupEnabled = false } : o;
    }
}
