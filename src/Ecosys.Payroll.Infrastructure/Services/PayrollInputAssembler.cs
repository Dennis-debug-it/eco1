using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services;

/// <summary>Everything loaded once per run and reused for every employee.</summary>
public sealed record RunContext(
    PayrollRun Run,
    PayrollPeriod Period,
    LegalEntity Entity,
    IReadOnlyDictionary<Guid, SalaryComponent> Components,
    IReadOnlyDictionary<Guid, SalaryStructure> Structures,
    IReadOnlyList<Employee> Employees,
    IReadOnlyDictionary<Guid, EmployeeSalaryAssignment> Assignments,
    IReadOnlyDictionary<Guid, EmployeeStatutoryDetails> Statutory,
    IReadOnlyDictionary<Guid, List<PayrollVariableInput>> Variables,
    IReadOnlyDictionary<Guid, List<StaffLoan>> Loans,
    IReadOnlyDictionary<Guid, decimal> UnpaidLeaveDays,
    IReadOnlyDictionary<Guid, List<PayrollAdjustment>> Adjustments,
    IReadOnlyDictionary<Guid, decimal> PreviousNetPay,
    IReadOnlyDictionary<Guid, EmployeeBankAccount> PrimaryBankAccounts,
    PayrollGroup Group,
    IReadOnlyDictionary<Guid, AttendanceRecord> Attendance)
{
    public PeriodFrequency Frequency => Group.Frequency switch
    {
        PayFrequency.Weekly => PeriodFrequency.Weekly,
        PayFrequency.SemiMonthly => PeriodFrequency.SemiMonthly,
        _ => PeriodFrequency.Monthly,
    };

    /// <summary>Salary assignments and fixed components are monthly amounts; this converts them to the period.</summary>
    public decimal PeriodFactor => Frequency switch { PeriodFrequency.Weekly => 12m / 52m, PeriodFrequency.SemiMonthly => 0.5m, _ => 1m };
}

/// <summary>
/// Translates persisted configuration into the engine's <see cref="EmployeePayrollInput"/> (PRD §4.1 steps 1–5).
/// The engine stays free of persistence concerns; this class owns "which employees, with what inputs".
/// </summary>
public sealed class PayrollInputAssembler(PayrollDbContext db)
{
    public async Task<RunContext> LoadAsync(PayrollRun run, CancellationToken ct)
    {
        var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PayrollPeriodId, ct);
        var entity = await db.LegalEntities.FirstAsync(e => e.Id == run.LegalEntityId, ct);

        var components = await db.SalaryComponents.Where(c => c.LegalEntityId == run.LegalEntityId).AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var structures = await db.SalaryStructures.Include(s => s.Lines).Where(s => s.LegalEntityId == run.LegalEntityId).AsNoTracking().ToDictionaryAsync(s => s.Id, ct);

        var employees = await db.Employees
            .Where(e => e.LegalEntityId == run.LegalEntityId && e.PayrollGroupId == run.PayrollGroupId)
            .AsNoTracking().ToListAsync(ct);
        employees = employees.Where(e => e.IsPayable(period.PeriodStart, period.PeriodEnd)).OrderBy(e => e.EmployeeNumber).ToList();
        var ids = employees.Select(e => e.Id).ToList();

