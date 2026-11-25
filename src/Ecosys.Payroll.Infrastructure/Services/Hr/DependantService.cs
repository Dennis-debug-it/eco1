using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Hr;

public sealed record DependantRequest(Guid EmployeeId, string FullName, string Relationship, DateOnly? DateOfBirth, string? IdNumber, string? Phone, bool IsBeneficiary, decimal BeneficiaryPercent);
public sealed record UploadedFile(string FileName, byte[] Content);

/// <summary>
/// Dependants and beneficiary nominations. The employee (or HR for them) submits with supporting documents; HR verifies
/// the documents; a second person approves. Only approved records count: for medical cover and for death-in-service
/// dues. The person who submitted or verified cannot approve, and nobody verifies or approves their own family, unless
/// the legal entity runs payroll with one person (then the self-approval is flagged in the audit log).
/// </summary>
public sealed class DependantService(PayrollDbContext db, ITenantContext tenant, HrAccess access, FileStore files, AuditService audit, NotificationService notify)
{
    public static readonly string[] Relationships = ["Spouse", "Child", "Parent", "Sibling", "Guardian", "Other"];

    public async Task<Dependant> SubmitAsync(DependantRequest r, IReadOnlyList<UploadedFile> documents, CancellationToken ct = default)
    {
        if (!access.IsSelf(r.EmployeeId)) tenant.Require(Permissions.HrBenefits);
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == r.EmployeeId, ct) ?? throw new NotFoundException("Employee", r.EmployeeId);
        tenant.RequireLegalEntity(emp.LegalEntityId);
        if (string.IsNullOrWhiteSpace(r.FullName) || string.IsNullOrWhiteSpace(r.Relationship)) throw new DomainException("Enter the person's full name and relationship.", "VALIDATION");
        if (documents.Count == 0)
            throw new DomainException("Attach a supporting document: a copy of their ID, or a birth or marriage certificate.", "VALIDATION");
        if (r.BeneficiaryPercent is < 0 or > 100 || r.IsBeneficiary && r.BeneficiaryPercent <= 0) throw new DomainException("A beneficiary's share must be more than 0% and at most 100%.", "VALIDATION");
        if (r.IsBeneficiary)
        {
            var committed = await db.Dependants.Where(d => d.EmployeeId == emp.Id && d.IsBeneficiary && d.Status != DependantStatus.Rejected).SumAsync(d => d.BeneficiaryPercent, ct);
            if (committed + r.BeneficiaryPercent > 100)
                throw new DomainException($"Beneficiary shares would add up to more than 100% ({committed:0.##}% already approved or awaiting approval).", "VALIDATION");
        }
        var phone = string.IsNullOrWhiteSpace(r.Phone) ? null
            : KenyaIdentifiers.NormalisePhone(r.Phone) ?? throw new DomainException($"Phone '{r.Phone}' isn't a valid number.", "VALIDATION");

