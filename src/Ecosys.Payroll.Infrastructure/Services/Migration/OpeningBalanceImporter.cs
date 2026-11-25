using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Domain.Migration;
using Ecosys.Payroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Migration;

/// <summary>
/// Balances the old system carried for employees already loaded: leave (per type and year), loans and advances still
/// being recovered, and NSSF contributed before Ecosys (shown as "NSSF to date" on payslips). A row may carry any of the
/// three; an employee with two loans or two leave types takes two rows. Recurring deductions (SACCO, HELB, insurance)
/// are pay components and come in with the employee import.
/// </summary>
public sealed class OpeningBalanceImporter(PayrollDbContext db)
{
    public static readonly IReadOnlyList<ImportColumn> Columns =
    [
        new("EmployeeNumber", "Staff number of an employee already in Ecosys", true, "E001", "EmployeeNo", "EmpNo", "Emp No", "StaffNo", "Staff Number", "PayrollNo", "EmployeeId", "EmpId", "EmployeeCode", "PF No"),
        new("LeaveType", "Leave type code or name (default ANNUAL when leave figures are given)", false, "ANNUAL", "Leave", "LeaveCode", "LeaveTypeCode"),
        new("LeaveYear", "Leave year (default this year)", false, "2026", "Year"),
        new("LeaveBalance", "Days the employee can still take today. Use this, or the three columns below", false, null, "LeaveDays", "DaysRemaining", "LeaveDaysRemaining", "BalanceDays", "DaysAvailable", "LeaveBalanceDays"),
        new("CarriedOver", "Days brought forward from last year", false, "4", "BroughtForward", "BF", "Carried Forward", "CarryForward", "OpeningLeave"),
        new("Taken", "Days taken so far this year", false, "6", "DaysTaken", "LeaveTaken", "Used"),
        new("Accrued", "Days earned so far this year (default worked out from the hire date)", false, null, "Earned", "DaysEarned", "LeaveAccrued", "Entitled"),
        new("LoanType", "LOAN or ADVANCE, or another deduction component's code or name (default LOAN)", false, "LOAN", "DeductionType", "LoanCode"),
        new("LoanReference", "Loan reference, unique in the company (default made from the staff number)", false, null, "LoanRef", "LoanNo", "LoanNumber", "Reference"),
        new("LoanPrincipal", "Amount originally lent (default the balance)", false, "120000", "Principal", "LoanAmount", "AmountBorrowed", "OriginalAmount"),
        new("LoanBalance", "Amount still owed today", false, "80000", "OutstandingBalance", "Outstanding", "LoanOutstanding", "BalanceOutstanding"),
        new("LoanInstalment", "Amount recovered each month", false, "10000", "Instalment", "Installment", "MonthlyDeduction", "MonthlyRepayment", "Repayment"),
        new("LoanStartDate", "Date the loan started (default today)", false, null, "LoanDate", "DateIssued", "StartDate"),
        new("NssfBeforeEcosys", "Total NSSF contributions made before Ecosys (employee + employer, as the statement shows)", false, null, "NssfToDate", "NssfOpeningBalance", "NssfContributionsToDate", "NSSF Balance"),
    ];

    public sealed class Plan
    {
        public List<EmployeeLeaveBalance> NewLeave { get; } = [];
        public List<(EmployeeLeaveBalance Row, LeaveBalanceBefore Before, decimal Accrued, decimal Taken, decimal CarriedOver)> LeaveUpdates { get; } = [];
        public List<StaffLoan> Loans { get; } = [];
        public List<(Guid EmployeeId, Guid LegalEntityId, EmployeeStatutoryDetails? Existing, decimal Amount)> Nssf { get; } = [];
        public Dictionary<string, int> Summary { get; } = [];
        public List<string> Notes { get; } = [];
    }

