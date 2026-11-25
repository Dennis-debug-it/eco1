using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Migration;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Services.Saas;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Migration;

/// <summary>
/// Loads a company's staff from the old payroll system: the employee record, statutory numbers, one bank account and
/// current pay (basic salary plus any fixed pay component, as a column named after its code or name, e.g. HOUSE,
/// "Transport allowance", SACCO). New employees only: a staff number already on the books is a problem, never an update.
/// Bank accounts arrive unverified, so nothing is paid to them until a second person checks each one.
/// </summary>
public sealed class EmployeeImporter(PayrollDbContext db, BankDirectoryService banks, SubscriptionService subscriptions)
{
    public static readonly IReadOnlyList<ImportColumn> Columns =
    [
        new("EmployeeNumber", "Staff number, unique in the company", true, "E001", "EmployeeNo", "EmpNo", "Emp No", "StaffNo", "Staff Number", "PayrollNo", "Payroll Number", "EmployeeId", "EmpId", "EmployeeCode", "EmpCode", "PersonnelNo", "PF No", "PFNumber", "Code"),
        new("FirstName", "First name (or give FullName instead)", false, "Achieng", "Forename", "GivenName", "FName", "First"),
        new("MiddleName", "Middle name", false, null, "Middle", "SecondName"),
        new("LastName", "Surname (or give FullName instead)", false, "Otieno", "Surname", "FamilyName", "LName", "Last"),
        new("FullName", "Whole name, split into first, middle and last when those columns are missing", false, null, "Name", "EmployeeName", "Names", "Employee"),
        new("HireDate", "Date joined", true, "05/01/2026", "DateOfEmployment", "EmploymentDate", "DateJoined", "JoinDate", "JoiningDate", "DateOfJoining", "StartDate", "DateEngaged", "EngagementDate", "DateHired"),
        new("NationalId", "National ID number", false, "12345678", "IdNo", "Id Number", "NationalIdNo", "NationalIdNumber", "IDNumber", "ID"),
        new("PassportNumber", "Passport or alien ID, for non-citizens", false, null, "Passport", "PassportNo", "AlienId", "AlienIdNo"),
        new("DateOfBirth", "Date of birth", false, "14/03/1990", "DOB", "BirthDate", "Date Of Birth"),
        new("Gender", "Female, Male or Other", false, "Female", "Sex"),
        new("Email", "Email address", false, "achieng@example.com", "EmailAddress", "WorkEmail", "E-mail"),
        new("Phone", "Mobile number", false, "0712345678", "Mobile", "MobileNo", "MobileNumber", "PhoneNo", "PhoneNumber", "Telephone", "CellPhone", "Tel"),
        new("PhysicalAddress", "Address", false, null, "Address", "PostalAddress", "ResidentialAddress"),
        new("EmergencyContactName", "Emergency contact or next of kin", false, null, "NextOfKin", "EmergencyContact", "NOK"),
        new("EmergencyContactPhone", "Their phone number", false, null, "NextOfKinPhone", "NextOfKinTel", "EmergencyPhone", "NOKPhone"),
        new("EmploymentType", "Permanent, Contract, Casual, Intern, Probation or Expatriate (default Permanent)", false, "Permanent", "ContractType", "Terms", "TermsOfEmployment", "EmployeeType", "Category"),
        new("Status", "Active, OnLeave, Suspended, Terminated, Resigned, Retired or Deceased (default Active)", false, "Active", "EmploymentStatus", "EmployeeStatus"),
        new("JobTitle", "Job title", false, "Accountant", "Designation", "Title", "Position", "Role", "Job"),
        new("Department", "Department code or name", false, "FIN", "Dept", "DepartmentCode", "DepartmentName", "Section"),
        new("Branch", "Branch code or name (the company's own branch, not the bank's)", false, null, "BranchName", "CompanyBranch", "Office"),
        new("CostCentre", "Cost centre code or name", false, null, "CostCenter", "CostCentreCode", "CC"),
        new("PayrollGroup", "Pay group code or name; may be left out when the company has one pay group", false, "MTH", "PayGroup", "PayrollGroupCode"),
        new("ManagerNumber", "Line manager's staff number (in this file or already loaded)", false, null, "ManagerNo", "ReportsTo", "LineManager", "SupervisorNo", "Supervisor", "ManagerEmployeeNumber"),
        new("WorkLocation", "Where they work", false, null, "Location", "Station", "WorkStation"),
        new("ContractEndDate", "End of a fixed-term contract", false, null, "ContractEnd", "EndOfContract"),
        new("ProbationEndDate", "End of probation", false, null, "ProbationEnd"),
        new("NoticePeriodDays", "Notice period in days", false, null, "NoticePeriod", "NoticeDays"),
        new("KraPin", "Employee KRA PIN", false, "A012345678Z", "PIN", "PinNo", "PIN Number", "TaxPin", "KRA", "KRA PIN No"),
        new("NssfNumber", "NSSF member number", false, null, "NSSF", "NssfNo", "NSSF Number"),
        new("ShaNumber", "SHA (SHIF) member number", false, null, "SHA", "ShaNo", "SHIF", "ShifNo", "ShifNumber", "NHIF", "NhifNo", "NhifNumber"),
        new("DisabilityExemption", "Yes if exempt from PAYE on disability grounds", false, null, "Disabled", "Disability", "PWD", "PwdExemption"),
        new("DisabilityCertificate", "Exemption certificate number", false, null, "ExemptionCertificate", "DisabilityCertificateNo"),
        new("DisabilityExemptionExpiry", "Date the exemption certificate ends", false, null, "ExemptionExpiry", "CertificateExpiry"),
        new("PaymentMethod", "Bank, M-Pesa, Cash or Cheque (default Bank when an account number is given)", false, "Bank", "PayMode", "ModeOfPayment", "PaymentMode"),
        new("BankCode", "Bank code (e.g. 01 for KCB) or the bank's name", false, "01", "Bank", "BankName"),
        new("BankBranchCode", "Bank branch code", false, "091", "BranchCode", "BankBranch", "BankBranchNo"),
        new("AccountName", "Name on the account (default the employee's name)", false, null, "BankAccountName", "AccName"),
        new("AccountNumber", "Bank account number, or M-Pesa number", false, "1234567890", "AccountNo", "AccNo", "AcNo", "BankAccount", "BankAccountNo", "BankAccountNumber", "MpesaNo", "MpesaNumber"),
        new("BasicSalary", "Monthly basic salary", false, "85000", "Basic", "BasicPay", "Salary", "MonthlySalary", "BasicSalaryKES"),
        new("PayBasis", "Monthly or Daily (default Daily when a daily rate is given, otherwise Monthly)", false, "Monthly", "PayType", "WageBasis", "PaidBy", "SalaryType", "PayFrequency"),
        new("DailyRate", "Daily wage, for workers paid by the day worked", false, null, "DayRate", "RatePerDay", "DailyWage", "WagePerDay", "Daily", "WageRate"),
        new("SalaryEffectiveFrom", "Date the salary applies from (default the hire date)", false, null, "EffectiveFrom", "EffectiveDate", "SalaryEffectiveDate"),
    ];

