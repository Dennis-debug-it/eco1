using System.Security.Cryptography;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Ecosys.Payroll.Infrastructure.Services.Hr;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Seed;

public sealed record TenantProvisionRequest(
    string OrganisationName, string Slug, string LegalEntityName, string KraPin, string AdminEmail, string AdminName,
    string? RegistrationNumber = null, string? NssfEmployerNumber = null, string? ShaEmployerNumber = null, string? NitaNumber = null,
    string? Address = null, string? Email = null, string? Phone = null, int PayDay = 28);

public sealed record TenantProvisionResult(Guid TenantId, Guid LegalEntityId, string Slug, string AdminEmail, string TemporaryPassword);

/// <summary>
/// Creates a real customer organisation: tenant, legal entity, a monthly payroll group with periods, a Kenyan starter
/// catalogue (pay components, Employment Act leave types, gazetted public holidays) and the first administrator, who
/// must change the one-time password and enrol MFA at first sign-in. No sample employees or data.
/// </summary>
public sealed class TenantProvisioner(PayrollDbContext db, TenantContext tenant, WorkCalendar calendar)
{
    public async Task<TenantProvisionResult> ProvisionAsync(TenantProvisionRequest r, CancellationToken ct = default)
    {
        var slug = r.Slug.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$"))
            throw new DomainException("The sign-in code (slug) must be 3–40 lowercase letters, digits or hyphens, e.g. 'acme-kenya'.", "VALIDATION");
        var pin = KenyaIdentifiers.NormalisePin(r.KraPin);
        if (!KenyaIdentifiers.IsValidPin(pin, individual: false)) throw new DomainException("Employer KRA PIN should look like P051234567A.", "VALIDATION");
        var email = r.AdminEmail.Trim().ToLowerInvariant();
        if (!email.Contains('@')) throw new DomainException("Enter the administrator's email address.", "VALIDATION");
        db.IgnoreTenantFilter = true;
        if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct)) throw new DomainException($"An organisation with the sign-in code '{slug}' already exists.", "DUPLICATE");
        db.IgnoreTenantFilter = false;

        var t = new Tenant { Name = r.OrganisationName.Trim(), Slug = slug };
        db.Tenants.Add(t);
        tenant.TenantId = t.Id; tenant.UserName = "provisioning"; tenant.IsPlatformAdministrator = true;

        var le = new LegalEntity
        {
            TenantId = t.Id, Name = r.LegalEntityName.Trim(), RegistrationNumber = r.RegistrationNumber ?? "", TaxPin = pin!, NssfEmployerNumber = r.NssfEmployerNumber,
            ShifEmployerNumber = r.ShaEmployerNumber, NitaRegistrationNumber = r.NitaNumber, Address = r.Address, Email = r.Email,
            Phone = string.IsNullOrWhiteSpace(r.Phone) ? null : KenyaIdentifiers.NormalisePhone(r.Phone),
        };
        db.LegalEntities.Add(le);

        // Monthly payroll: this month and the next 23.
        var group = new PayrollGroup { TenantId = t.Id, LegalEntityId = le.Id, Code = "MTH", Name = "Monthly staff", PayDay = Math.Clamp(r.PayDay, 1, 31) };
        db.PayrollGroups.Add(group);
        var today = BusinessClock.Today();
        for (var m = 0; m < 24; m++)
        {
            var start = new DateOnly(today.Year, today.Month, 1).AddMonths(m);
            db.PayrollPeriods.Add(new PayrollPeriod
            {
                TenantId = t.Id, LegalEntityId = le.Id, PayrollGroupId = group.Id, Name = start.ToString("MMMM yyyy"), PeriodStart = start, PeriodEnd = start.AddMonths(1).AddDays(-1),
                PayDate = new DateOnly(start.Year, start.Month, Math.Min(group.PayDay, DateTime.DaysInMonth(start.Year, start.Month))),
            });
        }

        db.SalaryComponents.AddRange(StarterComponents(t.Id, le.Id));
        db.LeaveTypes.AddRange(StarterLeaveTypes(t.Id, le.Id));

        var password = TemporaryPassword();
        var admin = new User
        {
            TenantId = t.Id, Email = email, DisplayName = r.AdminName.Trim(), PasswordHash = PasswordHasher.Hash(password),
            Roles = [Roles.TenantAdministrator], MustChangePassword = true,
        };
        db.Users.Add(admin);
        db.PayrollAuditLogs.Add(new Domain.Payroll.PayrollAuditLog { TenantId = t.Id, ActorName = "provisioning", Action = "tenant.provisioned", EntityType = nameof(Tenant), EntityId = t.Id, Details = $"{{\"slug\":\"{slug}\",\"admin\":\"{email}\"}}" });
        await db.SaveChangesAsync(ct);

        await calendar.SeedKenyaAsync(t.Id, le.Id, today.Year, ct);
        await calendar.SeedKenyaAsync(t.Id, le.Id, today.Year + 1, ct);
        await db.SaveChangesAsync(ct);
        return new TenantProvisionResult(t.Id, le.Id, slug, email, password);
    }

    /// <summary>Common Kenyan pay elements. Review GL codes and rates with the customer before the first run.</summary>
    public static IEnumerable<SalaryComponent> StarterComponents(Guid tenantId, Guid legalEntityId)
    {
        SalaryComponent C(string code, string name, ComponentType type, CalculationMethod method, bool taxable = true, bool pensionable = false, bool basic = false,
            DeductionTreatment treatment = DeductionTreatment.Ordinary, decimal employerMatch = 0, decimal? rate = null, string? gl = null, int order = 0) => new()
        {
            TenantId = tenantId, LegalEntityId = legalEntityId, Code = code, Name = name, Type = type, Method = method, Taxable = taxable, Pensionable = pensionable, IsBasic = basic,
            DeductionTreatment = treatment, EmployerMatchRate = employerMatch, DefaultRate = rate, GlAccountCode = gl, SortOrder = order,
        };
        yield return C("BASIC", "Basic salary", ComponentType.Earning, CalculationMethod.FixedAmount, pensionable: true, basic: true, gl: "6100", order: 1);
        yield return C("HOUSE", "House allowance", ComponentType.Earning, CalculationMethod.FixedAmount, gl: "6110", order: 2);
        yield return C("TRANSPORT", "Transport allowance", ComponentType.Earning, CalculationMethod.FixedAmount, gl: "6120", order: 3);
        yield return C("OT", "Overtime", ComponentType.Earning, CalculationMethod.Variable, gl: "6140", order: 5);
        yield return C("COMM", "Commission", ComponentType.Earning, CalculationMethod.Variable, gl: "6145", order: 6);
        yield return C("BONUS", "Bonus", ComponentType.Earning, CalculationMethod.Variable, gl: "6146", order: 7);
        yield return C("PENSION", "Registered pension scheme", ComponentType.Deduction, CalculationMethod.PercentOfBasic, treatment: DeductionTreatment.RegisteredPension, rate: 0.05m, gl: "2160", order: 20);
        yield return C("LOAN", "Staff loan", ComponentType.Deduction, CalculationMethod.Variable, gl: "1400", order: 21);
        yield return C("ADVANCE", "Salary advance", ComponentType.Deduction, CalculationMethod.Variable, gl: "1410", order: 22);
        yield return C("SACCO", "SACCO contribution", ComponentType.Deduction, CalculationMethod.FixedAmount, gl: "2170", order: 23);
        yield return C("LIFE", "Life insurance premium", ComponentType.Deduction, CalculationMethod.FixedAmount, treatment: DeductionTreatment.QualifyingInsurancePremium, gl: "2180", order: 24);
        yield return C("UNION", "Union dues", ComponentType.Deduction, CalculationMethod.FixedAmount, gl: "2185", order: 25);
    }

    /// <summary>Employment Act 2007 minimums: s.28 annual (21 working days), s.29 maternity (3 months) and paternity (2 weeks), s.30 sick (7 days full pay, 7 half pay after 2 months' service).</summary>
    public static IEnumerable<LeaveType> StarterLeaveTypes(Guid tenantId, Guid legalEntityId)
    {
        yield return new LeaveType { TenantId = tenantId, LegalEntityId = legalEntityId, Code = "ANNUAL", Name = "Annual leave", AnnualEntitlementDays = 21, MaxCarryOverDays = 5 };
        yield return new LeaveType { TenantId = tenantId, LegalEntityId = legalEntityId, Code = "SICK", Name = "Sick leave", AnnualEntitlementDays = 14, AccruesMonthly = false, RequiresAttachment = true, FullPayDaysPerYear = 7, HalfPayDaysPerYear = 7, MinServiceMonths = 2 };
        yield return new LeaveType { TenantId = tenantId, LegalEntityId = legalEntityId, Code = "MAT", Name = "Maternity leave", AnnualEntitlementDays = 90, AccruesMonthly = false, CountsWorkingDaysOnly = false };
        yield return new LeaveType { TenantId = tenantId, LegalEntityId = legalEntityId, Code = "PAT", Name = "Paternity leave", AnnualEntitlementDays = 14, AccruesMonthly = false, CountsWorkingDaysOnly = false };
        yield return new LeaveType { TenantId = tenantId, LegalEntityId = legalEntityId, Code = "COMP", Name = "Compassionate leave", AnnualEntitlementDays = 5, AccruesMonthly = false };
        yield return new LeaveType { TenantId = tenantId, LegalEntityId = legalEntityId, Code = "UNPAID", Name = "Unpaid leave", AnnualEntitlementDays = 0, IsPaid = false, AccruesMonthly = false };
    }

    private static string TemporaryPassword() =>
        string.Join("-", Enumerable.Range(0, 4).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant())) + "-K";
}
