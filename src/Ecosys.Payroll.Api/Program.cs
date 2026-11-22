using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Ecosys.Payroll.Api.Endpoints;
using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Infrastructure;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Seed;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// Container healthcheck: `dotnet Ecosys.Payroll.Api.dll --healthcheck` probes the running instance (no curl in the image).
if (args.Contains("--healthcheck"))
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    try { return (await http.GetAsync($"http://localhost:{Environment.GetEnvironmentVariable("HEALTHCHECK_PORT") ?? "8080"}/health")).IsSuccessStatusCode ? 0 : 1; }
    catch { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
ProductionGuard.Check(builder.Configuration, builder.Environment);
Ecosys.Payroll.Domain.Common.BusinessClock.Configure(builder.Configuration["Payroll:TimeZone"]);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddPayrollInfrastructure(builder.Configuration);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<SessionValidator>();

// Sign-in throttling per client address, on top of per-account lockout.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (c, ct) => await c.HttpContext.Response.WriteAsJsonAsync(new { title = "Too many requests", status = 429, code = "RATE_LIMITED",
        detail = c.HttpContext.Request.Path.StartsWithSegments("/api/payroll/public/careers")
            ? "Too many applications from this network. Try again later."
            : "Too many sign-in attempts from this network. Wait a minute and try again." }, ct);
    o.AddPolicy(AuthEndpoints.LoginRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("Auth:LoginAttemptsPerMinute", 10), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Anyone can apply on the public careers page, so applications per network address are capped.
    o.AddPolicy(CareersEndpoints.ApplyRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("Careers:ApplicationsPerHour", 10), Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
});

// Behind a TLS-terminating proxy (Caddy, nginx, a cloud load balancer) trust its X-Forwarded-* headers so client
// addresses in the audit log and rate limiter are real, and HTTPS is recognised.
if (builder.Configuration.GetValue("Payroll:BehindProxy", false))
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        o.KnownIPNetworks.Clear(); o.KnownProxies.Clear(); // the proxy is the only route in (container network)
        o.ForwardLimit = 1;
    });
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition")));

// Authentication: shared Ecosys identity (OIDC authority) in production; a local HS256 issuer for development/tests.
var auth = builder.Configuration.GetSection("Auth");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false; // keep JWT claim names (sub, role, amr, tenant_id) exactly as issued
    if (auth["Mode"] == "Ecosys")
    {
        o.Authority = auth["Authority"];
        o.Audience = auth["Audience"];
    }
    else
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = auth["Issuer"] ?? "ecosys-payroll-dev",
            ValidAudience = auth["Audience"] ?? "ecosys-payroll",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth["SigningKey"] ?? throw new InvalidOperationException("Auth:SigningKey is required in Local auth mode."))),
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = "name",
            RoleClaimType = "role",
        };
    }
});
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Configuration.GetValue("Payroll:BehindProxy", false)) app.UseForwardedHeaders();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) app.UseHsts();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", product = "Ecosys Payroll", version = "1.0.0" })).AllowAnonymous();
// Readiness for load balancers and monitoring: the database must answer and the outbox must not be backing up.
app.MapGet("/health/ready", async (PayrollDbContext db, CancellationToken ct) =>
{
    try
    {
        db.IgnoreTenantFilter = true;
        var stuck = await db.NotificationMessages.CountAsync(m => m.Status == Ecosys.Payroll.Domain.Payroll.NotificationStatus.Failed && m.CreatedAt > DateTimeOffset.UtcNow.AddDays(-1), ct);
        return Results.Ok(new { status = "ready", database = "ok", failedNotifications24h = stuck });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Json(new { status = "unavailable", database = "unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();

var api = app.MapGroup("/api/payroll");
api.MapAuth();
api.MapOrganisation();
api.MapBanking();
api.MapCareers();
api.MapEmployees();
api.MapCompensation();
api.MapStatutory();
api.MapPayrollRuns();
api.MapSelfService();
api.MapLeave();
api.MapSeparations();
api.MapPerformance();
api.MapLearning();
api.MapHr();
api.MapDataProtection();
api.MapReports();

// Static SPA (built React app copied into wwwroot in the container image).
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html").AllowAnonymous();

// Database bootstrap.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PayrollDbContext>();
    if (app.Configuration.GetValue("Payroll:MigrateOnStartup", true)) await db.Database.MigrateAsync();
    var seeder = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
    await seeder.SeedPlatformRulesAsync();
    await seeder.SeedPlatformBanksAsync();
    if (app.Configuration.GetValue("Payroll:SeedDemoData", false) && await seeder.SeedDemoTenantAsync())
        app.Logger.LogInformation("Seeded demo tenant '{Slug}'. Sign in with admin@demo.ecosys / {Password}", DemoDataSeeder.DemoTenantSlug, DemoDataSeeder.DemoPassword);
    if (app.Configuration.GetValue("Payroll:SeedDemoData", false))
        await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().EnsureDemoHrDataAsync();
}

// One-off command: create a customer organisation and its first administrator, then exit.
//   docker compose run --rm payroll provision-tenant --name "Acme Kenya" --slug acme --entity "Acme Kenya Ltd" \
//     --kra-pin P051234567A --admin-email hr.lead@acme.co.ke --admin-name "Jane Wanjiku"
if (args.FirstOrDefault() == "provision-tenant")
{
    string? Arg(string name) { var i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    string Req(string name) => Arg(name) ?? throw new ArgumentException($"--{name} is required");
    try
    {
        using var scope = app.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<TenantProvisioner>().ProvisionAsync(new TenantProvisionRequest(
            Req("name"), Req("slug"), Arg("entity") ?? Req("name"), Req("kra-pin"), Req("admin-email"), Req("admin-name"),
            Arg("registration-number"), Arg("nssf-number"), Arg("sha-number"), Arg("nita-number"), Arg("address"), Arg("email"), Arg("phone"),
            int.TryParse(Arg("pay-day"), out var payDay) ? payDay : 28));
        Console.WriteLine($"""

            Organisation created.
              Sign-in code (organisation): {result.Slug}
              Administrator:               {result.AdminEmail}
              One-time password:           {result.TemporaryPassword}

            Give the password to the administrator directly (not by email). At first sign-in they choose their own
            password and set up an authenticator app. It will not be shown again.
            """);
        return 0;
    }
    catch (Exception ex) when (ex is ArgumentException or Ecosys.Payroll.Domain.Common.DomainException)
    {
        Console.Error.WriteLine($"provision-tenant: {ex.Message}");
        return 2;
    }
}

await app.RunAsync();
return 0;

public partial class Program;
