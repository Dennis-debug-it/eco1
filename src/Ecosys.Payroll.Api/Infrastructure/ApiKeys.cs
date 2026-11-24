using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Ecosys.Payroll.Domain.Integration;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services.Integration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Ecosys.Payroll.Api.Infrastructure;

/// <summary>Valid keys, cached for 30 seconds so a busy integration costs one lookup per key every 30 seconds.</summary>
public sealed class ApiKeyCache(IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static string Key(string hash) => $"apikey:{hash}";

    public async Task<ApiKeyIdentity?> FindAsync(PayrollDbContext db, string key, CancellationToken ct)
    {
        var hash = ApiKeyService.Hash(key);
        if (cache.TryGetValue(Key(hash), out ApiKeyIdentity? found)) return found;
        found = await ApiKeyService.FindAsync(db, key, ct);
        cache.Set(Key(hash), found, Ttl);
        return found;
    }

    /// <summary>Call after revoking, so this instance refuses the key at once (others within 30 seconds).</summary>
    public void Invalidate(string keyHash) => cache.Remove(Key(keyHash));
}

/// <summary>
/// Signs in a program by its API key, sent as <c>Authorization: Bearer eco_live_…</c> or <c>X-Api-Key: eco_live_…</c>.
/// The principal carries the key's own permissions and companies, never a role.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyCache keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    /// <summary>Picks the API-key handler or the JWT handler for each request, from the shape of the credential.</summary>
    public const string SelectorScheme = "ApiKeyOrJwt";
    public const string ClientIdClaim = "client_id";
    public const string PermissionClaim = "perm";
    public const string RateClaim = "rpm";
    public const string HeaderName = "X-Api-Key";

    public static string? KeyFrom(HttpRequest request)
    {
        if (request.Headers.TryGetValue(HeaderName, out var h) && ApiKeyService.LooksLikeKey(h.ToString().Trim())) return h.ToString().Trim();
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && ApiKeyService.LooksLikeKey(auth[7..].Trim())) return auth[7..].Trim();
        return null;
    }

    public static string SelectScheme(HttpContext http) => KeyFrom(http.Request) is null ? JwtBearerDefaults.AuthenticationScheme : SchemeName;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = KeyFrom(Request);
        if (key is null) return AuthenticateResult.NoResult();
        var db = Context.RequestServices.GetRequiredService<PayrollDbContext>();
        var id = await keys.FindAsync(db, key, Context.RequestAborted);
        if (id is null) return AuthenticateResult.Fail("The API key is not valid, has expired or was revoked.");

        var claims = new List<Claim>
        {
            new("sub", id.ClientId.ToString()), new(ClientIdClaim, id.ClientId.ToString()), new("name", $"API: {id.Name}"),
            new(ClaimNames.TenantId, id.TenantId.ToString()), new(ClaimNames.AuthMethod, "apikey"), new(RateClaim, id.RequestsPerMinute.ToString()),
        };
        claims.AddRange(id.Permissions.Select(p => new Claim(PermissionClaim, p)));
        if (id.LegalEntityIds.Count > 0) claims.Add(new Claim(ClaimNames.LegalEntityIds, string.Join(',', id.LegalEntityIds)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "name", ClaimNames.Role));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(new { title = "Unauthorised", status = 401, code = "INVALID_API_KEY",
            detail = "The API key is not valid, has expired or was revoked. An administrator can issue a new one under Settings, Integrations." });
    }
}

/// <summary>
/// What applies to programs calling with a key: they may use only the published API (<c>/api/v1</c>), at most
/// their key's requests per minute, and each key's last use is recorded.
/// </summary>
public sealed class ApiClientGateMiddleware(RequestDelegate next, IMemoryCache cache)
{
    public const string ApiPrefix = "/api/v1";

    private static readonly PartitionedRateLimiter<(Guid Client, int Limit)> Limiter = PartitionedRateLimiter.Create<(Guid Client, int Limit), (Guid, int)>(k =>
        RateLimitPartition.GetFixedWindowLimiter(k, p => new FixedWindowRateLimiterOptions { PermitLimit = p.Item2, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    public async Task InvokeAsync(HttpContext http, ITenantContext ctx, PayrollDbContext db)
    {
        if (ctx.ApiClientId is not { } client) { await next(http); return; }
        if (!http.Request.Path.StartsWithSegments(ApiPrefix))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { title = "Forbidden", status = 403, code = "API_KEY_SCOPE",
                detail = $"API keys work only with the published API under {ApiPrefix}. See {ApiPrefix}/openapi.json." });
            return;
        }
        var limit = int.TryParse(http.User.FindFirst(ApiKeyAuthenticationHandler.RateClaim)?.Value, out var l) ? l : 120;
        using var lease = Limiter.AttemptAcquire((client, limit));
        http.Response.Headers["X-RateLimit-Limit"] = limit.ToString();
        if (!lease.IsAcquired)
        {
            var retry = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)) : 60;
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            http.Response.Headers.RetryAfter = retry.ToString();
            await http.Response.WriteAsJsonAsync(new { title = "Too many requests", status = 429, code = "RATE_LIMITED",
                detail = $"This key may make {limit} requests a minute. Wait {retry} seconds and try again." });
            return;
        }
        if (!cache.TryGetValue($"apikey-touched:{client}", out _))
        {
            cache.Set($"apikey-touched:{client}", true, TimeSpan.FromMinutes(1));
            await ApiKeyService.TouchAsync(db, client, ctx.IpAddress, http.RequestAborted);
        }
        await next(http);
    }
}