        var d = new Dependant
        {
            TenantId = tenant.RequireTenant(), LegalEntityId = emp.LegalEntityId, EmployeeId = emp.Id, FullName = r.FullName.Trim(), Relationship = r.Relationship.Trim(),
            DateOfBirth = r.DateOfBirth, IdNumber = string.IsNullOrWhiteSpace(r.IdNumber) ? null : r.IdNumber.Trim(), Phone = phone,
            IsBeneficiary = r.IsBeneficiary, BeneficiaryPercent = r.IsBeneficiary ? r.BeneficiaryPercent : 0, Status = DependantStatus.Submitted,
        };
        foreach (var f in documents) d.DocumentFileIds.Add((await files.SaveAsync(f.FileName, f.Content, nameof(Dependant), d.Id, emp.Id, ct)).Id);
        db.Dependants.Add(d);
        audit.Record("benefits.dependant.submitted", nameof(Dependant), d.Id, new { d.Relationship, d.IsBeneficiary, d.BeneficiaryPercent, documents = documents.Count, byEmployee = access.IsSelf(emp.Id) }, emp.LegalEntityId);
        await NotifyHrAsync(d, $"{emp.FirstName} {emp.LastName} ({emp.EmployeeNumber}) submitted a {(d.IsBeneficiary ? "beneficiary nomination" : "dependant")} for verification.", ct);
        await db.SaveChangesAsync(ct);
        return d;
    }

    /// <summary>Adds documents to a record that is not yet approved (e.g. HR asked for a clearer copy).</summary>
    public async Task<Dependant> AddDocumentsAsync(Guid id, IReadOnlyList<UploadedFile> documents, CancellationToken ct = default)
    {
        var d = await LoadAsync(id, ct);
        if (!access.IsSelf(d.EmployeeId)) tenant.Require(Permissions.HrBenefits);
        if (d.Status is DependantStatus.Approved or DependantStatus.Rejected) throw new InvalidStateException("Documents can only be added while the record is awaiting approval.");
        if (documents.Count == 0) throw new DomainException("Choose a file.", "VALIDATION");
        foreach (var f in documents) d.DocumentFileIds.Add((await files.SaveAsync(f.FileName, f.Content, nameof(Dependant), d.Id, d.EmployeeId, ct)).Id);
        d.DocumentFileIds = [.. d.DocumentFileIds]; // new list so the jsonb change is detected
        audit.Record("benefits.dependant.documents_added", nameof(Dependant), d.Id, new { count = documents.Count }, d.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return d;
    }

    /// <summary>Step 1: HR confirms the documents match the details.</summary>
    public async Task<Dependant> VerifyAsync(Guid id, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrBenefits);
        var d = await LoadAsync(id, ct);
        if (d.Status != DependantStatus.Submitted) throw new InvalidStateException($"Only a submitted record can be verified; this one is {d.Status.ToString().ToLowerInvariant()}.");
        var me = tenant.RequireUser();
        var self = await CheckSeparationAsync(d, d.CreatedBy == me, "verify", ct);
        d.Status = DependantStatus.Verified; d.VerifiedBy = me; d.VerifiedAt = DateTimeOffset.UtcNow;
        audit.Record(self ? "benefits.dependant.self_verified" : "benefits.dependant.verified", nameof(Dependant), d.Id, null, d.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return d;
    }

    /// <summary>Step 2: a different person approves; the record then takes effect.</summary>
    public async Task<Dependant> ApproveAsync(Guid id, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrBenefits);
        var d = await LoadAsync(id, ct);
        if (d.Status != DependantStatus.Verified) throw new InvalidStateException("HR must verify the supporting documents before approval.");
        var me = tenant.RequireUser();
        var self = await CheckSeparationAsync(d, d.CreatedBy == me || d.VerifiedBy == me, "approve", ct);
        if (d.IsBeneficiary)
        {
            var approved = await db.Dependants.Where(x => x.EmployeeId == d.EmployeeId && x.Id != d.Id && x.IsBeneficiary && x.Status == DependantStatus.Approved).SumAsync(x => x.BeneficiaryPercent, ct);
            if (approved + d.BeneficiaryPercent > 100) throw new InvalidStateException($"Approving this would take approved beneficiary shares to {approved + d.BeneficiaryPercent:0.##}%. Reject it or remove another nomination first.");
        }
        d.Status = DependantStatus.Approved; d.ApprovedBy = me; d.ApprovedAt = DateTimeOffset.UtcNow;
        audit.Record(self ? "benefits.dependant.self_approved" : "benefits.dependant.approved", nameof(Dependant), d.Id, new { d.IsBeneficiary, d.BeneficiaryPercent }, d.LegalEntityId);
        await NotifyEmployeeAsync(d, "approved", $"Your {Kind(d)} for {d.FullName} ({d.Relationship.ToLowerInvariant()}) has been approved.", ct);
        await db.SaveChangesAsync(ct);
        return d;
    }

    public async Task<Dependant> RejectAsync(Guid id, string reason, CancellationToken ct = default)
    {
        tenant.Require(Permissions.HrBenefits);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Say why, so the employee knows what to correct.", "VALIDATION");
        var d = await LoadAsync(id, ct);
        if (d.Status is not (DependantStatus.Submitted or DependantStatus.Verified)) throw new InvalidStateException("Only a record awaiting verification or approval can be rejected.");
        if (access.IsSelf(d.EmployeeId) && !tenant.IsSystem) throw new ForbiddenException("You cannot decide on your own dependants.");
        d.Status = DependantStatus.Rejected; d.RejectedBy = tenant.RequireUser(); d.RejectedAt = DateTimeOffset.UtcNow; d.RejectionReason = reason.Trim();
        audit.Record("benefits.dependant.rejected", nameof(Dependant), d.Id, new { reason = d.RejectionReason }, d.LegalEntityId);
        await NotifyEmployeeAsync(d, "rejected", $"Your {Kind(d)} for {d.FullName} was not approved: {d.RejectionReason}. You can submit it again with corrected details or documents.", ct);
        await db.SaveChangesAsync(ct);
        return d;
    }

    /// <summary>The employee can withdraw a record that has not been approved; removing an approved one is for HR.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var d = await LoadAsync(id, ct);
        if (!tenant.Has(Permissions.HrBenefits))
        {
            if (!access.IsSelf(d.EmployeeId)) throw new ForbiddenException("You can only change your own dependants.");
            if (d.Status == DependantStatus.Approved) throw new ForbiddenException("This record is approved. Ask HR to remove or change it.");
        }
        var today = BusinessClock.Today();
        var cover = await db.BenefitEnrolments.Where(b => b.EmployeeId == d.EmployeeId && (b.EndDate == null || b.EndDate >= today)).Select(b => b.CoveredDependantIds).ToListAsync(ct);
        if (cover.Any(ids => ids.Contains(d.Id)))
            throw new InvalidStateException("This person is on the employee's current medical cover. Change the cover first.");
        db.Dependants.Remove(d); // soft delete keeps history
        audit.Record("benefits.dependant.removed", nameof(Dependant), d.Id, new { previousStatus = d.Status.ToString(), byEmployee = access.IsSelf(d.EmployeeId) }, d.LegalEntityId);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Dependant> LoadAsync(Guid id, CancellationToken ct)
    {
        var d = await db.Dependants.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Dependant), id);
        tenant.RequireLegalEntity(d.LegalEntityId);
        return d;
    }

    /// <summary>Returns true when the action is allowed only because the legal entity runs payroll with one person.</summary>
    private async Task<bool> CheckSeparationAsync(Dependant d, bool sameActor, string step, CancellationToken ct)
    {
        var ownFamily = access.IsSelf(d.EmployeeId);
        if (!sameActor && !ownFamily || tenant.IsSystem) return false;
        var onePerson = await db.LegalEntities.Where(e => e.Id == d.LegalEntityId).Select(e => e.Settings.AllowPreparerToApprove).FirstAsync(ct);
        if (onePerson) return true;
        throw new ForbiddenException(ownFamily
            ? $"You cannot {step} your own dependants. Another HR user must do it."
            : step == "verify" ? "You entered this record, so someone else must verify the documents (separation of duties)."
            : "You entered or verified this record, so a different person must approve it (separation of duties).");
    }

    private static string Kind(Dependant d) => d.IsBeneficiary ? "beneficiary nomination" : "dependant record";

    private async Task NotifyHrAsync(Dependant d, string body, CancellationToken ct)
    {
        var hr = (await db.Users.Where(u => u.IsActive).ToListAsync(ct))
            .Where(u => u.Id != tenant.UserId && u.EmployeeId != d.EmployeeId && Permissions.ForRoles(u.Roles).Contains(Permissions.HrBenefits)
                        && (u.LegalEntityIds.Count == 0 || u.LegalEntityIds.Contains(d.LegalEntityId)));
        foreach (var u in hr)
            notify.Enqueue(NotificationChannel.Email, u.Email, u.DisplayName, "benefits.dependant.submitted", "Dependant or beneficiary awaiting verification",
                body + " Check the supporting documents under People records → Dependants & beneficiaries.", nameof(Dependant), d.Id);
    }

    private async Task NotifyEmployeeAsync(Dependant d, string outcome, string body, CancellationToken ct)
    {
        var e = await db.Employees.Where(x => x.Id == d.EmployeeId).Select(x => new { x.FirstName, x.Email }).FirstAsync(ct);
        if (!string.IsNullOrWhiteSpace(e.Email))
            notify.Enqueue(NotificationChannel.Email, e.Email, e.FirstName, $"benefits.dependant.{outcome}", $"Dependant or beneficiary {outcome}", $"Hello {e.FirstName}, {body}", nameof(Dependant), d.Id);
    }
}