    public sealed record Planned(int Row, Employee Employee, EmploymentRecord Record, EmployeeStatutoryDetails? Statutory, EmployeeBankAccount? Bank, EmployeeSalaryAssignment? Salary);

    public sealed class Plan
    {
        public List<Planned> Rows { get; } = [];
        public Dictionary<string, int> Summary { get; } = [];
        public List<string> Notes { get; } = [];
    }

    private static readonly Dictionary<string, EmploymentType> Types = new()
    {
        ["permanent"] = EmploymentType.Permanent, ["perm"] = EmploymentType.Permanent, ["contract"] = EmploymentType.Contract, ["fixedterm"] = EmploymentType.Contract,
        ["casual"] = EmploymentType.Casual, ["temporary"] = EmploymentType.Casual, ["temp"] = EmploymentType.Casual, ["intern"] = EmploymentType.Intern, ["internship"] = EmploymentType.Intern,
        ["attachment"] = EmploymentType.Intern, ["probation"] = EmploymentType.Probation, ["expatriate"] = EmploymentType.Expatriate, ["expat"] = EmploymentType.Expatriate,
    };
    private static readonly Dictionary<string, EmploymentStatus> Statuses = new()
    {
        ["active"] = EmploymentStatus.Active, ["current"] = EmploymentStatus.Active, ["onleave"] = EmploymentStatus.OnLeave, ["leave"] = EmploymentStatus.OnLeave,
        ["suspended"] = EmploymentStatus.Suspended, ["terminated"] = EmploymentStatus.Terminated, ["dismissed"] = EmploymentStatus.Terminated, ["resigned"] = EmploymentStatus.Resigned,
        ["retired"] = EmploymentStatus.Retired, ["deceased"] = EmploymentStatus.Deceased,
    };
    private static readonly Dictionary<string, Gender> Genders = new()
    {
        ["f"] = Gender.Female, ["female"] = Gender.Female, ["m"] = Gender.Male, ["male"] = Gender.Male, ["other"] = Gender.Other, ["unspecified"] = Gender.Unspecified, ["u"] = Gender.Unspecified,
    };
    private static readonly Dictionary<string, PaymentMethod> Methods = new()
    {
        ["bank"] = PaymentMethod.BankTransfer, ["banktransfer"] = PaymentMethod.BankTransfer, ["eft"] = PaymentMethod.BankTransfer, ["rtgs"] = PaymentMethod.BankTransfer, ["transfer"] = PaymentMethod.BankTransfer,
        ["mpesa"] = PaymentMethod.MobileMoney, ["mobilemoney"] = PaymentMethod.MobileMoney, ["mobile"] = PaymentMethod.MobileMoney,
        ["cash"] = PaymentMethod.Cash, ["cheque"] = PaymentMethod.Cheque, ["check"] = PaymentMethod.Cheque,
    };