/// <summary>
/// A program can send <c>Idempotency-Key: &lt;any unique text&gt;</c> with a write. The first answer is kept for 24 hours
/// and returned again for a retry of the same request, so a timeout never creates a record twice. Reusing a key for a
/// different request is refused (422); a retry while the first is still running gets 409.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, IServiceScopeFactory scopes)
{
    public const string Header = "Idempotency-Key";
    private const int MaxStoredBytes = 256 * 1024;

    public async Task InvokeAsync(HttpContext http, ITenantContext ctx)
    {
        var method = http.Request.Method;
        if (ctx.ApiClientId is not { } client || ctx.TenantId is not { } tenantId || !http.Request.Headers.TryGetValue(Header, out var raw)
            || !(HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method)))
        {
            await next(http);
            return;
        }
        var key = raw.ToString().Trim();
        if (key.Length is 0 or > 200)
        {
            await Problem(http, 400, "IDEMPOTENCY_KEY_INVALID", "The Idempotency-Key header must be 1 to 200 characters.");
            return;
        }

        http.Request.EnableBuffering();
        string requestHash;
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            sha.AppendData(Encoding.UTF8.GetBytes($"{method} {http.Request.Path}{http.Request.QueryString}\n"));
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0) sha.AppendData(buffer, 0, read);
            http.Request.Body.Position = 0;
            requestHash = Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        // A separate unit of work, so the stored answer survives whatever the request itself does to its own context.
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PayrollDbContext>();
        db.IgnoreTenantFilter = true;
        var existing = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.ApiClientId == client && r.Key == key && r.ExpiresAt > DateTimeOffset.UtcNow, http.RequestAborted);
        if (existing is not null)
        {
            if (existing.RequestHash != requestHash) { await Problem(http, 422, "IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used for a different request. Use a new key for each new request."); return; }
            if (existing.StatusCode == 0) { await Problem(http, 409, "IDEMPOTENCY_IN_PROGRESS", "The first request with this Idempotency-Key is still being processed. Try again shortly."); return; }
            http.Response.StatusCode = existing.StatusCode;
            http.Response.Headers["Idempotent-Replayed"] = "true";
            if (existing.Location is not null) http.Response.Headers.Location = existing.Location;
            if (existing.ContentType is not null) http.Response.ContentType = existing.ContentType;
            if (existing.ResponseBody is not null) await http.Response.WriteAsync(existing.ResponseBody, http.RequestAborted);
            return;
        }
        await db.IdempotencyRecords.IgnoreQueryFilters().Where(r => r.ApiClientId == client && r.Key == key).ExecuteDeleteAsync(http.RequestAborted); // an expired one
        var record = new IdempotencyRecord { TenantId = tenantId, ApiClientId = client, Key = key, RequestHash = requestHash, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };
        db.IdempotencyRecords.Add(record);
        try { await db.SaveChangesAsync(http.RequestAborted); }
        catch (DbUpdateException) { await Problem(http, 409, "IDEMPOTENCY_IN_PROGRESS", "Another request with this Idempotency-Key is being processed. Try again shortly."); return; }

        var original = http.Response.Body;
        await using var capture = new MemoryStream();
        http.Response.Body = capture;
        var stored = false;
        try
        {
            await next(http);
            capture.Position = 0;
            await capture.CopyToAsync(original, http.RequestAborted);
            // Server errors and very large answers aren't kept, so the request can simply be retried.
            if (http.Response.StatusCode < 500 && capture.Length <= MaxStoredBytes)
            {
                record.StatusCode = http.Response.StatusCode;
                record.ContentType = http.Response.ContentType;
                record.Location = http.Response.Headers.Location.ToString() is { Length: > 0 } loc ? loc : null;
                record.ResponseBody = Encoding.UTF8.GetString(capture.ToArray());
                await db.SaveChangesAsync(CancellationToken.None);
                stored = true;
            }
        }
        finally
        {
            http.Response.Body = original;
            if (!stored) await db.IdempotencyRecords.IgnoreQueryFilters().Where(r => r.Id == record.Id).ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    private static Task Problem(HttpContext http, int status, string code, string detail)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new { title = status == 409 ? "Conflict" : "Idempotency key problem", status, code, detail });
    }
}