    public async Task<Plan> PlanAsync(Guid legalEntityId, Sheet sheet, List<ImportProblem> problems, CancellationToken ct)
    {
        var plan = new Plan();
        var map = ColumnMap.Build(sheet, Columns, problems);
        if (!map.Has("EmployeeNumber")) problems.Add(new ImportProblem(1, "EmployeeNumber", "The file needs an EmployeeNumber column (staff number)."));
        var leaveCols = new[] { "LeaveBalance", "CarriedOver", "Taken", "Accrued" };
        var loanCols = new[] { "LoanBalance", "LoanInstalment", "LoanPrincipal" };
        if (!leaveCols.Concat(loanCols).Append("NssfBeforeEcosys").Any(map.Has))
            problems.Add(new ImportProblem(1, null, "The file has no balance columns. Give leave (LeaveBalance, or CarriedOver and Taken), loans (LoanBalance and LoanInstalment) or NssfBeforeEcosys."));
        if (map.Has("LeaveBalance") && (map.Has("CarriedOver") || map.Has("Taken")))
            problems.Add(new ImportProblem(1, "LeaveBalance", "Give either LeaveBalance or CarriedOver and Taken, not both."));
        if (problems.Count > 0) return plan;
        if (map.Unrecognised.Count > 0) plan.Notes.Add($"Not imported (columns this import doesn't use): {string.Join(", ", map.Unrecognised)}.");

        var employees = await db.Employees.Where(e => e.LegalEntityId == legalEntityId).ToDictionaryAsync(e => e.EmployeeNumber, StringComparer.OrdinalIgnoreCase, ct);
        var leaveTypes = await db.LeaveTypes.Where(t => t.LegalEntityId == legalEntityId && t.IsActive).ToListAsync(ct);
        var deductions = await db.SalaryComponents.Where(c => c.LegalEntityId == legalEntityId && c.IsActive && c.Type == ComponentType.Deduction).ToListAsync(ct);
        var existingRefs = (await db.StaffLoans.IgnoreQueryFilters().Where(l => l.LegalEntityId == legalEntityId).Select(l => l.Reference).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = employees.Values.Select(e => e.Id).ToList();
        var balances = await db.EmployeeLeaveBalances.Where(b => ids.Contains(b.EmployeeId)).ToListAsync(ct);
        var statutory = await db.EmployeeStatutoryDetails.Where(s => ids.Contains(s.EmployeeId)).ToDictionaryAsync(s => s.EmployeeId, ct);
        var today = BusinessClock.Today();
        var leaveSeen = new Dictionary<(Guid, Guid, int), int>();
        var nssfSeen = new Dictionary<Guid, (int Row, decimal Amount)>();
        var refsInFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var loanCount = new Dictionary<Guid, int>();

        foreach (var sheetRow in sheet.Rows)
        {
            var r = new RowReader(sheetRow, map, problems);
            var number = r.Text("EmployeeNumber", required: true);
            if (number is null) continue;
            if (!employees.TryGetValue(number, out var emp)) { r.Problem("EmployeeNumber", $"Employee {number} isn't in this company. Load employees first."); continue; }

            // ---- Leave ----
            decimal? balance = r.Money("LeaveBalance", allowNegative: true), carried = r.Money("CarriedOver"), taken = r.Money("Taken"), accrued = r.Money("Accrued");
            if (balance is not null || carried is not null || taken is not null || accrued is not null)
            {
                var typeCell = r.Text("LeaveType") ?? "ANNUAL";
                var key = ColumnMap.Normalise(typeCell);
                var type = leaveTypes.FirstOrDefault(t => ColumnMap.Normalise(t.Code) == key) ?? leaveTypes.FirstOrDefault(t => ColumnMap.Normalise(t.Name) == key);
                var year = r.Int("LeaveYear", 2000, today.Year + 1) ?? today.Year;
                if (type is null) r.Problem("LeaveType", $"Leave type '{typeCell}' doesn't exist in this company ({string.Join(", ", leaveTypes.Select(t => t.Code))}).");
                else if (leaveSeen.TryGetValue((emp.Id, type.Id, year), out var other)) r.Problem("LeaveType", $"{type.Name} {year} for {number} appears twice in the file (also row {other}).");
                else
                {
                    leaveSeen[(emp.Id, type.Id, year)] = r.Number;
                    var earned = accrued ?? LeaveBalanceService.Accrued(type, emp.HireDate, year, today);
                    decimal c, t;
                    if (balance is { } b)
                    {
                        // Only the days left are known: split them so that carried over + earned - taken = the balance given.
                        c = Math.Max(0, b - earned); t = Math.Max(0, earned - b);
                    }
                    else { c = carried ?? 0; t = taken ?? 0; }
                    var current = balances.FirstOrDefault(x => x.EmployeeId == emp.Id && x.LeaveTypeId == type.Id && x.Year == year);
                    if (current is null)
                        plan.NewLeave.Add(new EmployeeLeaveBalance { LegalEntityId = legalEntityId, EmployeeId = emp.Id, LeaveTypeId = type.Id, Year = year, Entitlement = type.AnnualEntitlementDays, Accrued = earned, Taken = t, CarriedOver = c });
                    else
                        plan.LeaveUpdates.Add((current, new LeaveBalanceBefore(current.Id, current.Entitlement, current.Accrued, current.Taken, current.CarriedOver), earned, t, c));
                }
            }

            // ---- Loans ----
            decimal? loanBalance = r.Money("LoanBalance"), instalment = r.Money("LoanInstalment"), principal = r.Money("LoanPrincipal");
            if (loanBalance is not null || instalment is not null || principal is not null)
            {
                var typeCell = r.Text("LoanType") ?? "LOAN";
                var key = ColumnMap.Normalise(typeCell);
                var component = deductions.FirstOrDefault(c => ColumnMap.Normalise(c.Code) == key) ?? deductions.FirstOrDefault(c => ColumnMap.Normalise(c.Name) == key);
                if (component is null) r.Problem("LoanType", $"'{typeCell}' isn't a deduction in this company. Use LOAN or ADVANCE, or add the deduction under Compensation first.");
                if (loanBalance is null or 0) r.Problem("LoanBalance", "A loan needs the amount still owed (LoanBalance). Leave settled loans out.");
                if (instalment is null or 0) r.Problem("LoanInstalment", "A loan needs the monthly instalment.");
                if (principal is { } p && loanBalance is { } lb && lb > p) r.Problem("LoanPrincipal", "The amount still owed is more than the amount lent.");
                var n = loanCount[emp.Id] = loanCount.GetValueOrDefault(emp.Id) + 1;
                var reference = r.Text("LoanReference") ?? $"MIG-{number}-{n}";
                if (existingRefs.Contains(reference)) r.Problem("LoanReference", $"Loan reference {reference} is already used in this company.");
                else if (refsInFile.TryGetValue(reference, out var other)) r.Problem("LoanReference", $"Loan reference {reference} appears twice in the file (also row {other}).");
                else refsInFile[reference] = r.Number;
                if (component is not null && loanBalance is > 0 && instalment is > 0 && !r.HasProblems)
                    plan.Loans.Add(new StaffLoan
                    {
                        LegalEntityId = legalEntityId, EmployeeId = emp.Id, SalaryComponentId = component.Id, Reference = reference, Principal = principal ?? loanBalance.Value,
                        Balance = loanBalance.Value, InstalmentAmount = instalment.Value, StartDate = r.Date("LoanStartDate") ?? today, Status = LoanStatus.Active,
                        Notes = "Brought across from the previous payroll system",
                    });
            }

            // ---- NSSF before Ecosys ----
            if (r.Money("NssfBeforeEcosys") is { } nssf)
            {
                if (nssfSeen.TryGetValue(emp.Id, out var seen))
                {
                    if (seen.Amount != nssf) r.Problem("NssfBeforeEcosys", $"{number} has a different NSSF total on row {seen.Row}.");
                }
                else
                {
                    nssfSeen[emp.Id] = (r.Number, nssf);
                    plan.Nssf.Add((emp.Id, legalEntityId, statutory.GetValueOrDefault(emp.Id), nssf));
                }
            }
        }

        // A row with any problem is left out whole, so nothing half-loads.
        var failed = problems.Select(p => p.Row).ToHashSet();
        if (failed.Count > 0)
        {
            var rowOf = sheet.Rows.ToDictionary(r => r, r => r.RowNumber);
            var badEmployees = sheet.Rows.Where(r => failed.Contains(r.RowNumber)).Select(r => map.IndexOf("EmployeeNumber") is { } i && i < r.Cells.Count ? r.Cells[i] : null)
                .Where(n => n is not null && employees.ContainsKey(n)).Select(n => employees[n!].Id).ToHashSet();
            plan.NewLeave.RemoveAll(x => badEmployees.Contains(x.EmployeeId));
            plan.LeaveUpdates.RemoveAll(x => badEmployees.Contains(x.Row.EmployeeId));
            plan.Loans.RemoveAll(x => badEmployees.Contains(x.EmployeeId));
            plan.Nssf.RemoveAll(x => badEmployees.Contains(x.EmployeeId));
        }
        plan.Summary["leaveBalances"] = plan.NewLeave.Count + plan.LeaveUpdates.Count;
        plan.Summary["loans"] = plan.Loans.Count;
        plan.Summary["nssfOpeningBalances"] = plan.Nssf.Count;
        if (plan.LeaveUpdates.Count > 0) plan.Notes.Add($"{plan.LeaveUpdates.Count} leave balance(s) already exist and will be overwritten. Undo puts the old figures back.");
        return plan;
    }

    public void Apply(Plan plan, ImportBatch batch)
    {
        foreach (var b in plan.NewLeave) { db.EmployeeLeaveBalances.Add(b); batch.Records.LeaveBalances.Add(b.Id); }
        foreach (var (row, before, accrued, taken, carried) in plan.LeaveUpdates)
        {
            row.Accrued = accrued; row.Taken = taken; row.CarriedOver = carried;
            batch.Records.LeaveBalanceUpdates.Add(before);
        }
        foreach (var l in plan.Loans) { db.StaffLoans.Add(l); batch.Records.Loans.Add(l.Id); }
        foreach (var (employeeId, legalEntityId, existing, amount) in plan.Nssf)
        {
            if (existing is null)
            {
                var s = new EmployeeStatutoryDetails { LegalEntityId = legalEntityId, EmployeeId = employeeId, NssfOpeningBalance = amount };
                db.EmployeeStatutoryDetails.Add(s);
                batch.Records.NssfUpdates.Add(new NssfOpeningBefore(employeeId, null, 0m));
            }
            else
            {
                batch.Records.NssfUpdates.Add(new NssfOpeningBefore(employeeId, existing.Id, existing.NssfOpeningBalance));
                existing.NssfOpeningBalance = amount;
            }
        }
    }
}
