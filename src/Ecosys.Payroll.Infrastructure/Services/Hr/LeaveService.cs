using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Hr;

public sealed record LeaveApplication(Guid LeaveTypeId, DateOnly StartDate, DateOnly EndDate, decimal? Days, string? Reason, Guid? EmployeeId = null);
public sealed record LeaveClash(Guid EmployeeId, string EmployeeName, DateOnly StartDate, DateOnly EndDate, string Status);
public sealed record LeaveOutcome(LeaveRequest Request, IReadOnlyList<LeaveClash> Clashes, IReadOnlyList<string> Warnings);
public sealed record LeavePaySplit(decimal FullPay, decimal HalfPay, decimal Unpaid, string Explanation);
public sealed record LiabilityRow(Guid EmployeeId, string EmployeeNumber, string EmployeeName, string? Department, decimal DaysAvailable, decimal DailyRate, decimal Liability);

/// <summary>
/// Leave rules: server-side working-day counts (weekends + gazetted holidays), service eligibility,
/// Employment Act s.30 sick-pay tiers, routing to the line manager, clash detection and leave liability.
/// </summary>
public sealed class LeaveService(PayrollDbContext db, ITenantContext tenant, HrAccess access, WorkCalendar calendar, LeaveBalanceService balances,
    AuditService audit, NotificationService notify)
{
    public async Task<decimal> CountDaysAsync(Guid legalEntityId, LeaveType type, DateOnly start, DateOnly end, CancellationToken ct) =>
        type.CountsWorkingDaysOnly ? await calendar.WorkingDaysAsync(legalEntityId, start, end, ct) : end.DayNumber - start.DayNumber + 1;

    public async Task<LeaveOutcome> ApplyAsync(LeaveApplication a, CancellationToken ct = default)
    {
        // Employees apply for themselves; HR may record leave on someone's behalf.
        var employeeId = a.EmployeeId ?? tenant.EmployeeId ?? throw new ForbiddenException("No employee linked to this user.");
        if (employeeId != tenant.EmployeeId) tenant.Require(Permissions.LeaveApprove);
        if (a.EndDate < a.StartDate) throw new DomainException("The last day is before the first day.", "VALIDATION");
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, ct) ?? throw new NotFoundException(nameof(Employee), employeeId);
        var type = await db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == a.LeaveTypeId && t.LegalEntityId == emp.LegalEntityId && t.IsActive, ct)
                   ?? throw new NotFoundException(nameof(LeaveType), a.LeaveTypeId);

        var computed = await CountDaysAsync(emp.LegalEntityId, type, a.StartDate, a.EndDate, ct);
        if (computed <= 0) throw new DomainException("The selected dates contain no working days.", "VALIDATION");
        // A shorter figure is accepted for half-days; never more than the dates allow.
        var days = a.Days is { } d && d > 0 && d <= computed && d % 0.5m == 0 ? d : computed;

        var warnings = new List<string>();
        var overlap = await db.LeaveRequests.AnyAsync(r => r.EmployeeId == employeeId && r.Status != LeaveRequestStatus.Rejected && r.Status != LeaveRequestStatus.Cancelled
                                                         && r.StartDate <= a.EndDate && r.EndDate >= a.StartDate, ct);
        if (overlap) throw new DomainException("These dates overlap an existing leave request.", "OVERLAP");

        if (type.IsPaid && type.AnnualEntitlementDays > 0 && type.FullPayDaysPerYear is null)
        {
            await balances.EnsureAsync(employeeId, a.StartDate.Year, ct);
            await db.SaveChangesAsync(ct);
            var bal = await db.EmployeeLeaveBalances.FirstOrDefaultAsync(b => b.EmployeeId == employeeId && b.LeaveTypeId == type.Id && b.Year == a.StartDate.Year, ct);
            var pending = await db.LeaveRequests.Where(r => r.EmployeeId == employeeId && r.LeaveTypeId == type.Id && r.Status == LeaveRequestStatus.Pending && r.StartDate.Year == a.StartDate.Year).SumAsync(r => r.Days, ct);
            if (bal is null || bal.Available - pending < days)
                throw new DomainException($"Insufficient {type.Name} balance: {Math.Max(0, (bal?.Available ?? 0) - pending):0.##} day(s) available, {days:0.##} requested.", "INSUFFICIENT_BALANCE");
        }
        if (type.MinServiceMonths > 0 && emp.HireDate.AddMonths(type.MinServiceMonths) > a.StartDate)
            warnings.Add($"{type.Name} is paid only after {type.MinServiceMonths} months' service; these days will be unpaid.");
        if (type.RequiresAttachment) warnings.Add("Attach supporting evidence (e.g. a medical certificate) before approval.");

        var r = new LeaveRequest
        {
            LegalEntityId = emp.LegalEntityId, EmployeeId = employeeId, LeaveTypeId = type.Id, StartDate = a.StartDate, EndDate = a.EndDate,
            Days = days, Reason = a.Reason, ApproverEmployeeId = emp.ReportingManagerId,
        };
        db.LeaveRequests.Add(r);
        audit.Record("leave.requested", nameof(LeaveRequest), r.Id, new { type.Code, r.StartDate, r.EndDate, r.Days }, emp.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return new LeaveOutcome(r, await ClashesAsync(emp, a.StartDate, a.EndDate, r.Id, ct), warnings);
    }

    /// <summary>Colleagues in the same department (or with the same manager) away on overlapping dates.</summary>
    public async Task<List<LeaveClash>> ClashesAsync(Employee emp, DateOnly from, DateOnly to, Guid? excludeRequestId, CancellationToken ct)
    {
        var peers = db.Employees.Where(e => e.Id != emp.Id && ((emp.DepartmentId != null && e.DepartmentId == emp.DepartmentId) || (emp.ReportingManagerId != null && e.ReportingManagerId == emp.ReportingManagerId)));
        return await db.LeaveRequests
            .Where(r => r.Id != excludeRequestId && (r.Status == LeaveRequestStatus.Approved || r.Status == LeaveRequestStatus.Pending) && r.StartDate <= to && r.EndDate >= from)
            .Join(peers, r => r.EmployeeId, e => e.Id, (r, e) => new LeaveClash(e.Id, e.FirstName + " " + e.LastName, r.StartDate, r.EndDate, r.Status.ToString()))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Splits a request into full-pay, half-pay and unpaid days using the type's yearly tiers and the days of the
    /// same type already approved this year (Employment Act s.30: 7 days full pay, then 7 days half pay).
    /// </summary>
    public static LeavePaySplit Split(LeaveType type, decimal days, decimal alreadyTakenThisYear, bool serviceEligible)
    {
        if (!type.IsPaid) return new(0, 0, days, $"{type.Name} is unpaid.");
        if (!serviceEligible) return new(0, 0, days, $"Not yet eligible ({type.MinServiceMonths} months' service required): unpaid.");
        if (type.FullPayDaysPerYear is not { } fullTier) return new(days, 0, 0, "Full pay.");
        var halfTierEnd = fullTier + type.HalfPayDaysPerYear;
        var start = alreadyTakenThisYear;
        var end = alreadyTakenThisYear + days;
        var full = Math.Max(0, Math.Min(end, fullTier) - Math.Min(start, fullTier));
        var half = Math.Max(0, Math.Min(end, halfTierEnd) - Math.Max(start, fullTier));
        var unpaid = days - full - half;
        return new(full, half, unpaid, $"{alreadyTakenThisYear:0.##} day(s) already taken this year: {full:0.##} full pay, {half:0.##} half pay, {unpaid:0.##} unpaid (tiers {fullTier:0.##} full + {type.HalfPayDaysPerYear:0.##} half).");
    }

    public async Task<LeaveRequest> ApproveAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        var r = await db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(LeaveRequest), id);
        if (r.Status != LeaveRequestStatus.Pending) throw new InvalidStateException("Request is not pending.");
        await access.RequireManagerOrAsync(r.EmployeeId, Permissions.LeaveApprove, ct);
        var type = await db.LeaveTypes.FirstAsync(t => t.Id == r.LeaveTypeId, ct);
        var emp = await db.Employees.FirstAsync(e => e.Id == r.EmployeeId, ct);
        if (type.RequiresAttachment && r.AttachmentFileId is null)
            throw new InvalidStateException($"{type.Name} requires supporting evidence. Ask the employee to attach it before approving.");

        var taken = await db.LeaveRequests.Where(x => x.EmployeeId == r.EmployeeId && x.LeaveTypeId == r.LeaveTypeId && x.Status == LeaveRequestStatus.Approved && x.StartDate.Year == r.StartDate.Year).SumAsync(x => x.Days, ct);
        var split = Split(type, r.Days, taken, type.MinServiceMonths == 0 || emp.HireDate.AddMonths(type.MinServiceMonths) <= r.StartDate);
        r.FullPayDays = split.FullPay; r.HalfPayDays = split.HalfPay; r.UnpaidDays = split.Unpaid;
        r.Status = LeaveRequestStatus.Approved; r.ApproverId = tenant.UserId; r.DecidedAt = DateTimeOffset.UtcNow; r.ApproverComment = comment;

        if (type.IsPaid && type.AnnualEntitlementDays > 0)
        {
            await balances.EnsureAsync(r.EmployeeId, r.StartDate.Year, ct);
            await db.SaveChangesAsync(ct);
            var bal = await db.EmployeeLeaveBalances.FirstOrDefaultAsync(b => b.EmployeeId == r.EmployeeId && b.LeaveTypeId == r.LeaveTypeId && b.Year == r.StartDate.Year, ct);
            if (bal is not null) bal.Taken += r.Days;
        }
        audit.Record("leave.approved", nameof(LeaveRequest), id, new { r.Days, split.FullPay, split.HalfPay, split.Unpaid, split.Explanation }, r.LegalEntityId);
        await notify.LeaveDecidedAsync(r, approved: true, ct);
        await db.SaveChangesAsync(ct);
        return r;
    }

    public async Task<LeaveRequest> RejectAsync(Guid id, string comment, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(comment)) throw new DomainException("A reason is required.", "VALIDATION");
        var r = await db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(LeaveRequest), id);
        if (r.Status != LeaveRequestStatus.Pending) throw new InvalidStateException("Request is not pending.");
        await access.RequireManagerOrAsync(r.EmployeeId, Permissions.LeaveApprove, ct);
        r.Status = LeaveRequestStatus.Rejected; r.ApproverId = tenant.UserId; r.DecidedAt = DateTimeOffset.UtcNow; r.ApproverComment = comment;
        audit.Record("leave.rejected", nameof(LeaveRequest), id, new { comment }, r.LegalEntityId);
        await notify.LeaveDecidedAsync(r, approved: false, ct);
        await db.SaveChangesAsync(ct);
        return r;
    }

    public async Task<LeaveRequest> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var r = await db.LeaveRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(LeaveRequest), id);
        if (r.EmployeeId != tenant.EmployeeId) tenant.Require(Permissions.LeaveApprove);
        if (r.Status is LeaveRequestStatus.Rejected or LeaveRequestStatus.Cancelled) throw new InvalidStateException("Request is already closed.");
        if (r.Status == LeaveRequestStatus.Approved && r.ProcessedInRunId != null) throw new InvalidStateException("Leave already processed in payroll; ask HR for an adjustment.");
        if (r.Status == LeaveRequestStatus.Approved)
        {
            var bal = await db.EmployeeLeaveBalances.FirstOrDefaultAsync(b => b.EmployeeId == r.EmployeeId && b.LeaveTypeId == r.LeaveTypeId && b.Year == r.StartDate.Year, ct);
            if (bal is not null) bal.Taken -= r.Days;
        }
        r.Status = LeaveRequestStatus.Cancelled;
        audit.Record("leave.cancelled", nameof(LeaveRequest), id, null, r.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return r;
    }

    /// <summary>Value of untaken paid annual-type leave: available days × daily rate of current basic.</summary>
    public async Task<List<LiabilityRow>> LiabilityAsync(Guid legalEntityId, DateOnly asOf, CancellationToken ct = default)
    {
        tenant.Require(Permissions.SalaryRead);
        var entity = await db.LegalEntities.FirstAsync(e => e.Id == legalEntityId, ct);
        var workingDays = entity.Settings.StandardMonthlyWorkingDays > 0 ? entity.Settings.StandardMonthlyWorkingDays : 22m;
        var employees = await db.Employees.Where(e => e.LegalEntityId == legalEntityId && e.TerminationDate == null).ToListAsync(ct);
        foreach (var e in employees) await balances.EnsureAsync(e.Id, asOf.Year, ct);
        await db.SaveChangesAsync(ct);
        var accrualTypes = await db.LeaveTypes.Where(t => t.LegalEntityId == legalEntityId && t.IsPaid && t.AccruesMonthly && t.FullPayDaysPerYear == null).Select(t => t.Id).ToListAsync(ct);
        var bals = await db.EmployeeLeaveBalances.Where(b => b.Year == asOf.Year && accrualTypes.Contains(b.LeaveTypeId)).ToListAsync(ct);
        var ids = employees.Select(e => e.Id).ToList();
        var salaries = (await db.EmployeeSalaryAssignments.Where(a => ids.Contains(a.EmployeeId) && a.EffectiveFrom <= asOf && (a.EffectiveTo == null || a.EffectiveTo >= asOf)).ToListAsync(ct))
            .GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.EffectiveFrom).First().BasicSalary);
        var depts = await db.Departments.Where(d => d.LegalEntityId == legalEntityId).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        return employees.OrderBy(e => e.EmployeeNumber).Select(e =>
        {
            var days = Math.Max(0, bals.Where(b => b.EmployeeId == e.Id).Sum(b => b.Available));
            var rate = Math.Round(salaries.GetValueOrDefault(e.Id) / workingDays, 2);
            return new LiabilityRow(e.Id, e.EmployeeNumber, e.FullName, e.DepartmentId is { } d ? depts.GetValueOrDefault(d) : null, days, rate, Math.Round(days * rate, 2));
        }).ToList();
    }

    public const string ProvisionAccount = "2195";
    public const string ProvisionExpenseAccount = "6160";

    /// <summary>Posts the movement in the leave-pay provision since the last posting (Dr expense / Cr provision, or the reverse).</summary>
    public async Task<Domain.Finance.PayrollJournal> PostProvisionAsync(Guid legalEntityId, DateOnly asOf, CancellationToken ct = default)
    {
        tenant.Require(Permissions.PayrollFinalise);
        var rows = await LiabilityAsync(legalEntityId, asOf, ct);
        var total = rows.Sum(r => r.Liability);
        var lines = await db.PayrollJournalLines
            .Join(db.PayrollJournals.Where(j => j.LegalEntityId == legalEntityId && j.Source == "LeaveProvision" && j.Status == Domain.Finance.JournalStatus.Posted),
                l => l.PayrollJournalId, j => j.Id, (l, j) => l)
            .Where(l => l.AccountCode == ProvisionAccount).ToListAsync(ct);
        var existing = lines.Sum(l => l.Credit - l.Debit);
        var movement = total - existing;
        if (movement == 0) throw new InvalidStateException($"The provision is already {total:N2}; nothing to post.");
        var reference = $"LP-{asOf:yyyyMMdd}";
        if (await db.PayrollJournals.AnyAsync(j => j.LegalEntityId == legalEntityId && j.Reference == reference, ct)) reference += $"-{DateTime.UtcNow:HHmmss}";
        var journal = GeneralJournal.Build(tenant.RequireTenant(), legalEntityId, "LeaveProvision", reference, asOf,
            $"Leave pay provision {existing:N2} → {total:N2} ({rows.Sum(r => r.DaysAvailable):0.##} days)",
            movement > 0
                ? [(ProvisionExpenseAccount, "Leave pay expense", movement, 0m, null), (ProvisionAccount, "Leave pay provision", 0m, movement, null)]
                : [(ProvisionAccount, "Leave pay provision released", -movement, 0m, null), (ProvisionExpenseAccount, "Leave pay expense (release)", 0m, -movement, null)]);
        db.PayrollJournals.Add(journal);
        audit.Record("leave.provision.posted", "PayrollJournal", journal.Id, new { asOf, total, existing, movement }, legalEntityId);
        await db.SaveChangesAsync(ct);
        return journal;
    }
}
