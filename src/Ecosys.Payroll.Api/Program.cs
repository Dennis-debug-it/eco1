using System.Text;
using System.Text.Json.Serialization;
using Ecosys.Payroll.Api.Endpoints;
using Ecosys.Payroll.Api.Infrastructure;
using Ecosys.Payroll.Infrastructure;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Seed;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

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

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", product = "Ecosys Payroll", version = "1.0.0" })).AllowAnonymous();

var api = app.MapGroup("/api/payroll");
api.MapAuth();
api.MapOrganisation();
api.MapEmployees();
api.MapCompensation();
api.MapStatutory();
api.MapPayrollRuns();
api.MapSelfService();
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
    if (app.Configuration.GetValue("Payroll:SeedDemoData", false) && await seeder.SeedDemoTenantAsync())
        app.Logger.LogInformation("Seeded demo tenant '{Slug}'. Sign in with admin@demo.ecosys / {Password}", DemoDataSeeder.DemoTenantSlug, DemoDataSeeder.DemoPassword);
}

app.Run();

public partial class Program;
