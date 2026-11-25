using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Compliance;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Engine.Jurisdictions.Kenya;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Seed;

/// <summary>
/// Seeds (a) the platform statutory rules for Kenya — always, idempotently — and (b) an optional demo tenant
/// with a realistic small company so the product can be evaluated end-to-end.
/// </summary>
public sealed partial class DemoDataSeeder(PayrollDbContext db, TenantContext tenant)
{
    public const string DemoTenantSlug = "demo";
    public const string DemoPassword = "Demo!2345";

    public async Task SeedPlatformRulesAsync(CancellationToken ct = default)
    {
        db.IgnoreTenantFilter = true;
        var existingRecords = await db.StatutoryRuleVersions.IgnoreQueryFilters().Where(r => r.TenantId == null).ToDictionaryAsync(r => r.Id, ct);
        var existing = existingRecords.Keys.ToHashSet();
        // Vendor-maintained platform versions: when a successor is shipped, the predecessor's end date and
        // reference are synced. Rates/parameters of an existing version are never altered here.
        foreach (var rule in KenyaStatutoryRules.All().Where(r => existing.Contains(r.Id)))
        {
            var rec = existingRecords[rule.Id];
            if (rec.EffectiveTo != rule.EffectiveTo || rec.LegalReference != rule.LegalReference)
            {
                db.PayrollAuditLogs.Add(new Domain.Payroll.PayrollAuditLog { TenantId = Guid.Empty, ActorName = "platform-seed", Action = "statutory.rule.synced", EntityType = nameof(StatutoryRuleVersionRecord), EntityId = rec.Id, Details = $"{{\"effectiveTo\":\"{rec.EffectiveTo}\u2192{rule.EffectiveTo}\"}}" });
                rec.EffectiveTo = rule.EffectiveTo;
                rec.LegalReference = rule.LegalReference;
            }
        }
        foreach (var rule in KenyaStatutoryRules.All().Where(r => !existing.Contains(r.Id)))
        {
            var rec = StatutoryRuleVersionRecord.FromEngine(rule);
            rec.ApprovedAt = DateTimeOffset.UtcNow;
            rec.ApprovalNote = "Seeded platform rule — verify against the authority's current publication before production use.";
            db.StatutoryRuleVersions.Add(rec);
        }
        await db.SaveChangesAsync(ct);
        db.IgnoreTenantFilter = false;
    }

    /// <summary>Adds platform bank-directory rows that are missing. Existing rows are left alone so a correction made in the app sticks.</summary>
    public async Task SeedPlatformBanksAsync(CancellationToken ct = default)
    {
        db.IgnoreTenantFilter = true;
        var existing = (await db.BankDirectoryBanks.IgnoreQueryFilters().Where(b => b.TenantId == null).Select(b => b.Code).ToListAsync(ct)).ToHashSet();
        foreach (var (code, name) in KenyaBanks.All.Where(b => !existing.Contains(b.Code)))
            db.BankDirectoryBanks.Add(new BankDirectoryBank { Code = code, Name = name });
        await db.SaveChangesAsync(ct);
        db.IgnoreTenantFilter = false;
    }

    public const string DemoOperatorEmail = "ops@platform.ecosys";

    /// <summary>One demo operator per role, so each console view can be tried.</summary>
    public static readonly (string Email, string Name, string Role)[] DemoOperators =
    [
        (DemoOperatorEmail, "Demo Operator", Roles.PlatformAdministrator),
        ("support@platform.ecosys", "Demo Operations Officer", Roles.PlatformOperations),
        ("finance@platform.ecosys", "Demo Finance Officer", Roles.PlatformFinance),
    ];

