using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ecosys.Payroll.Tests.Integration;

/// <summary>
/// Boots the real API against a throwaway PostgreSQL database (created per test run, dropped afterwards),
/// with the demo tenant seeded plus a second tenant "rival" for isolation tests.
/// Connection: env <c>PAYROLL_TEST_POSTGRES</c> (server-level connection string, no database), default localhost.
/// </summary>
public class PayrollApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public const string Password = "Demo!2345";

    private readonly string _server = Environment.GetEnvironmentVariable("PAYROLL_TEST_POSTGRES") ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres";
    private readonly string _database = $"ecosys_payroll_test_{Guid.NewGuid():N}"[..36];
    private readonly string _key = FieldEncryptor.GenerateKey();
    protected virtual bool RequireMfa => false;
    protected virtual int CareersApplicationsPerHour => 100000;

    public string ConnectionString => $"{_server};Database={_database}";
    public Guid RivalTenantId { get; private set; }
    public Guid RivalLegalEntityId { get; private set; }
    public Guid RivalEmployeeId { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting (not ConfigureAppConfiguration) so values are visible while Program registers services.
        foreach (var (k, v) in new Dictionary<string, string>
        {
            ["ConnectionStrings:Payroll"] = ConnectionString,
            ["Payroll:SeedDemoData"] = "true",
            ["Payroll:Encryption:Key"] = _key,
            ["Auth:Mode"] = "Local",
            ["Auth:SigningKey"] = "integration-test-signing-key-0123456789abcdefghij",
            ["Auth:RequireMfaForPrivilegedRoles"] = RequireMfa ? "true" : "false",
            ["Auth:LoginAttemptsPerMinute"] = "100000", // every test signs in from the same in-memory client address
            ["Careers:ApplicationsPerHour"] = CareersApplicationsPerHour.ToString(),
            ["Notifications:Enabled"] = "false", // tests drive the dispatcher explicitly
            ["Logging:LogLevel:Default"] = "Warning",
        })
            builder.UseSetting(k, v);
    }

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(_server + ";Database=postgres"))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        _ = Server; // boots the app: migrate + seed
        await SeedRivalTenantAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_server + ";Database=postgres");
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Runs code against the DbContext with a system context for the given tenant.</summary>
    public async Task<T> WithDbAsync<T>(Guid? tenantId, Func<PayrollDbContext, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TenantContext>();
        ctx.TenantId = tenantId; ctx.IsPlatformAdministrator = true; ctx.UserName = "test";
        var db = scope.ServiceProvider.GetRequiredService<PayrollDbContext>();
        if (tenantId is null) db.IgnoreTenantFilter = true;
        return await work(db);
    }

    private async Task SeedRivalTenantAsync()
    {
        await WithDbAsync<int>(null, async db =>
        {
            var t = new Tenant { Name = "Rival Holdings", Slug = "rival" };
            var le = new LegalEntity { TenantId = t.Id, Name = "Rival Ltd", RegistrationNumber = "PVT-RIVAL", TaxPin = "P099999999Z" };
            var g = new PayrollGroup { TenantId = t.Id, LegalEntityId = le.Id, Code = "MTH", Name = "Monthly" };
            var e = new Employee { TenantId = t.Id, LegalEntityId = le.Id, EmployeeNumber = "R001", FirstName = "Secret", LastName = "Person", HireDate = new DateOnly(2020, 1, 1), PayrollGroupId = g.Id };
            db.AddRange(t, le, g, e);
            db.Users.Add(new User { TenantId = t.Id, Email = "admin@rival.test", DisplayName = "Rival Admin", PasswordHash = PasswordHasher.Hash(Password), Roles = [Roles.TenantAdministrator] });
            RivalTenantId = t.Id; RivalLegalEntityId = le.Id; RivalEmployeeId = e.Id;
            return await db.SaveChangesAsync();
        });
    }

    public async Task<HttpClient> LoginAsync(string email, string tenant = "demo", string? code = null)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/payroll/auth/login", new { tenant, email, password = Password, code });
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Login {email} failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        var body = await res.Content.ReadFromJsonAsync<JsonObject>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!["token"]!.GetValue<string>());
        return client;
    }
}

[CollectionDefinition(Name)]
public class PayrollApiCollection : ICollectionFixture<PayrollApiFixture>
{
    public const string Name = "payroll-api";
}

public static class HttpTestExtensions
{
    public static async Task<JsonNode> Json(this HttpResponseMessage res, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expected, $"Expected {(int)expected} {expected} but got {(int)res.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? new JsonObject() : JsonNode.Parse(text)!;
    }

    public static async Task<JsonNode> GetJson(this HttpClient c, string url, HttpStatusCode expected = HttpStatusCode.OK) => await (await c.GetAsync(url)).Json(expected);
    public static async Task<JsonNode> PostJson(this HttpClient c, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) => await (await c.PostAsJsonAsync(url, body ?? new { }, PayrollApiFixture.Json)).Json(expected);
    public static async Task<JsonNode> PutJson(this HttpClient c, string url, object body, HttpStatusCode expected = HttpStatusCode.OK) => await (await c.PutAsJsonAsync(url, body, PayrollApiFixture.Json)).Json(expected);
    public static decimal Dec(this JsonNode? n) => n!.GetValue<decimal>();
    public static string Str(this JsonNode? n) => n!.GetValue<string>();
    public static Guid Id(this JsonNode? n) => Guid.Parse(n!.GetValue<string>());
}