    public async Task<Plan> PlanAsync(Guid legalEntityId, Sheet sheet, List<ImportProblem> problems, CancellationToken ct)
    {
        var plan = new Plan();
        // Fixed pay components (allowances, SACCO, insurance premiums…) can be columns of their own, named by code or name.
        var components = await db.SalaryComponents.Where(c => c.LegalEntityId == legalEntityId && c.IsActive && !c.IsBasic && c.Method == CalculationMethod.FixedAmount).ToListAsync(ct);
        var byComponentKey = new Dictionary<string, SalaryComponent>();
        foreach (var c in components) { byComponentKey.TryAdd(ColumnMap.Normalise(c.Code), c); byComponentKey.TryAdd(ColumnMap.Normalise(c.Name), c); }
        var map = ColumnMap.Build(sheet, Columns, problems, h => byComponentKey.TryGetValue(ColumnMap.Normalise(h), out var c) ? c.Id.ToString() : null);

        if (!map.Has("EmployeeNumber")) problems.Add(new ImportProblem(1, "EmployeeNumber", "The file needs an EmployeeNumber column (staff number)."));
        if (!map.Has("HireDate")) problems.Add(new ImportProblem(1, "HireDate", "The file needs a HireDate column (date joined)."));
        if (!(map.Has("FirstName") && map.Has("LastName")) && !map.Has("FullName")) problems.Add(new ImportProblem(1, "FirstName", "The file needs FirstName and LastName columns, or a FullName column."));
        if (problems.Count > 0) return plan;
        // A column named after a component that isn't a fixed amount here (e.g. house allowance as a % of basic) is explained, not just listed.
        var calculated = await db.SalaryComponents.Where(c => c.LegalEntityId == legalEntityId && c.IsActive && !c.IsBasic && c.Method != CalculationMethod.FixedAmount).ToListAsync(ct);
        var unknown = new List<string>();
        foreach (var h in map.Unrecognised)
        {
            var c = calculated.FirstOrDefault(x => ColumnMap.Normalise(x.Code) == ColumnMap.Normalise(h) || ColumnMap.Normalise(x.Name) == ColumnMap.Normalise(h));
            if (c is null) unknown.Add(h);
            else plan.Notes.Add(c.Method == CalculationMethod.Variable
                ? $"\"{h}\" isn't imported: {c.Name} is entered on each payroll run, not as fixed pay."
                : $"\"{h}\" isn't imported: this company works out {c.Name} as a percentage, not a fixed amount. Change the component under Compensation if each employee has their own amount.");
        }
        if (unknown.Count > 0) plan.Notes.Add($"Not imported (columns this import doesn't use): {string.Join(", ", unknown)}.");
        var componentColumns = map.Extra.ToDictionary(x => x.Key, x => components.First(c => c.Id.ToString() == x.Value));
        if (componentColumns.Count > 0) plan.Notes.Add($"Pay component columns: {string.Join(", ", componentColumns.Select(x => $"{sheet.Headers[x.Key]} → {x.Value.Name}"))}.");

        var entity = await db.LegalEntities.FirstAsync(x => x.Id == legalEntityId, ct);
        var groups = await db.PayrollGroups.Where(x => x.LegalEntityId == legalEntityId).ToListAsync(ct);
        var depts = await db.Departments.Where(x => x.LegalEntityId == legalEntityId).ToListAsync(ct);
        var branches = await db.Branches.Where(x => x.LegalEntityId == legalEntityId).ToListAsync(ct);
        var centres = await db.CostCentres.Where(x => x.LegalEntityId == legalEntityId).ToListAsync(ct);
        var existing = await db.Employees.IgnoreQueryFilters().Where(e => e.LegalEntityId == legalEntityId).Select(e => new { e.EmployeeNumber, e.Id, e.IsDeleted }).ToListAsync(ct);
        var existingNumbers = existing.Select(e => e.EmployeeNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingManagers = existing.Where(e => !e.IsDeleted).GroupBy(e => e.EmployeeNumber, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var existingPins = (await db.EmployeeStatutoryDetails.Where(s => s.LegalEntityId == legalEntityId && s.KraPin != null).Select(s => s.KraPin!).ToListAsync(ct)).ToHashSet();
        var directory = await banks.ListBanksAsync(ct);
        var finalisedThrough = await db.PayrollRuns.Where(r => r.LegalEntityId == legalEntityId && r.Status == PayrollRunStatus.Finalised)
            .Join(db.PayrollPeriods, r => r.PayrollPeriodId, p => p.Id, (r, p) => p.PeriodEnd).OrderByDescending(d => d).FirstOrDefaultAsync(ct);
        if (!map.Has("PayrollGroup") && groups.Count != 1)
            problems.Add(new ImportProblem(1, "PayrollGroup", $"The company has {groups.Count} pay groups, so the file needs a PayrollGroup column ({string.Join(", ", groups.Select(g => g.Code))})."));

        var inFile = new Dictionary<string, (int Row, Guid Id)>(StringComparer.OrdinalIgnoreCase);
        var pinsInFile = new Dictionary<string, int>();
        var managerRefs = new List<(RowReader Row, Employee Employee, string Manager)>();

        foreach (var sheetRow in sheet.Rows)
        {
            var r = new RowReader(sheetRow, map, problems);
            var number = r.Text("EmployeeNumber", required: true);
            string? first = r.Text("FirstName"), middle = r.Text("MiddleName"), last = r.Text("LastName");
            if ((first is null || last is null) && r.Text("FullName") is { } full)
            {
                var parts = full.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2) { first ??= parts[0]; last ??= parts[^1]; middle ??= parts.Length > 2 ? string.Join(' ', parts[1..^1]) : null; }
                else r.Problem("FullName", $"Name '{full}' needs at least a first name and a surname.");
            }
            else if (first is null || last is null) r.Problem(first is null ? "FirstName" : "LastName", "First name and surname are both required.");
            var hired = r.Date("HireDate", required: true);

            if (number is not null)
            {
                if (existingNumbers.Contains(number)) r.Problem("EmployeeNumber", $"Employee {number} is already in this company. The import adds new employees only.");
                else if (inFile.TryGetValue(number, out var dup)) r.Problem("EmployeeNumber", $"Employee {number} appears twice in the file (also row {dup.Row}).");
            }

            var group = map.Has("PayrollGroup") ? Find(r, "PayrollGroup", groups, g => g.Code, g => g.Name, required: true) : groups.Count == 1 ? groups[0] : null;
            var e = new Employee
            {
                LegalEntityId = legalEntityId, EmployeeNumber = number ?? "", FirstName = first ?? "", MiddleName = middle, LastName = last ?? "",
                NationalId = r.Text("NationalId"), PassportNumber = r.Text("PassportNumber"), DateOfBirth = r.Date("DateOfBirth"),
                Gender = r.Choice("Gender", Genders, "Female, Male, Other") ?? Gender.Unspecified,
                Email = r.Text("Email"), Phone = r.Text("Phone"), PhysicalAddress = r.Text("PhysicalAddress"),
                EmergencyContactName = r.Text("EmergencyContactName"), EmergencyContactPhone = r.Text("EmergencyContactPhone"),
                EmploymentType = r.Choice("EmploymentType", Types, "Permanent, Contract, Casual, Intern, Probation, Expatriate") ?? EmploymentType.Permanent,
                Status = r.Choice("Status", Statuses, "Active, OnLeave, Suspended, Terminated, Resigned, Retired, Deceased") ?? EmploymentStatus.Active,
                HireDate = hired ?? default, JobTitle = r.Text("JobTitle"),
                DepartmentId = Find(r, "Department", depts, d => d.Code, d => d.Name)?.Id, BranchId = Find(r, "Branch", branches, b => b.Code, b => b.Name)?.Id,
                CostCentreId = Find(r, "CostCentre", centres, c => c.Code, c => c.Name)?.Id, PayrollGroupId = group?.Id ?? Guid.Empty,
                WorkLocation = r.Text("WorkLocation"), ContractEndDate = r.Date("ContractEndDate"), ProbationEndDate = r.Date("ProbationEndDate"),
                NoticePeriodDays = r.Int("NoticePeriodDays", 0, 365) ?? 30,
            };
            foreach (var p in EmployeeRules.IdentifierProblems(e)) r.Problem(ColumnFor(p), p);
            if (e.Status is EmploymentStatus.Terminated or EmploymentStatus.Resigned or EmploymentStatus.Retired or EmploymentStatus.Deceased) e.TerminationDate ??= e.ContractEndDate;
            if (number is not null) inFile[number] = (r.Number, e.Id);
            if (r.Text("ManagerNumber") is { } mgr) managerRefs.Add((r, e, mgr));

            // Statutory numbers: only when the file carries any of them.
            EmployeeStatutoryDetails? stat = null;
            if (new[] { "KraPin", "NssfNumber", "ShaNumber", "DisabilityExemption", "DisabilityCertificate" }.Any(map.Has))
            {
                stat = new EmployeeStatutoryDetails
                {
                    LegalEntityId = legalEntityId, EmployeeId = e.Id, KraPin = r.Text("KraPin"), NssfNumber = r.Text("NssfNumber"), ShifNumber = r.Text("ShaNumber"),
                    HasDisabilityExemption = r.Bool("DisabilityExemption") ?? false, DisabilityExemptionCertificate = r.Text("DisabilityCertificate"), DisabilityExemptionExpiry = r.Date("DisabilityExemptionExpiry"),
                };
                foreach (var p in EmployeeRules.StatutoryProblems(stat)) r.Problem(ColumnFor(p), p);
                if (stat.HasDisabilityExemption && stat.DisabilityExemptionCertificate is null) r.Problem("DisabilityCertificate", "A disability exemption needs the exemption certificate number.");
                if (stat.KraPin is { } pin)
                {
                    if (existingPins.Contains(pin)) r.Problem("KraPin", $"KRA PIN {pin} already belongs to an employee in this company.");
                    else if (pinsInFile.TryGetValue(pin, out var other)) r.Problem("KraPin", $"KRA PIN {pin} appears twice in the file (also row {other}).");
                    else pinsInFile[pin] = r.Number;
                }
            }

            var bank = await BankAsync(r, e, directory, ct);
            var salary = Salary(r, e, componentColumns, sheet, finalisedThrough, entity);
            if (r.HasProblems) continue;
            plan.Rows.Add(new Planned(r.Number, e, new EmploymentRecord
            {
                LegalEntityId = legalEntityId, EmployeeId = e.Id, EffectiveFrom = e.HireDate, EmploymentType = e.EmploymentType, Status = e.Status, JobTitle = e.JobTitle,
                DepartmentId = e.DepartmentId, BranchId = e.BranchId, CostCentreId = e.CostCentreId, PayrollGroupId = e.PayrollGroupId, ChangeReason = "Imported",
            }, stat, bank, salary));
        }

        foreach (var (r, e, mgr) in managerRefs)
        {
            if (string.Equals(mgr, e.EmployeeNumber, StringComparison.OrdinalIgnoreCase)) { r.Problem("ManagerNumber", "An employee can't report to themselves."); continue; }
            if (inFile.TryGetValue(mgr, out var m)) e.ReportingManagerId = m.Id;
            else if (existingManagers.TryGetValue(mgr, out var id)) e.ReportingManagerId = id;
            else r.Problem("ManagerNumber", $"Manager {mgr} isn't in this file or in the company.");
        }
        // A manager row that failed takes its reports with it, or they would point at nobody (repeated down the chain).
        var fileIds = inFile.Values.Select(v => v.Id).ToHashSet();
        while (true)
        {
            var failedRows = problems.Select(x => x.Row).ToHashSet();
            plan.Rows.RemoveAll(p => failedRows.Contains(p.Row));
            var planned = plan.Rows.Select(p => p.Employee.Id).ToHashSet();
            var orphans = managerRefs.Where(x => !x.Row.HasProblems && x.Employee.ReportingManagerId is { } m && fileIds.Contains(m) && !planned.Contains(m)).ToList();
            if (orphans.Count == 0) break;
            foreach (var (r, _, mgr) in orphans) r.Problem("ManagerNumber", $"Manager {mgr} has problems of their own in this file; fix that row first.");
        }

        var counted = plan.Rows.Count(p => SubscriptionService.CountedStatuses.Contains(p.Employee.Status));
        try { await subscriptions.EnsureCanAddEmployeesAsync(counted, ct); }
        catch (DomainException ex) { problems.Add(new ImportProblem(0, null, ex.Message)); }

        plan.Summary["employees"] = plan.Rows.Count;
        plan.Summary["statutoryDetails"] = plan.Rows.Count(p => p.Statutory is not null);
        plan.Summary["bankAccounts"] = plan.Rows.Count(p => p.Bank is not null);
        plan.Summary["salaries"] = plan.Rows.Count(p => p.Salary is not null);
        var cash = plan.Rows.Count(p => p.Bank is null && p.Employee.Status == EmploymentStatus.Active);
        if (cash > 0) plan.Notes.Add($"{cash} active employee(s) have no bank or M-Pesa account in the file. Add one before the first live payroll, or they can't be paid by transfer.");
        if (plan.Summary["bankAccounts"] > 0) plan.Notes.Add("Bank accounts arrive unverified. Check each against the old system's last bank file, then verify it on the employee's page; payment files skip unverified accounts.");
        return plan;
    }

    public void Apply(Plan plan, ImportBatch batch)
    {
        foreach (var p in plan.Rows)
        {
            p.Employee.TenantId = batch.TenantId;
            db.Employees.Add(p.Employee);
            db.EmploymentRecords.Add(p.Record);
            if (p.Statutory is not null) db.EmployeeStatutoryDetails.Add(p.Statutory);
            if (p.Bank is not null) db.EmployeeBankAccounts.Add(p.Bank);
            if (p.Salary is not null) db.EmployeeSalaryAssignments.Add(p.Salary);
            batch.Records.Employees.Add(p.Employee.Id);
        }
    }

    private async Task<EmployeeBankAccount?> BankAsync(RowReader r, Employee e, IReadOnlyList<DirectoryBank> directory, CancellationToken ct)
    {
        var number = r.Text("AccountNumber");
        var method = r.Choice("PaymentMethod", Methods, "Bank, M-Pesa, Cash, Cheque") ?? (number is null ? null : PaymentMethod.BankTransfer);
        if (method is null or PaymentMethod.Cash or PaymentMethod.Cheque) return null;
        if (number is null) { r.Problem("AccountNumber", $"Payment by {(method == PaymentMethod.MobileMoney ? "M-Pesa needs the M-Pesa number" : "bank needs the account number")}."); return null; }
        string? code = null;
        if (method == PaymentMethod.BankTransfer && r.Text("BankCode") is { } bankCell)
        {
            code = ResolveBankCode(bankCell, directory, out var why);
            if (code is null) { r.Problem("BankCode", why!); return null; }
        }
        try
        {
            var d = await banks.ResolveAsync(method.Value, code, r.Text("BankBranchCode"), number, ct);
            return new EmployeeBankAccount
            {
                LegalEntityId = e.LegalEntityId, EmployeeId = e.Id, Method = method.Value, BankCode = d.BankCode, BankName = d.BankName, BranchCode = d.BranchCode, BranchName = d.BranchName,
                AccountName = r.Text("AccountName") ?? $"{e.FirstName} {e.LastName}", AccountNumber = d.AccountNumber, IsPrimary = true, SplitPercent = 100m, IsVerified = false,
            };
        }
        catch (DomainException ex) { r.Problem(method == PaymentMethod.MobileMoney ? "AccountNumber" : "BankCode", ex.Message); return null; }
    }

    /// <summary>A bank code as written, a code missing its leading zero ("1" for "01"), or a bank's name, as old systems export it.</summary>
    public static string? ResolveBankCode(string cell, IReadOnlyList<DirectoryBank> directory, out string? why)
    {
        why = null;
        var s = cell.Trim();
        if (s.All(char.IsAsciiDigit))
        {
            var hit = directory.FirstOrDefault(b => b.Code == s) ?? directory.FirstOrDefault(b => b.Code.TrimStart('0') == s.TrimStart('0'));
            if (hit is not null) return hit.Code;
            why = $"Bank code '{s}' isn't in the bank directory.";
            return null;
        }
        var key = Simplify(s);
        var matches = directory.Where(b => Simplify(b.Name) == key).ToList();
        if (matches.Count == 0) matches = directory.Where(b => Simplify(b.Name).StartsWith(key) || key.StartsWith(Simplify(b.Name))).ToList();
        if (matches.Count == 1) return matches[0].Code;
        why = matches.Count == 0 ? $"Bank '{s}' isn't in the bank directory. Use the bank code instead." : $"Bank '{s}' matches several banks ({string.Join(", ", matches.Select(m => m.Name))}). Use the bank code instead.";
        return null;
    }

    private static string Simplify(string name)
    {
        var n = ColumnMap.Normalise(name);
        foreach (var w in new[] { "limited", "ltd", "plc", "kenya", "bank", "the" }) n = n.Replace(w, "");
        return n;
    }

    private static readonly Dictionary<string, PayBasis> Bases = new()
    {
        ["monthly"] = PayBasis.Monthly, ["month"] = PayBasis.Monthly, ["salary"] = PayBasis.Monthly, ["salaried"] = PayBasis.Monthly,
        ["daily"] = PayBasis.Daily, ["day"] = PayBasis.Daily, ["perday"] = PayBasis.Daily, ["dailypaid"] = PayBasis.Daily, ["casual"] = PayBasis.Daily,
    };

    private static EmployeeSalaryAssignment? Salary(RowReader r, Employee e, Dictionary<int, SalaryComponent> componentColumns, Sheet sheet, DateOnly finalisedThrough, Domain.Organisation.LegalEntity entity)
    {
        var basic = r.Money("BasicSalary");
        var dailyRate = r.Money("DailyRate");
        var basis = r.Choice("PayBasis", Bases, "Monthly, Daily") ?? (dailyRate is not null && basic is null ? PayBasis.Daily : PayBasis.Monthly);
        var lines = new List<EmployeeSalaryAssignmentLine>();
        foreach (var (index, component) in componentColumns)
            if (r.MoneyAt(index, sheet.Headers[index]) is { } amount && amount > 0)
                lines.Add(new EmployeeSalaryAssignmentLine { SalaryComponentId = component.Id, Method = CalculationMethod.FixedAmount, Amount = amount });
        if (basic is null && dailyRate is null && lines.Count == 0) return null;
        if (basis == PayBasis.Daily && dailyRate is not > 0) { r.Problem("DailyRate", "A daily-paid worker needs a daily rate above zero."); return null; }
        if (basis == PayBasis.Monthly && basic is null) { r.Problem("BasicSalary", dailyRate is null ? "Pay components are given but BasicSalary is missing." : "A daily rate is given but PayBasis says Monthly. Set PayBasis to Daily, or give BasicSalary."); return null; }
        var from = r.Date("SalaryEffectiveFrom") ?? e.HireDate;
        if (finalisedThrough != default && from <= finalisedThrough)
        {
            r.Problem("SalaryEffectiveFrom", $"Payroll is finalised through {finalisedThrough:dd/MM/yyyy}; the salary must apply from a later date.");
            return null;
        }
        var a = new EmployeeSalaryAssignment { LegalEntityId = e.LegalEntityId, EmployeeId = e.Id, EffectiveFrom = from, ChangeReason = "Imported from previous payroll" };
        PayBasisRules.Apply(a, basis, basic ?? 0m, dailyRate, entity);
        foreach (var l in lines) { l.EmployeeSalaryAssignmentId = a.Id; a.Lines.Add(l); }
        return a;
    }

    /// <summary>The column a shared validation message is about, so the dry run can point at the cell.</summary>
    private static string? ColumnFor(string message) => message switch
    {
        _ when message.StartsWith("KRA PIN") => "KraPin",
        _ when message.StartsWith("NSSF") => "NssfNumber",
        _ when message.StartsWith("SHA") => "ShaNumber",
        _ when message.StartsWith("National ID") => "NationalId",
        _ when message.StartsWith("Emergency contact phone") => "EmergencyContactPhone",
        _ when message.StartsWith("Phone") => "Phone",
        _ when message.StartsWith("Email") => "Email",
        _ => null,
    };

    private static T? Find<T>(RowReader r, string column, List<T> options, Func<T, string> code, Func<T, string> name, bool required = false) where T : class
    {
        var v = r.Text(column, required);
        if (v is null) return null;
        var key = ColumnMap.Normalise(v);
        var hit = options.FirstOrDefault(o => ColumnMap.Normalise(code(o)) == key) ?? options.FirstOrDefault(o => ColumnMap.Normalise(name(o)) == key);
        if (hit is null) r.Problem(column, $"{column} '{v}' doesn't exist in this company. Add it under Organisation first, or use one of: {string.Join(", ", options.Take(12).Select(code))}{(options.Count > 12 ? "…" : "")}.");
        return hit;
    }
}