    /// <summary>
    /// Development only: an operator account (organisation code <c>platform</c>) with the demo password, so the operator
    /// console can be reached without running <c>create-platform-admin</c>. Idempotent; adds the account to an existing
    /// operator organisation and leaves other operators alone. Never call this on a server reachable by customers.
    /// </summary>
    public async Task<bool> SeedDemoOperatorAsync(CancellationToken ct = default)
    {
        db.IgnoreTenantFilter = true;
        try
        {
            var t = await db.Tenants.FirstOrDefaultAsync(x => x.Slug == PlatformAdminProvisioner.PlatformSlug, ct);
            if (t is null) { t = new Tenant { Name = "Platform operations", Slug = PlatformAdminProvisioner.PlatformSlug }; db.Tenants.Add(t); }
            tenant.TenantId = t.Id; tenant.UserName = "seed"; tenant.IsSystem = true;
            var existing = await db.Users.Where(u => u.TenantId == t.Id).Select(u => u.Email).ToListAsync(ct);
            var added = false;
            foreach (var (email, name, role) in DemoOperators.Where(o => !existing.Contains(o.Email)))
            {
                db.Users.Add(new User { TenantId = t.Id, Email = email, DisplayName = name, PasswordHash = PasswordHasher.Hash(DemoPassword), Roles = [role] });
                added = true;
            }
            await db.SaveChangesAsync(ct);
            return added;
        }
        finally { db.IgnoreTenantFilter = false; }
    }

