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
    /// <summary>
    /// Kerberos/GSS transport encryption is off unless explicitly requested: TLS (SSL Mode) is the supported
    /// encryption, and probing GSS needs libgssapi, which slim container images don't ship.
    /// </summary>
    public static string PayrollConnectionString(IConfiguration config)
    {
        var raw = config.GetConnectionString("Payroll") ?? throw new InvalidOperationException("ConnectionStrings:Payroll is not configured.");
        var b = new Npgsql.NpgsqlConnectionStringBuilder(raw);
        if (!raw.Contains("gss", StringComparison.OrdinalIgnoreCase)) b.GssEncryptionMode = Npgsql.GssEncryptionMode.Disable;
        return b.ConnectionString;
    }

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
            o.UseNpgsql(PayrollConnectionString(config), npg => npg.MigrationsHistoryTable("__ef_migrations", "payroll"))
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
        services.AddScoped<LeaveBalanceService>();
        services.AddScoped<Services.Hr.HrAccess>();
        services.AddScoped<Services.Hr.FileStore>();
        services.AddScoped<Services.Hr.WorkCalendar>();
        services.AddScoped<Services.Hr.SystemComponents>();
        services.AddScoped<Services.Hr.LeaveService>();
        services.AddScoped<Services.Hr.SeparationService>();
        services.AddScoped<Services.Hr.PerformanceService>();
        services.AddScoped<Services.Hr.LearningService>();
        services.AddScoped<Services.Hr.HrOperationsService>();
        services.AddScoped<DataProtectionService>();
        services.AddScoped<Seed.TenantProvisioner>();
        services.AddSingleton<Services.Hr.HrReminderJob>();
        if (config.GetValue("Notifications:Enabled", true)) services.AddHostedService(sp => sp.GetRequiredService<Services.Hr.HrReminderJob>());
        services.AddSingleton(TimeProvider.System);
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
