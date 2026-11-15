using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compliance;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Engine.Rules;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services;

public sealed class StatutoryRuleService(PayrollDbContext db, ITenantContext tenant, AuditService audit)
{
    /// <summary>
    /// Resolves the approved rules in force for a jurisdiction on a date. A tenant-specific approved
    /// version of a rule code supersedes the platform version for that code.
    /// </summary>
    public async Task<StatutoryRuleSet> ResolveAsync(string jurisdiction, DateOnly asOf, CancellationToken ct = default)
    {
        var records = await db.StatutoryRuleVersions
            .Where(r => r.Jurisdiction == jurisdiction && r.ApprovalStatus == RuleApprovalStatus.Approved
                        && r.EffectiveFrom <= asOf && (r.EffectiveTo == null || r.EffectiveTo >= asOf))
            .AsNoTracking()
            .ToListAsync(ct);

        var tenantCodes = records.Where(r => r.TenantId != null).Select(r => r.RuleCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var effective = records.Where(r => r.TenantId != null || !tenantCodes.Contains(r.RuleCode)).Select(r => r.ToEngine());
        return StatutoryRuleSet.Resolve(jurisdiction, asOf, effective);
    }

    public Task<List<StatutoryRuleVersionRecord>> ListAsync(string? jurisdiction, string? ruleCode, CancellationToken ct = default)
    {
        var q = db.StatutoryRuleVersions.AsNoTracking();
        if (jurisdiction is not null) q = q.Where(r => r.Jurisdiction == jurisdiction);
        if (ruleCode is not null) q = q.Where(r => r.RuleCode == ruleCode);
        return q.OrderBy(r => r.Jurisdiction).ThenBy(r => r.RuleCode).ThenBy(r => r.Version).ToListAsync(ct);
    }

    /// <summary>Creates a new draft version of a rule for this tenant. Platform versions are seeded, not edited here.</summary>
    public async Task<StatutoryRuleVersionRecord> CreateDraftAsync(StatutoryRuleVersionRecord draft, CancellationToken ct = default)
    {
        tenant.Require(Permissions.StatutoryManage);
        ValidateDraft(draft);
        var tenantId = tenant.RequireTenant();
        var latest = await db.StatutoryRuleVersions
            .Where(r => r.Jurisdiction == draft.Jurisdiction && r.RuleCode == draft.RuleCode)
            .MaxAsync(r => (int?)r.Version, ct) ?? 0;

        draft.Id = Guid.NewGuid();
        draft.TenantId = tenantId;
        draft.Version = latest + 1;
        draft.ApprovalStatus = RuleApprovalStatus.Draft;
        draft.ApprovedAt = null;
        draft.ApprovedBy = null;
        db.StatutoryRuleVersions.Add(draft);
        audit.Record("statutory.rule.drafted", nameof(StatutoryRuleVersionRecord), draft.Id, new { draft.RuleCode, draft.Version, draft.EffectiveFrom });
        await db.SaveChangesAsync(ct);
        return draft;
    }

    public async Task<StatutoryRuleVersionRecord> SubmitAsync(Guid id, CancellationToken ct = default)
    {
        tenant.Require(Permissions.StatutoryManage);
        var r = await GetOwnedAsync(id, ct);
        if (r.ApprovalStatus != RuleApprovalStatus.Draft) throw new InvalidStateException("Only draft rule versions can be submitted.");
        r.ApprovalStatus = RuleApprovalStatus.PendingApproval;
        audit.Record("statutory.rule.submitted", nameof(StatutoryRuleVersionRecord), r.Id);
        await db.SaveChangesAsync(ct);
        return r;
    }

    /// <summary>
    /// Approves a pending version. The approver must differ from the author, and the effective window
    /// must not overlap another approved version of the same code (the engine would reject it at run time;
    /// we reject it here so the configuration is never left in a broken state).
    /// </summary>
    public async Task<StatutoryRuleVersionRecord> ApproveAsync(Guid id, string? note, CancellationToken ct = default)
    {
        tenant.Require(Permissions.StatutoryApprove);
        var r = await GetOwnedAsync(id, ct);
        if (r.ApprovalStatus != RuleApprovalStatus.PendingApproval) throw new InvalidStateException("Rule version is not pending approval.");
        if (r.CreatedBy == tenant.UserId && !tenant.IsPlatformAdministrator)
            throw new ForbiddenException("The author of a statutory rule version cannot approve it.");

        var overlapping = await db.StatutoryRuleVersions
            .Where(o => o.Id != r.Id && o.TenantId == r.TenantId && o.Jurisdiction == r.Jurisdiction && o.RuleCode == r.RuleCode
                        && o.ApprovalStatus == RuleApprovalStatus.Approved
                        && o.EffectiveFrom <= (r.EffectiveTo ?? DateOnly.MaxValue)
                        && (o.EffectiveTo == null || o.EffectiveTo >= r.EffectiveFrom))
            .ToListAsync(ct);

        // Convenience: an open-ended predecessor is closed the day before the new version starts.
        foreach (var o in overlapping)
        {
            if (o.EffectiveTo is null && o.EffectiveFrom < r.EffectiveFrom)
            {
                o.EffectiveTo = r.EffectiveFrom.AddDays(-1);
                audit.Record("statutory.rule.closed", nameof(StatutoryRuleVersionRecord), o.Id, new { o.EffectiveTo, closedBy = r.Id });
            }
            else
            {
                throw new InvalidStateException($"Version {o.Version} of {r.RuleCode} is approved for an overlapping period.");
            }
        }

        r.ApprovalStatus = RuleApprovalStatus.Approved;
        r.ApprovedAt = DateTimeOffset.UtcNow;
        r.ApprovedBy = tenant.UserId;
        r.ApprovalNote = note;
        audit.Record("statutory.rule.approved", nameof(StatutoryRuleVersionRecord), r.Id, new { r.RuleCode, r.Version, note });
        await db.SaveChangesAsync(ct);
        return r;
    }

    public async Task<StatutoryRuleVersionRecord> RejectAsync(Guid id, string reason, CancellationToken ct = default)
    {
        tenant.Require(Permissions.StatutoryApprove);
        var r = await GetOwnedAsync(id, ct);
        if (r.ApprovalStatus != RuleApprovalStatus.PendingApproval) throw new InvalidStateException("Rule version is not pending approval.");
        r.ApprovalStatus = RuleApprovalStatus.Rejected;
        r.ApprovalNote = reason;
        audit.Record("statutory.rule.rejected", nameof(StatutoryRuleVersionRecord), r.Id, new { reason });
        await db.SaveChangesAsync(ct);
        return r;
    }

    private async Task<StatutoryRuleVersionRecord> GetOwnedAsync(Guid id, CancellationToken ct)
    {
        var r = await db.StatutoryRuleVersions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("StatutoryRuleVersion", id);
        if (r.TenantId is null && !tenant.IsPlatformAdministrator)
            throw new ForbiddenException("Platform statutory rules can only be changed by a platform administrator.");
        return r;
    }

    private static void ValidateDraft(StatutoryRuleVersionRecord d)
    {
        if (string.IsNullOrWhiteSpace(d.RuleCode)) throw new DomainException("Rule code is required.", "VALIDATION");
        if (string.IsNullOrWhiteSpace(d.Jurisdiction)) throw new DomainException("Jurisdiction is required.", "VALIDATION");
        if (d.EffectiveTo is { } to && to < d.EffectiveFrom) throw new DomainException("Effective end precedes start.", "VALIDATION");
        if (d.EmployeeRate < 0 || d.EmployeeRate > 1 || d.EmployerRate < 0 || d.EmployerRate > 1) throw new DomainException("Rates must be between 0 and 1.", "VALIDATION");
        if (d.Method == StatutoryCalculationMethod.ProgressiveBands) RuleEvaluator.ValidateBands(d.Parameters.Bands);
        if (d.Method == StatutoryCalculationMethod.TieredContribution && (d.Parameters.LowerEarningsLimit is null || d.Parameters.UpperEarningsLimit is null))
            throw new DomainException("Tiered rules need lower and upper earnings limits.", "VALIDATION");
    }
}