    public async Task<bool> SeedDemoTenantAsync(CancellationToken ct = default)
    {
        db.IgnoreTenantFilter = true;
        if (await db.Tenants.AnyAsync(t => t.Slug == DemoTenantSlug, ct)) { db.IgnoreTenantFilter = false; return false; }

        var t = new Tenant { Name = "Savanna Logistics Group", Slug = DemoTenantSlug };
        db.Tenants.Add(t);
        tenant.TenantId = t.Id;
        tenant.UserName = "seed";
        tenant.IsSystem = true;

        var le = new LegalEntity
        {
            TenantId = t.Id, Name = "Savanna Logistics Ltd", RegistrationNumber = "PVT-7Q2KX9", TaxPin = "P051234567A", NssfEmployerNumber = "100234567",
            ShifEmployerNumber = "SHA-EMP-55012", NitaRegistrationNumber = "NITA/4421", Address = "Mombasa Road, Nairobi", Email = "payroll@savanna.example", Phone = "+254 700 000 000",
        };
        db.LegalEntities.Add(le);

        var hq = new Branch { TenantId = t.Id, LegalEntityId = le.Id, Code = "NBO", Name = "Nairobi HQ", Location = "Nairobi" };
        var msa = new Branch { TenantId = t.Id, LegalEntityId = le.Id, Code = "MSA", Name = "Mombasa Depot", Location = "Mombasa" };
        db.Branches.AddRange(hq, msa);

        var ccOps = new CostCentre { TenantId = t.Id, LegalEntityId = le.Id, Code = "CC-OPS", Name = "Operations" };
        var ccAdm = new CostCentre { TenantId = t.Id, LegalEntityId = le.Id, Code = "CC-ADM", Name = "Administration" };
        var ccSal = new CostCentre { TenantId = t.Id, LegalEntityId = le.Id, Code = "CC-SAL", Name = "Sales" };
        db.CostCentres.AddRange(ccOps, ccAdm, ccSal);

        var dOps = new Department { TenantId = t.Id, LegalEntityId = le.Id, Code = "OPS", Name = "Operations", DefaultCostCentreId = ccOps.Id };
        var dFin = new Department { TenantId = t.Id, LegalEntityId = le.Id, Code = "FIN", Name = "Finance", DefaultCostCentreId = ccAdm.Id };
        var dHr = new Department { TenantId = t.Id, LegalEntityId = le.Id, Code = "HR", Name = "People & Culture", DefaultCostCentreId = ccAdm.Id };
        var dSales = new Department { TenantId = t.Id, LegalEntityId = le.Id, Code = "SAL", Name = "Sales", DefaultCostCentreId = ccSal.Id };
        var dIt = new Department { TenantId = t.Id, LegalEntityId = le.Id, Code = "IT", Name = "Technology", DefaultCostCentreId = ccAdm.Id };
        db.Departments.AddRange(dOps, dFin, dHr, dSales, dIt);

        var monthly = new PayrollGroup { TenantId = t.Id, LegalEntityId = le.Id, Code = "MTH", Name = "Monthly staff", PayDay = 28 };
        db.PayrollGroups.Add(monthly);
        foreach (var m in Enumerable.Range(0, 14))
        {
            var start = new DateOnly(2025, 1, 1).AddMonths(m);
            var end = start.AddMonths(1).AddDays(-1);
            db.PayrollPeriods.Add(new PayrollPeriod { TenantId = t.Id, LegalEntityId = le.Id, PayrollGroupId = monthly.Id, Name = start.ToString("MMMM yyyy"), PeriodStart = start, PeriodEnd = end, PayDate = new DateOnly(start.Year, start.Month, Math.Min(28, DateTime.DaysInMonth(start.Year, start.Month))) });
        }

        // Components
        SalaryComponent C(string code, string name, ComponentType type, CalculationMethod method, bool taxable = true, bool pensionable = false, bool basic = false,
            DeductionTreatment treatment = DeductionTreatment.Ordinary, decimal employerMatch = 0, decimal? rate = null, string? gl = null, int order = 0) => new()
        {
            TenantId = t.Id, LegalEntityId = le.Id, Code = code, Name = name, Type = type, Method = method, Taxable = taxable, Pensionable = pensionable, IsBasic = basic,
            DeductionTreatment = treatment, EmployerMatchRate = employerMatch, DefaultRate = rate, GlAccountCode = gl, SortOrder = order,
        };
        var cBasic = C("BASIC", "Basic salary", ComponentType.Earning, CalculationMethod.FixedAmount, pensionable: true, basic: true, gl: "6100", order: 1);
        var cHouse = C("HOUSE", "House allowance", ComponentType.Earning, CalculationMethod.PercentOfBasic, rate: 0.15m, gl: "6110", order: 2);
        var cTransport = C("TRANSPORT", "Transport allowance", ComponentType.Earning, CalculationMethod.FixedAmount, gl: "6120", order: 3);
        var cResp = C("RESP", "Responsibility allowance", ComponentType.Earning, CalculationMethod.FixedAmount, gl: "6130", order: 4);
        var cOt = C("OT", "Overtime", ComponentType.Earning, CalculationMethod.Variable, gl: "6140", order: 5);
        var cComm = C("COMM", "Sales commission", ComponentType.Earning, CalculationMethod.Variable, gl: "6145", order: 6);
        var cBonus = C("BONUS", "Bonus", ComponentType.Earning, CalculationMethod.Variable, gl: "6146", order: 7);
        var cReimb = C("REIMB", "Expense reimbursement", ComponentType.Earning, CalculationMethod.Variable, taxable: false, gl: "6190", order: 8);
        var cPension = C("PENSION", "Staff pension scheme", ComponentType.Deduction, CalculationMethod.PercentOfBasic, treatment: DeductionTreatment.RegisteredPension, employerMatch: 1.0m, rate: 0.05m, gl: "2160", order: 20);
        var cLoan = C("LOAN", "Staff loan", ComponentType.Deduction, CalculationMethod.Variable, gl: "1400", order: 21);
        var cAdvance = C("ADVANCE", "Salary advance", ComponentType.Deduction, CalculationMethod.Variable, gl: "1410", order: 22);
        var cSacco = C("SACCO", "SACCO contribution", ComponentType.Deduction, CalculationMethod.FixedAmount, gl: "2170", order: 23);
        var cLife = C("LIFE", "Life insurance premium", ComponentType.Deduction, CalculationMethod.FixedAmount, treatment: DeductionTreatment.QualifyingInsurancePremium, gl: "2180", order: 24);
        var cUnion = C("UNION", "Union dues", ComponentType.Deduction, CalculationMethod.FixedAmount, gl: "2185", order: 25);
        db.SalaryComponents.AddRange(cBasic, cHouse, cTransport, cResp, cOt, cComm, cBonus, cReimb, cPension, cLoan, cAdvance, cSacco, cLife, cUnion);

        // Structures
        SalaryStructure S(string code, string name, params SalaryStructureLine[] lines)
        {
            var s = new SalaryStructure { TenantId = t.Id, LegalEntityId = le.Id, Code = code, Name = name };
            var i = 0;
            foreach (var l in lines) { l.TenantId = t.Id; l.SalaryStructureId = s.Id; l.SortOrder = ++i; s.Lines.Add(l); }
            return s;
        }
        SalaryStructureLine L(SalaryComponent c, CalculationMethod m, decimal? amount = null, decimal? rate = null) => new() { SalaryComponentId = c.Id, Method = m, Amount = amount, Rate = rate };
        var sMgmt = S("MGMT", "Management", L(cHouse, CalculationMethod.PercentOfBasic, rate: 0.20m), L(cTransport, CalculationMethod.FixedAmount, 15_000m), L(cResp, CalculationMethod.FixedAmount, 20_000m), L(cPension, CalculationMethod.PercentOfBasic, rate: 0.075m));
        var sProf = S("PROF", "Professional", L(cHouse, CalculationMethod.PercentOfBasic, rate: 0.15m), L(cTransport, CalculationMethod.FixedAmount, 8_000m), L(cPension, CalculationMethod.PercentOfBasic, rate: 0.05m));
        var sField = S("FIELD", "Field & support staff", L(cHouse, CalculationMethod.PercentOfBasic, rate: 0.15m), L(cTransport, CalculationMethod.FixedAmount, 4_000m));
        db.SalaryStructures.AddRange(sMgmt, sProf, sField);

        // Employees
        var people = new (string No, string First, string Last, string Title, Department Dept, Branch Br, CostCentre Cc, SalaryStructure Str, decimal Basic, Gender G, string Bank, string BankCode, EmploymentType Type)[]
        {
            ("SL001", "Wanjiru", "Kamau", "Managing Director", dOps, hq, ccAdm, sMgmt, 450_000m, Gender.Female, "KCB Bank", "01", EmploymentType.Permanent),
            ("SL002", "Otieno", "Odhiambo", "Finance Manager", dFin, hq, ccAdm, sMgmt, 280_000m, Gender.Male, "Equity Bank", "68", EmploymentType.Permanent),
            ("SL003", "Amina", "Hassan", "HR Business Partner", dHr, hq, ccAdm, sProf, 160_000m, Gender.Female, "Co-operative Bank", "11", EmploymentType.Permanent),
            ("SL004", "Kipchoge", "Rotich", "Operations Manager", dOps, msa, ccOps, sMgmt, 240_000m, Gender.Male, "KCB Bank", "01", EmploymentType.Permanent),
            ("SL005", "Grace", "Njeri", "Senior Accountant", dFin, hq, ccAdm, sProf, 135_000m, Gender.Female, "Absa Bank", "03", EmploymentType.Permanent),
            ("SL006", "Brian", "Mwangi", "Software Engineer", dIt, hq, ccAdm, sProf, 180_000m, Gender.Male, "Stanbic Bank", "31", EmploymentType.Permanent),
            ("SL007", "Faith", "Achieng", "Sales Executive", dSales, hq, ccSal, sProf, 95_000m, Gender.Female, "Equity Bank", "68", EmploymentType.Permanent),
            ("SL008", "Daniel", "Kiptoo", "Fleet Supervisor", dOps, msa, ccOps, sField, 72_000m, Gender.Male, "KCB Bank", "01", EmploymentType.Permanent),
            ("SL009", "Mercy", "Wambui", "Logistics Coordinator", dOps, msa, ccOps, sField, 58_000m, Gender.Female, "Co-operative Bank", "11", EmploymentType.Permanent),
            ("SL010", "Samuel", "Ochieng", "Driver", dOps, msa, ccOps, sField, 42_000m, Gender.Male, "Equity Bank", "68", EmploymentType.Permanent),
            ("SL011", "Lucy", "Muthoni", "Driver", dOps, hq, ccOps, sField, 42_000m, Gender.Female, "KCB Bank", "01", EmploymentType.Permanent),
            ("SL012", "Peter", "Njoroge", "Warehouse Assistant", dOps, msa, ccOps, sField, 32_000m, Gender.Male, "Family Bank", "70", EmploymentType.Contract),
            ("SL013", "Esther", "Chebet", "Receptionist", dHr, hq, ccAdm, sField, 35_000m, Gender.Female, "Equity Bank", "68", EmploymentType.Permanent),
            ("SL014", "James", "Omondi", "IT Support Technician", dIt, hq, ccAdm, sProf, 85_000m, Gender.Male, "NCBA Bank", "07", EmploymentType.Probation),
            ("SL015", "Zawadi", "Baraka", "Sales Intern", dSales, hq, ccSal, sField, 25_000m, Gender.Female, "KCB Bank", "01", EmploymentType.Intern),
            ("SL016", "Hamisi", "Juma", "Security Officer", dOps, msa, ccOps, sField, 28_000m, Gender.Male, "Co-operative Bank", "11", EmploymentType.Casual),
        };

        var employees = new List<Employee>();
        var i2 = 0;
        foreach (var p in people)
        {
            i2++;
            var e = new Employee
            {
                TenantId = t.Id, LegalEntityId = le.Id, EmployeeNumber = p.No, FirstName = p.First, LastName = p.Last, JobTitle = p.Title, Gender = p.G,
                NationalId = $"{20_000_000 + i2 * 137:D8}", DateOfBirth = new DateOnly(1975 + i2, (i2 % 12) + 1, (i2 % 27) + 1), Email = $"{p.First.ToLower()}.{p.Last.ToLower()}@savanna.example",
                Phone = $"+2547{10_000_000 + i2 * 9_173:D8}", EmploymentType = p.Type, HireDate = new DateOnly(2018 + (i2 % 7), (i2 % 12) + 1, 1),
                DepartmentId = p.Dept.Id, BranchId = p.Br.Id, CostCentreId = p.Cc.Id, PayrollGroupId = monthly.Id, WorkLocation = p.Br.Location,
            };
            if (p.No == "SL014") e.HireDate = new DateOnly(2025, 9, 15);
            employees.Add(e);
            db.Employees.Add(e);
            db.EmploymentRecords.Add(new EmploymentRecord { TenantId = t.Id, LegalEntityId = le.Id, EmployeeId = e.Id, EffectiveFrom = e.HireDate, EmploymentType = e.EmploymentType, Status = EmploymentStatus.Active, JobTitle = e.JobTitle, DepartmentId = e.DepartmentId, BranchId = e.BranchId, CostCentreId = e.CostCentreId, PayrollGroupId = monthly.Id, ChangeReason = "Hired" });
            db.EmployeeBankAccounts.Add(new EmployeeBankAccount { TenantId = t.Id, LegalEntityId = le.Id, EmployeeId = e.Id, BankName = p.Bank, BankCode = p.BankCode, BranchCode = "001", BranchName = "Head Office", AccountName = $"{p.First} {p.Last}", AccountNumber = $"{1_100_000_000L + i2 * 7_919L:D12}", IsVerified = true });
            db.EmployeeStatutoryDetails.Add(new EmployeeStatutoryDetails { TenantId = t.Id, LegalEntityId = le.Id, EmployeeId = e.Id, KraPin = $"A{(i2 * 61_803 + 1_234_567) % 10_000_000:D9}{(char)('A' + i2 % 26)}", NssfNumber = $"{2_000_300_000 + i2 * 311}", ShifNumber = $"SHA{700_000 + i2 * 53}" });
            db.EmployeeSalaryAssignments.Add(new EmployeeSalaryAssignment { TenantId = t.Id, LegalEntityId = le.Id, EmployeeId = e.Id, SalaryStructureId = p.Str.Id, BasicSalary = p.Basic, EffectiveFrom = e.HireDate, ChangeReason = "Initial appointment" });
        }

        // A couple of recurring personal deductions and a staff loan.
        var otieno = employees[1]; var brian = employees[5]; var daniel = employees[7];
        db.EmployeeSalaryAssignmentLines.AddRange(
            new EmployeeSalaryAssignmentLine { TenantId = t.Id, EmployeeSalaryAssignmentId = db.ChangeTracker.Entries<EmployeeSalaryAssignment>().First(a => a.Entity.EmployeeId == otieno.Id).Entity.Id, SalaryComponentId = cLife.Id, Method = CalculationMethod.FixedAmount, Amount = 12_000m },
            new EmployeeSalaryAssignmentLine { TenantId = t.Id, EmployeeSalaryAssignmentId = db.ChangeTracker.Entries<EmployeeSalaryAssignment>().First(a => a.Entity.EmployeeId == brian.Id).Entity.Id, SalaryComponentId = cSacco.Id, Method = CalculationMethod.FixedAmount, Amount = 10_000m },
            new EmployeeSalaryAssignmentLine { TenantId = t.Id, EmployeeSalaryAssignmentId = db.ChangeTracker.Entries<EmployeeSalaryAssignment>().First(a => a.Entity.EmployeeId == daniel.Id).Entity.Id, SalaryComponentId = cUnion.Id, Method = CalculationMethod.FixedAmount, Amount = 500m });
        db.StaffLoans.Add(new StaffLoan { TenantId = t.Id, LegalEntityId = le.Id, EmployeeId = daniel.Id, SalaryComponentId = cLoan.Id, Reference = "LN-2025-014", Principal = 120_000m, Balance = 84_000m, InstalmentAmount = 12_000m, StartDate = new DateOnly(2025, 6, 1) });

        // Leave
        var annual = new LeaveType { TenantId = t.Id, LegalEntityId = le.Id, Code = "ANNUAL", Name = "Annual leave", AnnualEntitlementDays = 21, MaxCarryOverDays = 5 };
        var sick = new LeaveType { TenantId = t.Id, LegalEntityId = le.Id, Code = "SICK", Name = "Sick leave", AnnualEntitlementDays = 14, AccruesMonthly = false, RequiresAttachment = true };
        var maternity = new LeaveType { TenantId = t.Id, LegalEntityId = le.Id, Code = "MAT", Name = "Maternity leave", AnnualEntitlementDays = 90, AccruesMonthly = false };
        var paternity = new LeaveType { TenantId = t.Id, LegalEntityId = le.Id, Code = "PAT", Name = "Paternity leave", AnnualEntitlementDays = 14, AccruesMonthly = false };
        var unpaid = new LeaveType { TenantId = t.Id, LegalEntityId = le.Id, Code = "UNPAID", Name = "Unpaid leave", AnnualEntitlementDays = 0, IsPaid = false, AccruesMonthly = false };
        db.LeaveTypes.AddRange(annual, sick, maternity, paternity, unpaid);
        foreach (var e in employees)
        {
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance { TenantId = t.Id, LegalEntityId = le.Id, EmployeeId = e.Id, LeaveTypeId = annual.Id, Year = 2025, Entitlement = 21, Accrued = 19.25m, Taken = (e.EmployeeNumber.GetHashCode() & 7), CarriedOver = 2 });
            db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance { TenantId = t.Id, LegalEntityId = le.Id, EmployeeId = e.Id, LeaveTypeId = sick.Id, Year = 2025, Entitlement = 14, Accrued = 14, Taken = 0 });
        }

        // Users (one per role, plus two employees for self-service)
        void U(string email, string name, IEnumerable<string> roles, Employee? emp = null) => db.Users.Add(new User
        {
            TenantId = t.Id, Email = email, DisplayName = name, PasswordHash = PasswordHasher.Hash(DemoPassword), Roles = roles.ToList(), EmployeeId = emp?.Id,
        });
        U("admin@demo.ecosys", "Tenant Administrator", [Roles.TenantAdministrator]);
        U("hr@demo.ecosys", "Amina Hassan", [Roles.HrAdministrator, Roles.Employee], employees[2]);
        U("payroll@demo.ecosys", "Grace Njeri", [Roles.PayrollAdministrator, Roles.Employee], employees[4]);
        U("reviewer@demo.ecosys", "Payroll Reviewer", [Roles.PayrollReviewer]);
        U("approver@demo.ecosys", "Otieno Odhiambo", [Roles.PayrollApprover, Roles.Employee], employees[1]);
        U("finance@demo.ecosys", "Finance Officer", [Roles.FinanceOfficer]);
        U("auditor@demo.ecosys", "External Auditor", [Roles.Auditor]);
        U("brian.mwangi@demo.ecosys", "Brian Mwangi", [Roles.Employee], employees[5]);
        foreach (var e in employees) e.UserId = db.ChangeTracker.Entries<User>().FirstOrDefault(u => u.Entity.EmployeeId == e.Id)?.Entity.Id;

        await db.SaveChangesAsync(ct);
        db.IgnoreTenantFilter = false;
        return true;
    }
}
