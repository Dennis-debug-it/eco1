using Ecosys.Payroll.Domain.Billing;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services.Saas;
using Microsoft.Extensions.Caching.Memory;

namespace Ecosys.Payroll.Api.Infrastructure;

/// <summary>Per-tenant entitlements, cached briefly so the gate costs one query per tenant every 30 seconds.</summary>
public sealed class EntitlementCache(IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static string Key(Guid tenantId) => $"entitlements:{tenantId}";

    public async Task<Entitlements> GetAsync(Guid tenantId, SubscriptionService subscriptions, CancellationToken ct)
    {
        if (cache.TryGetValue(Key(tenantId), out Entitlements? e) && e is not null) return e;
        e = await subscriptions.GetEntitlementsAsync(tenantId, ct);
        cache.Set(Key(tenantId), e, Ttl);
        return e;
    }

    /// <summary>Call after any change to a subscription or plan, so this instance applies it at once.</summary>
    public void Invalidate(Guid tenantId) => cache.Remove(Key(tenantId));
}

/// <summary>
/// SaaS mode only. A suspended or cancelled organisation can sign in, read and export everything, and sort out
/// billing, but cannot change anything else (402 SUBSCRIPTION_INACTIVE). Modules outside the plan answer 402
/// FEATURE_NOT_IN_PLAN. Operator-managed tenants (no subscription) are never restricted.
/// </summary>
public sealed class SubscriptionGateMiddleware(RequestDelegate next, SaasOptions options)
{
    /// <summary>API areas that need <see cref="PlanFeatures.HrSuite"/>.</summary>
    public static readonly string[] HrSuitePaths =
        ["/api/payroll/recruitment", "/api/payroll/performance", "/api/payroll/learning", "/api/payroll/cases", "/api/payroll/positions", "/api/payroll/benefits", "/api/payroll/assets"];

    /// <summary>Inside HR-suite areas but part of every plan: beneficiaries matter for final dues on death in service.</summary>
    private static readonly string[] CorePaths = ["/api/payroll/benefits/dependants"];

    /// <summary>Still writable while read-only: signing in and security, and billing (so the customer can pay or reactivate).</summary>
    private static readonly string[] AlwaysWritable = ["/api/payroll/auth", "/api/payroll/billing"];

    public async Task InvokeAsync(HttpContext http, ITenantContext ctx, EntitlementCache cache, SubscriptionService subscriptions)
    {
        if (!options.Enabled || ctx.TenantId is not { } tenantId || !http.Request.Path.StartsWithSegments("/api/payroll"))
        {
            await next(http);
            return;
        }
        var ent = await cache.GetAsync(tenantId, subscriptions, http.RequestAborted);
        if (ent.Managed) { await next(http); return; }

        var path = http.Request.Path;
        if (!ent.Has(PlanFeatures.HrSuite) && HrSuitePaths.Any(p => path.StartsWithSegments(p)) && !CorePaths.Any(p => path.StartsWithSegments(p)))
        {
            await Refuse(http, "Not in your plan", "FEATURE_NOT_IN_PLAN", $"This module is not part of the {ent.PlanName} plan. An administrator can upgrade under Billing.");
            return;
        }
        var writes = !(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method));
        if (ent.ReadOnly && writes && !AlwaysWritable.Any(p => path.StartsWithSegments(p)))
        {
            var why = ent.Status == SubscriptionStatus.Cancelled ? "The subscription was cancelled" : ent.SuspensionReason ?? "The subscription is suspended";
            await Refuse(http, "Account is read-only", "SUBSCRIPTION_INACTIVE", $"{why}, so changes are switched off. You can still view and export your records. An administrator can reactivate under Billing.");
            return;
        }
        await next(http);
    }

    private static Task Refuse(HttpContext http, string title, string code, string detail)
    {
        http.Response.StatusCode = StatusCodes.Status402PaymentRequired;
        return http.Response.WriteAsJsonAsync(new { title, status = 402, code, detail });
    }
}