        var assignments = (await db.EmployeeSalaryAssignments.Include(a => a.Lines)
                .Where(a => ids.Contains(a.EmployeeId) && a.EffectiveFrom <= period.PeriodEnd && (a.EffectiveTo == null || a.EffectiveTo >= period.PeriodEnd))
                .AsNoTracking().ToListAsync(ct))
            .GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.EffectiveFrom).First());

        var statutory = await db.EmployeeStatutoryDetails.Where(s => ids.Contains(s.EmployeeId)).AsNoTracking().ToDictionaryAsync(s => s.EmployeeId, ct);

        var variables = (await db.PayrollVariableInputs.Where(v => v.PayrollRunId == run.Id).AsNoTracking().ToListAsync(ct))
            .GroupBy(v => v.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());

        var loans = (await db.StaffLoans.Where(l => ids.Contains(l.EmployeeId) && l.Status == LoanStatus.Active && l.StartDate <= period.PeriodEnd).AsNoTracking().ToListAsync(ct))
            .GroupBy(l => l.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());

        var unpaidTypes = await db.LeaveTypes.Where(t => t.LegalEntityId == run.LegalEntityId && !t.IsPaid).Select(t => t.Id).ToListAsync(ct);
        var unpaid = (await db.LeaveRequests
                .Where(l => ids.Contains(l.EmployeeId) && unpaidTypes.Contains(l.LeaveTypeId) && l.Status == LeaveRequestStatus.Approved
                            && l.ProcessedInRunId == null && l.StartDate <= period.PeriodEnd && l.EndDate >= period.PeriodStart)
                .AsNoTracking().ToListAsync(ct))
            .GroupBy(l => l.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(l => OverlapDays(l, period)));

        var adjustments = (await db.PayrollAdjustments
                .Where(a => ids.Contains(a.EmployeeId) && a.Status == AdjustmentStatus.Pending && a.ApprovedBy != null)
                .AsNoTracking().ToListAsync(ct))
            .GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());

        var previousRunId = await db.PayrollRuns
            .Where(r => r.PayrollGroupId == run.PayrollGroupId && r.Status == PayrollRunStatus.Finalised && r.Id != run.Id && r.RunType == PayrollRunType.Regular)
            .OrderByDescending(r => r.FinalisedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        var previous = previousRunId is null
            ? new Dictionary<Guid, decimal>()
            : await db.PayrollRunEmployees.Where(e => e.PayrollRunId == previousRunId).ToDictionaryAsync(e => e.EmployeeId, e => e.NetPay, ct);

        var banks = (await db.EmployeeBankAccounts.Where(b => ids.Contains(b.EmployeeId) && b.IsVerified).AsNoTracking().ToListAsync(ct))
            .GroupBy(b => b.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.IsPrimary).First());
        var group = await db.PayrollGroups.FirstAsync(g => g.Id == run.PayrollGroupId, ct);
        var attendance = await db.AttendanceRecords.Where(a => a.PayrollRunId == run.Id).AsNoTracking().ToDictionaryAsync(a => a.EmployeeId, ct);

        return new RunContext(run, period, entity, components, structures, employees, assignments, statutory, variables, loans, unpaid, adjustments, previous, banks, group, attendance);
    }

    private static decimal OverlapDays(LeaveRequest l, PayrollPeriod p)
    {
        var start = l.StartDate > p.PeriodStart ? l.StartDate : p.PeriodStart;
        var end = l.EndDate < p.PeriodEnd ? l.EndDate : p.PeriodEnd;
        var total = l.EndDate.DayNumber - l.StartDate.DayNumber + 1;
        var overlap = end.DayNumber - start.DayNumber + 1;
        if (overlap <= 0 || total <= 0) return 0;
        // Pro-rate the recorded (working-day) count by the calendar overlap.
        return Math.Round(l.Days * overlap / total, 2);
    }

    /// <summary>Builds the engine input for one employee, or returns the reasons it cannot be built.</summary>
    public (EmployeePayrollInput? Input, List<PayrollValidationIssue> Issues) Build(RunContext ctx, Employee emp)
    {
        var issues = new List<PayrollValidationIssue>();
        if (!ctx.Assignments.TryGetValue(emp.Id, out var assignment))
        {
            issues.Add(new(ValidationSeverity.Error, "NO_SALARY", "No salary assignment effective in this period."));
            return (null, issues);
        }

        var earnings = new List<EarningInput>();
        var deductions = new List<DeductionInput>();
        var monthlyBasic = assignment.BasicSalary;
        var basic = Math.Round(monthlyBasic * ctx.PeriodFactor, 2);

        // Days worked pro-rating for joiners/leavers inside the period.
        var daysInPeriod = ctx.Period.PeriodEnd.DayNumber - ctx.Period.PeriodStart.DayNumber + 1;
        var from = emp.HireDate > ctx.Period.PeriodStart ? emp.HireDate : ctx.Period.PeriodStart;
        var to = emp.TerminationDate is { } td && td < ctx.Period.PeriodEnd ? td : ctx.Period.PeriodEnd;
        var daysPaid = to.DayNumber - from.DayNumber + 1;
        var proration = daysPaid < daysInPeriod ? (decimal)daysPaid / daysInPeriod : 1m;

        var basicComponent = ctx.Components.Values.FirstOrDefault(c => c.IsBasic && c.IsActive);
        earnings.Add(new EarningInput(basicComponent?.Code ?? "BASIC", basicComponent?.Name ?? "Basic salary", Math.Round(basic * proration, 2),
            Taxable: basicComponent?.Taxable ?? true, Pensionable: basicComponent?.Pensionable ?? true, IsBasic: true,
            ComponentId: basicComponent?.Id, GlAccountCode: basicComponent?.GlAccountCode));

        // Structure lines, then per-employee lines override by component.
        var lines = new Dictionary<Guid, (CalculationMethod Method, decimal? Amount, decimal? Rate)>();
        if (assignment.SalaryStructureId is { } sid && ctx.Structures.TryGetValue(sid, out var structure))
            foreach (var l in structure.Lines.OrderBy(l => l.SortOrder)) lines[l.SalaryComponentId] = (l.Method, l.Amount, l.Rate);
        foreach (var l in assignment.Lines) lines[l.SalaryComponentId] = (l.Method, l.Amount, l.Rate);

        foreach (var (componentId, cfg) in lines)
        {
            if (!ctx.Components.TryGetValue(componentId, out var c) || !c.IsActive || c.IsBasic) continue;
            if (cfg.Method == CalculationMethod.Variable) continue; // supplied per run
            var amount = cfg.Method switch
            {
                CalculationMethod.FixedAmount => Math.Round((cfg.Amount ?? c.DefaultAmount ?? 0m) * ctx.PeriodFactor, 2),
                CalculationMethod.PercentOfBasic => Math.Round(basic * (cfg.Rate ?? c.DefaultRate ?? 0m), 2),
                CalculationMethod.PercentOfGross => 0m, // resolved after gross below
                _ => 0m,
            };
            if (c.Type == ComponentType.Earning) amount = Math.Round(amount * proration, 2);
            AddComponent(c, amount, earnings, deductions);
        }

        // Percent-of-gross components (e.g. pension 5% of gross earnings) are computed on the cash earnings assembled so far.
        var grossSoFar = earnings.Sum(e => e.Amount);
        foreach (var (componentId, cfg) in lines.Where(l => l.Value.Method == CalculationMethod.PercentOfGross))
        {
            if (!ctx.Components.TryGetValue(componentId, out var c) || !c.IsActive) continue;
            AddComponent(c, Math.Round(grossSoFar * (cfg.Rate ?? c.DefaultRate ?? 0m), 2), earnings, deductions);
        }

        // Variable inputs for this run.
        if (ctx.Variables.TryGetValue(emp.Id, out var vars))
            foreach (var v in vars)
                if (ctx.Components.TryGetValue(v.SalaryComponentId, out var c)) AddComponent(c, v.Amount, earnings, deductions);

        // Loan instalments.
        if (ctx.Loans.TryGetValue(emp.Id, out var loans))
            foreach (var loan in loans)
            {
                var instalment = Math.Min(loan.InstalmentAmount, loan.Balance);
                if (instalment <= 0 || !ctx.Components.TryGetValue(loan.SalaryComponentId, out var c)) continue;
                deductions.Add(new DeductionInput(c.Code, $"{c.Name} ({loan.Reference})", instalment, c.DeductionTreatment, 0m, c.Id, c.GlAccountCode));
            }

        // Approved adjustments carried into this run.
        if (ctx.Adjustments.TryGetValue(emp.Id, out var adjs))
            foreach (var a in adjs)
                if (ctx.Components.TryGetValue(a.SalaryComponentId, out var c))
                    AddComponent(c, a.Amount, earnings, deductions, $"{c.Name} (adjustment: {a.Type})");

        // Unpaid leave and unauthorised absence are valued at the daily rate of monthly basic.
        var settings = ctx.Entity.Settings;
        // Guard against settings stored before these fields existed (absent JSON keys load as 0).
        var workingDays = settings.StandardMonthlyWorkingDays > 0 ? settings.StandardMonthlyWorkingDays : 22m;
        var monthlyHours = settings.StandardMonthlyHours > 0 ? settings.StandardMonthlyHours : 195m;
        var otMultiplier = settings.OvertimeMultiplier > 0 ? settings.OvertimeMultiplier : 1.5m;
        var restMultiplier = settings.RestDayOvertimeMultiplier > 0 ? settings.RestDayOvertimeMultiplier : 2.0m;
        var dailyRate = monthlyBasic / workingDays;
        var unpaidDays = ctx.UnpaidLeaveDays.GetValueOrDefault(emp.Id);
        if (ctx.Attendance.TryGetValue(emp.Id, out var att)) unpaidDays += att.AbsentDays;
        var unpaidAmount = unpaidDays > 0 ? Math.Min(basic, Math.Round(dailyRate * unpaidDays, 2)) : 0m;
        if (unpaidDays > 0) issues.Add(new(ValidationSeverity.Warning, "UNPAID_DAYS", $"{unpaidDays:0.##} unpaid day(s) deducted at {dailyRate:N2}/day."));

        // Overtime from attendance, valued at the hourly rate of monthly basic.
        if (att is not null && (att.OvertimeHours > 0 || att.RestDayOvertimeHours > 0))
        {
            var hourly = monthlyBasic / monthlyHours;
            var ot = ctx.Components.Values.FirstOrDefault(c => c.Code == "OT" && c.IsActive);
            void AddOt(decimal hours, decimal multiplier, string label)
            {
                if (hours <= 0) return;
                var amt = Math.Round(hours * hourly * multiplier, 2);
                earnings.Add(new EarningInput(ot?.Code ?? "OT", $"{ot?.Name ?? "Overtime"} {label} ({hours:0.##}h x {multiplier:0.##})", amt,
                    ot?.Taxable ?? true, ot?.Pensionable ?? false, false, ot?.Id, ot?.GlAccountCode));
            }
            AddOt(att.OvertimeHours, otMultiplier, "normal");
            AddOt(att.RestDayOvertimeHours, restMultiplier, "rest day/holiday");
        }

        ctx.Statutory.TryGetValue(emp.Id, out var stat);
        if (stat is null || string.IsNullOrWhiteSpace(stat.KraPin)) issues.Add(new(ValidationSeverity.Error, "NO_KRA_PIN", "KRA PIN is missing."));
        if (stat is null || string.IsNullOrWhiteSpace(stat.NssfNumber)) issues.Add(new(ValidationSeverity.Warning, "NO_NSSF", "NSSF number is missing."));
        if (stat is null || string.IsNullOrWhiteSpace(stat.ShifNumber)) issues.Add(new(ValidationSeverity.Warning, "NO_SHIF", "SHIF number is missing."));
        if (!ctx.PrimaryBankAccounts.ContainsKey(emp.Id)) issues.Add(new(ValidationSeverity.Error, "NO_BANK", "No verified bank account on file."));
        if (proration < 1m) issues.Add(new(ValidationSeverity.Warning, "PRORATED", $"Pay pro-rated for {daysPaid} of {daysInPeriod} days."));

        var exemptions = stat?.StatutoryExemptions ?? [];
        var disabilityValid = stat is { HasDisabilityExemption: true } && (stat.DisabilityExemptionExpiry is null || stat.DisabilityExemptionExpiry >= ctx.Period.PeriodEnd);

        return (new EmployeePayrollInput
        {
            EmployeeId = emp.Id,
            EmployeeNumber = emp.EmployeeNumber,
            DisplayName = emp.FullName,
            Category = emp.EmploymentType.ToString(),
            HasDisabilityExemption = disabilityValid,
            DisabilityExemptAmount = disabilityValid ? stat!.DisabilityExemptAmount : 0m,
            StatutoryExemptions = exemptions,
            Earnings = earnings,
            Deductions = deductions,
            UnpaidLeaveDeduction = unpaidAmount,
        }, issues);
    }

    private static void AddComponent(SalaryComponent c, decimal amount, List<EarningInput> earnings, List<DeductionInput> deductions, string? name = null)
    {
        if (amount == 0) return;
        if (c.Type == ComponentType.Earning)
            earnings.Add(new EarningInput(c.Code, name ?? c.Name, amount, c.Taxable, c.Pensionable, false, c.Id, c.GlAccountCode));
        else
            deductions.Add(new DeductionInput(c.Code, name ?? c.Name, amount, c.DeductionTreatment, Math.Round(amount * c.EmployerMatchRate, 2), c.Id, c.GlAccountCode));
    }
}
