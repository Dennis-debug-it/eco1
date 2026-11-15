using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecosys.Payroll.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c>; the running application resolves the context through DI.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PayrollDbContext>
{
    public PayrollDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("PAYROLL_CONNECTION") ?? "Host=localhost;Port=5432;Database=ecosys_payroll;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<PayrollDbContext>()
            .UseNpgsql(cs, npg => npg.MigrationsHistoryTable("__ef_migrations", "payroll"))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new PayrollDbContext(options, new TenantContext(), new FieldEncryptor(FieldEncryptor.GenerateKey()));
    }
}
