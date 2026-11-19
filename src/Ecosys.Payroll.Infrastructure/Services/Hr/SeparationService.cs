using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Hr;

public sealed record SeparationRequest(Guid EmployeeId, SeparationType Type, DateOnly NoticeDate, DateOnly LastWorkingDay, int NoticeDaysServed, bool SettleNoticeInLieu, string Reason);

/// <summary>
/// Exits: initiation with an offboarding checklist, final-dues calculation (Employment Act 2007: notice s.35/s.36,
/// untaken leave, severance s.40(1)(g)), recoveries, maker-checker approval that hands the dues to payroll as
/// scheduled items for the employee's final regular run, and completion.
/// </summary>
public sealed class SeparationService(PayrollDbContext db, ITenantContext tenant, AuditService audit, SystemComponents components, LeaveBalanceService balances)
{
    public async Task<Separation> InitiateAsync(SeparationRequest req, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrSeparations);
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == req.EmployeeId, ct) ?? throw new NotFoundException(nameof(Employee), req.EmployeeId);
        tenant.RequireLegalEntity(emp.LegalEntityId);
        if (emp.TerminationDate is not null) throw new InvalidStateException("The employee already has a termination date.");
        if (await db.Separations.AnyAsync(s => s.EmployeeId == emp.Id && s.Status != SeparationStatus.Cancelled, ct)) throw new InvalidStateException("An exit is already in progress for this employee.");
        if (req.LastWorkingDay < req.NoticeDate) throw new DomainException("The last working day is before the notice date.", "VALIDATION");
        if (req.LastWorkingDay < emp.HireDate) throw new DomainException("The last working day is before the hire date.", "VALIDATION");
        if (string.IsNullOrWhiteSpace(req.Reason)) throw new DomainException("A reason is required.", "VALIDATION");
        if (req.NoticeDaysServed < 0) throw new DomainException("Notice days served cannot be negative.", "VALIDATION");

        var s = new Separation
        {
            LegalEntityId = emp.LegalEntityId, EmployeeId = emp.Id, Type = req.Type, NoticeDate = req.NoticeDate, LastWorkingDay = req.LastWorkingDay,
            NoticeDaysServed = req.NoticeDaysServed, PayNoticeInLieu = req.SettleNoticeInLieu, Reason = req.Reason.Trim(), InitiatedBy = tenant.RequireUser(),
            Checklist = await BuildChecklistAsync(emp, ct), Status = SeparationStatus.Clearance,
        };
        db.Separations.Add(s);
        audit.Record("separation.initiated", nameof(Separation), s.Id, new { emp.EmployeeNumber, req.Type, req.LastWorkingDay }, emp.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return s;
    }

    private async Task<List<ChecklistItem>> BuildChecklistAsync(Employee emp, CancellationToken ct)
    {
        var items = new List<ChecklistItem>
        {
            new() { Key = "handover", Title = "Handover of work completed", Owner = "Line manager" },
            new() { Key = "exit-interview", Title = "Exit interview held", Owner = "HR" },
            new() { Key = "access", Title = "System and building access revoked on last day", Owner = "IT" },
            new() { Key = "finance-clearance", Title = "Finance clearance (advances, imprests, petty cash)", Owner = "Finance" },
            new() { Key = "certificate", Title = "Certificate of service issued (Employment Act s.51)", Owner = "HR" },
        };
        var assets = await db.AssetAssignments.Where(a => a.EmployeeId == emp.Id && a.ReturnedOn == null)
            .Join(db.Assets, a => a.AssetId, x => x.Id, (a, x) => new { a.Id, x.Tag, x.Name }).ToListAsync(ct);
        items.AddRange(assets.Select(a => new ChecklistItem { Key = $"asset:{a.Id}", Title = $"Return {a.Name} ({a.Tag})", Owner = "Assets" }));
        return items;
    }

    public async Task<Separation> UpdateChecklistAsync(Guid id, string key, bool done, string? note, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrSeparations);
        var s = await LoadAsync(id, ct);
        if (s.Status is SeparationStatus.Completed or SeparationStatus.Cancelled) throw new InvalidStateException("This exit is closed.");
        var item = s.Checklist.FirstOrDefault(i => i.Key == key) ?? throw new DomainException($"Checklist item '{key}' not found.", "VALIDATION");
        item.Done = done; item.Note = note; item.DoneBy = done ? tenant.UserName : null; item.DoneAt = done ? DateTimeOffset.UtcNow : null;
        // Returning an asset closes its assignment so it is not charged in final dues.
        if (key.StartsWith("asset:") && Guid.TryParse(key[6..], out var assignmentId))
        {
            var a = await db.AssetAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (a is not null)
            {
                a.ReturnedOn = done ? BusinessClock.Today() : null; a.ConditionIn = note;
                var asset = await db.Assets.FirstAsync(x => x.Id == a.AssetId, ct);
                asset.Status = done ? AssetStatus.InStock : AssetStatus.Issued;
            }
        }
        s.Checklist = [.. s.Checklist]; // new instance so the JSON column is detected as changed
        audit.Record("separation.checklist", nameof(Separation), id, new { key, done, note }, s.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return s;
    }

    /// <summary>Computes final dues. Pro-rata salary for the last month is paid by the regular payroll (termination date).</summary>
    public async Task<Separation> CalculateAsync(Guid id, CancellationToken ct = default)
    {
        // HR runs the exit; dues involve pay, so they are calculated by someone with salary visibility (payroll).
        tenant.Require(Permissions.SalaryRead);
        if (!tenant.Has(Permissions.HrSeparations) && !tenant.Has(Permissions.PayrollPrepare)) throw new ForbiddenException("HR or payroll permission required.");
        var s = await LoadAsync(id, ct);
        if (s.Status is not (SeparationStatus.Initiated or SeparationStatus.Clearance or SeparationStatus.DuesCalculated)) throw new InvalidStateException($"Dues cannot be recalculated once the exit is {s.Status}.");
        s.Dues = await ComputeAsync(s, ct);
        s.Status = SeparationStatus.DuesCalculated;
        audit.Record("separation.dues.calculated", nameof(Separation), id, new { earnings = s.Dues.TotalEarnings, recoveries = s.Dues.TotalRecoveries }, s.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return s;
    }

    private async Task<FinalDues> ComputeAsync(Separation s, CancellationToken ct)
    {
        var emp = await db.Employees.FirstAsync(e => e.Id == s.EmployeeId, ct);
        var entity = await db.LegalEntities.FirstAsync(e => e.Id == s.LegalEntityId, ct);
        var basic = (await db.EmployeeSalaryAssignments.Where(a => a.EmployeeId == emp.Id && a.EffectiveFrom <= s.LastWorkingDay)
            .OrderByDescending(a => a.EffectiveFrom).FirstOrDefaultAsync(ct))?.BasicSalary ?? throw new InvalidStateException("The employee has no salary assignment.");
        var workingDays = entity.Settings.StandardMonthlyWorkingDays > 0 ? entity.Settings.StandardMonthlyWorkingDays : 22m;
        var dailyWorking = Math.Round(basic / workingDays, 2);
        var dailyCalendar = Math.Round(basic / 30m, 2);
        var years = CompletedYears(emp.HireDate, s.LastWorkingDay);
        var dues = new FinalDues
        {
            MonthlyBasic = basic, DailyRateWorking = dailyWorking, DailyRateCalendar = dailyCalendar, CompletedYearsOfService = years,
            PayableInPeriodEnding = new DateOnly(s.LastWorkingDay.Year, s.LastWorkingDay.Month, 1).AddMonths(1).AddDays(-1),
        };
        dues.Notes.Add($"Salary to {s.LastWorkingDay:dd MMM yyyy} is paid pro-rata by the regular payroll for that month; the items below are added to that run.");

        // Notice (s.35/s.36): shortfall against the contractual period is paid in lieu by the employer when it ends the
        // contract, or recovered from an employee who resigns without serving it (only if settlement in lieu is chosen).
        var shortfall = Math.Max(0, emp.NoticePeriodDays - s.NoticeDaysServed);
        if (shortfall > 0 && s.PayNoticeInLieu)
        {
            var amount = Math.Round(shortfall * dailyCalendar, 2);
            if (s.Type is SeparationType.Termination or SeparationType.Redundancy)
                dues.Lines.Add(new FinalDuesLine { Code = "NOTICE_PAY", Description = "Pay in lieu of notice", Amount = amount, Working = $"{shortfall} of {emp.NoticePeriodDays} notice days not served × {dailyCalendar:N2}/day (basic ÷ 30)" });
            else if (s.Type == SeparationType.Resignation)
                dues.Lines.Add(new FinalDuesLine { Code = "NOTICE_RECOVERY", Description = "Notice not served (recovered)", Amount = -amount, Working = $"{shortfall} of {emp.NoticePeriodDays} notice days not served × {dailyCalendar:N2}/day" });
        }
        if (s.Type == SeparationType.SummaryDismissal) dues.Notes.Add("Summary dismissal (s.44): no notice pay. Accrued leave and salary to the last day are still due.");

        // Untaken annual leave (accrued to the last working day) is paid; overdrawn leave is recovered.
        await balances.EnsureAsync(emp.Id, s.LastWorkingDay.Year, ct);
        await db.SaveChangesAsync(ct);
        var annualTypes = await db.LeaveTypes.Where(t => t.LegalEntityId == emp.LegalEntityId && t.IsPaid && t.AccruesMonthly && t.FullPayDaysPerYear == null).ToListAsync(ct);
        foreach (var t in annualTypes)
        {
            var bal = await db.EmployeeLeaveBalances.FirstOrDefaultAsync(b => b.EmployeeId == emp.Id && b.LeaveTypeId == t.Id && b.Year == s.LastWorkingDay.Year, ct);
            if (bal is null) continue;
            var accruedToExit = LeaveBalanceService.Accrued(t, emp.HireDate, s.LastWorkingDay.Year, s.LastWorkingDay);
            var pending = await db.LeaveRequests.Where(r => r.EmployeeId == emp.Id && r.LeaveTypeId == t.Id && r.Status == LeaveRequestStatus.Approved && r.StartDate > s.LastWorkingDay).SumAsync(r => r.Days, ct);
            var available = bal.CarriedOver + accruedToExit - (bal.Taken - pending);
            if (available == 0) continue;
            var amount = Math.Round(available * dailyWorking, 2);
            dues.Lines.Add(available > 0
                ? new FinalDuesLine { Code = "LEAVE_PAY", Description = $"{t.Name}: {available:0.##} untaken day(s)", Amount = amount, Working = $"{bal.CarriedOver:0.##} carried + {accruedToExit:0.##} accrued to exit − {bal.Taken - pending:0.##} taken = {available:0.##} × {dailyWorking:N2}/day (basic ÷ {workingDays:0})" }
                : new FinalDuesLine { Code = "LEAVE_PAY", Description = $"{t.Name}: {-available:0.##} day(s) taken in advance", Amount = amount, Working = $"Overdrawn {available:0.##} × {dailyWorking:N2}/day" });
        }

        // Severance on redundancy: at least 15 days' pay per completed year of service (s.40(1)(g)).
        if (s.Type == SeparationType.Redundancy)
        {
            if (years > 0)
                dues.Lines.Add(new FinalDuesLine { Code = "SEVERANCE", Description = "Severance pay (redundancy)", Amount = Math.Round(15m * years * dailyCalendar, 2), Working = $"15 days × {years} completed year(s) × {dailyCalendar:N2}/day" });
            dues.Notes.Add("Redundancy also requires one month's notice to the employee/union and the Labour Officer (s.40(1)(a)–(b)) and fair selection criteria.");
        }

        // Recoveries.
        foreach (var bond in await db.TrainingBonds.Where(b => b.EmployeeId == emp.Id && b.Status == BondStatus.Active).ToListAsync(ct))
        {
            var outstanding = bond.OutstandingOn(s.LastWorkingDay);
            if (outstanding > 0)
                dues.Lines.Add(new FinalDuesLine { Code = "BOND_RECOVERY", Description = "Training bond (unexpired portion)", Amount = -outstanding, Working = $"{bond.Amount:N2} over {bond.Months} months from {bond.StartDate:dd MMM yyyy}, pro-rata to {s.LastWorkingDay:dd MMM yyyy}" });
        }
        var unreturned = await db.AssetAssignments.Where(a => a.EmployeeId == emp.Id && a.ReturnedOn == null && a.ChargeableIfNotReturned)
            .Join(db.Assets, a => a.AssetId, x => x.Id, (a, x) => new { x.Tag, x.Name, x.Value }).ToListAsync(ct);
        foreach (var a in unreturned.Where(a => a.Value > 0))
            dues.Lines.Add(new FinalDuesLine { Code = "ASSET_RECOVERY", Description = $"Unreturned: {a.Name} ({a.Tag})", Amount = -a.Value, Working = "Book value; remove if returned before approval" });
        var loanBalance = await db.StaffLoans.Where(l => l.EmployeeId == emp.Id && l.Status == Domain.Compensation.LoanStatus.Active).SumAsync(l => (decimal?)l.Balance, ct) ?? 0;
        if (loanBalance > 0)
            dues.Lines.Add(new FinalDuesLine { Code = "LOAN_BALANCE", Description = "Outstanding staff loans (recovered by the final payroll)", Amount = -loanBalance, Working = "Full balance deducted in the final run" });

        if (s.Type == SeparationType.Death) dues.Notes.Add("Death in service: dues are payable to the estate or nominated beneficiaries; check the Dependants record and pension/insurance claims.");
        dues.Notes.Add("PAYE, NSSF, SHIF and Housing Levy are computed by the final payroll on these amounts. Deductions are capped by s.19(3) (net pay should not fall below one-third of wages) — review any warning on the final run.");
        return dues;
    }

    public static int CompletedYears(DateOnly hire, DateOnly exit)
    {
        var years = exit.Year - hire.Year;
        if (exit < hire.AddYears(years)) years--;
        return Math.Max(0, years);
    }

    /// <summary>Maker-checker approval: sets the termination date and hands the dues to payroll as scheduled items.</summary>
    public async Task<Separation> ApproveAsync(Guid id, CancellationToken ct = default)
    {
        tenant.Require(Permissions.PayrollApprove);
        var s = await LoadAsync(id, ct);
        if (s.Status != SeparationStatus.DuesCalculated || s.Dues is null) throw new InvalidStateException("Calculate final dues before approval.");
        if (s.InitiatedBy == tenant.UserId && !tenant.IsPlatformAdministrator) throw new ForbiddenException("The person who initiated the exit cannot approve the final dues (separation of duties).");
        // Recalculate at approval so the frozen figures reflect the latest balances.
        s.Dues = await ComputeAsync(s, ct);

        var emp = await db.Employees.FirstAsync(e => e.Id == s.EmployeeId, ct);
        foreach (var line in s.Dues.Lines.Where(l => l.Code != "LOAN_BALANCE" && l.Amount != 0))
        {
            // Overdrawn leave is recovered through a deduction component; every other line maps to its own component.
            var code = line.Code == "LEAVE_PAY" && line.Amount < 0 ? "LEAVE_RECOVERY" : line.Code;
            var component = await components.EnsureAsync(emp.TenantId, emp.LegalEntityId, code, ct);
            db.ScheduledPayItems.Add(new ScheduledPayItem
            {
                LegalEntityId = emp.LegalEntityId, EmployeeId = emp.Id, SalaryComponentId = component.Id, Amount = Math.Abs(line.Amount), DueDate = s.LastWorkingDay,
                Description = line.Description, SourceType = nameof(Separation), SourceId = s.Id,
            });
        }
        foreach (var bond in await db.TrainingBonds.Where(b => b.EmployeeId == emp.Id && b.Status == BondStatus.Active).ToListAsync(ct))
            bond.Status = bond.OutstandingOn(s.LastWorkingDay) > 0 ? BondStatus.Recovered : BondStatus.Fulfilled;

        emp.TerminationDate = s.LastWorkingDay;
        emp.Status = s.Type switch
        {
            SeparationType.Resignation => EmploymentStatus.Resigned,
            SeparationType.Retirement => EmploymentStatus.Retired,
            SeparationType.Death => EmploymentStatus.Deceased,
            _ => EmploymentStatus.Terminated,
        };
        var open = await db.EmploymentRecords.Where(r => r.EmployeeId == emp.Id && r.EffectiveTo == null).ToListAsync(ct);
        foreach (var r in open) r.EffectiveTo = s.LastWorkingDay;
        db.EmploymentRecords.Add(new EmploymentRecord
        {
            LegalEntityId = emp.LegalEntityId, EmployeeId = emp.Id, EffectiveFrom = s.LastWorkingDay.AddDays(1), EmploymentType = emp.EmploymentType, Status = emp.Status,
            JobTitle = emp.JobTitle, DepartmentId = emp.DepartmentId, BranchId = emp.BranchId, CostCentreId = emp.CostCentreId, PayrollGroupId = emp.PayrollGroupId, ChangeReason = $"Exit: {s.Type}",
        });

        s.Status = SeparationStatus.Approved; s.ApprovedBy = tenant.UserId; s.ApprovedAt = DateTimeOffset.UtcNow;
        audit.Record("separation.approved", nameof(Separation), id, new { emp.EmployeeNumber, s.LastWorkingDay, earnings = s.Dues.TotalEarnings, recoveries = s.Dues.TotalRecoveries }, s.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return s;
    }

    public async Task<Separation> CompleteAsync(Guid id, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrSeparations);
        var s = await LoadAsync(id, ct);
        if (s.Status != SeparationStatus.Approved) throw new InvalidStateException("Only an approved exit can be completed.");
        var open = s.Checklist.Where(i => !i.Done).Select(i => i.Title).ToList();
        if (open.Count > 0) throw new InvalidStateException($"Checklist items still open: {string.Join("; ", open)}.");
        if (await db.ScheduledPayItems.AnyAsync(i => i.SourceType == nameof(Separation) && i.SourceId == s.Id && i.Status == PayItemStatus.Pending, ct))
            throw new InvalidStateException("Final dues have not yet been paid by a finalised payroll run.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmployeeId == s.EmployeeId, ct);
        if (user is not null)
        {
            // Keep self-service access for payslips/P9 but drop every other role.
            user.Roles = user.Roles.Contains(Roles.Employee) ? [Roles.Employee] : [];
            user.IsActive = user.Roles.Count > 0;
        }
        s.Status = SeparationStatus.Completed;
        audit.Record("separation.completed", nameof(Separation), id, null, s.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return s;
    }

    public async Task<Separation> CancelAsync(Guid id, string reason, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrSeparations);
        var s = await LoadAsync(id, ct);
        if (s.Status is SeparationStatus.Completed or SeparationStatus.Cancelled) throw new InvalidStateException("This exit is closed.");
        if (s.Status == SeparationStatus.Approved)
        {
            if (await db.ScheduledPayItems.AnyAsync(i => i.SourceType == nameof(Separation) && i.SourceId == s.Id && i.Status == PayItemStatus.Processed, ct))
                throw new InvalidStateException("Final dues have already been paid; reverse the payroll run first.");
            await db.ScheduledPayItems.Where(i => i.SourceType == nameof(Separation) && i.SourceId == s.Id).ExecuteUpdateAsync(x => x.SetProperty(i => i.Status, PayItemStatus.Cancelled), ct);
            var emp = await db.Employees.FirstAsync(e => e.Id == s.EmployeeId, ct);
            emp.TerminationDate = null; emp.Status = EmploymentStatus.Active;
        }
        s.Status = SeparationStatus.Cancelled;
        audit.Record("separation.cancelled", nameof(Separation), id, new { reason }, s.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return s;
    }

    private async Task<Separation> LoadAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Separations.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Separation), id);
        tenant.RequireLegalEntity(s.LegalEntityId);
        return s;
    }
}
