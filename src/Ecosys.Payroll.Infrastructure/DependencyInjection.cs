using Ecosys.Payroll.Engine;
using Ecosys.Payroll.Engine.Jurisdictions.Kenya;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Seed;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecosys.Payroll.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPayrollInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var key = config["Payroll:Encryption:Key"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Payroll:Encryption:Key must be configured (base64, 32 bytes). Generate one with FieldEncryptor.GenerateKey().");
        services.AddSingleton(new FieldEncryptor(key));

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.AddDbContext<PayrollDbContext>((sp, o) =>
        {
            o.UseNpgsql(config.GetConnectionString("Payroll"), npg => npg.MigrationsHistoryTable("__ef_migrations", "payroll"))
             .UseSnakeCaseNamingConvention();
        });

        services.AddSingleton<IPayrollCalculator, KenyaPayrollCalculator>();
        services.AddSingleton<PayrollCalculatorRegistry>();

        services.AddScoped<AuditService>();
        services.AddScoped<StatutoryRuleService>();
        services.AddScoped<PayrollInputAssembler>();
        services.AddScoped<JournalBuilder>();
        services.AddScoped<PayrollRunService>();
        services.AddScoped<ReportingService>();
        services.AddScoped<DemoDataSeeder>();
        services.AddScoped<NotificationService>();
        if (config["Notifications:Provider"] == "Ecosys")
            services.AddHttpClient<INotificationSender, EcosysNotificationSender>(c =>
            {
                c.BaseAddress = new Uri((config["Notifications:Ecosys:BaseUrl"] ?? throw new InvalidOperationException("Notifications:Ecosys:BaseUrl is required.")).TrimEnd('/') + "/");
                c.Timeout = TimeSpan.FromSeconds(15);
            });
        else
            services.AddScoped<INotificationSender, LogNotificationSender>();
        services.AddSingleton<NotificationDispatcher>();
        if (config.GetValue("Notifications:Enabled", true)) services.AddHostedService(sp => sp.GetRequiredService<NotificationDispatcher>());
        return services;
    }
}
