using Microsoft.OpenApi;

namespace Ecosys.Payroll.Api.Infrastructure;

/// <summary>The OpenAPI description of <c>/api/v1</c>, served at <c>/api/v1/openapi.json</c>. Screen-only endpoints are left out.</summary>
public static class PublicApiDocument
{
    public const string Description = """
        Connect other systems to Ecosys Payroll: time and attendance, HR and recruitment tools, ERPs and accounting, BI and security monitoring.

        ## Authentication
        An administrator creates a key under **Settings → Integrations** and chooses what it may do and which companies it covers. Send it on every request:

            Authorization: Bearer eco_live_…

        (or `X-Api-Key: eco_live_…`). Keys can't approve, finalise or reverse payroll; those stay with people, who sign in with MFA.

        ## Limits
        Each key has a number of requests per minute (120 unless the administrator changes it). Over the limit the API answers `429` with `Retry-After`.

        ## Retrying safely
        Send `Idempotency-Key: <unique text>` with any POST, PUT or PATCH. A retry with the same key and body gets the first answer back
        (marked `Idempotent-Replayed: true`) instead of doing the work twice. Keys are kept for 24 hours.

        ## Errors
        Errors are JSON problem documents with a stable `code`, for example `VALIDATION`, `NOT_FOUND`, `FORBIDDEN`, `INVALID_STATE` (409, such as an approved run),
        `RATE_LIMITED`, `FEATURE_NOT_IN_PLAN` (402) and `SUBSCRIPTION_INACTIVE` (402, read-only account: reads still work).

        ## Events and webhooks
        `GET /api/v1/events` lists changes after a cursor. Webhooks POST the same JSON to your HTTPS address as things happen, signed with
        `Ecosys-Signature: t=<unix time>,v1=<hex HMAC-SHA256 of "<t>.<raw body>" using the webhook secret>`. Check the signature and reject a timestamp
        older than five minutes. Deliveries are retried for about eleven hours; use the event `id` to ignore repeats. Payloads name the record
        and give its URL, never pay figures: fetch details with your key.

        ## Versioning
        Fields may be added to responses at any time; nothing in v1 is removed or renamed. Breaking changes would come as `/api/v2`.
        """;

    public static IServiceCollection AddPublicApiDocument(this IServiceCollection services) => services.AddOpenApi("v1", o =>
    {
        o.ShouldInclude = d => d.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true;
        o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info = new OpenApiInfo { Title = "Ecosys Payroll API", Version = "v1", Description = Description };
            doc.Components ??= new OpenApiComponents();
            doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            doc.Components.SecuritySchemes["apiKey"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer",
                Description = "An API key from Settings → Integrations, sent as `Authorization: Bearer eco_live_…`.",
            };
            doc.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("apiKey", doc)] = [] }];
            return Task.CompletedTask;
        });
    });
}
