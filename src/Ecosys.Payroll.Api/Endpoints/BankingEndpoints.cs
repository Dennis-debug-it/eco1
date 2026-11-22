using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Ecosys.Payroll.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Api.Endpoints;

public sealed record SaveBankRequest(string Code, string Name, string? SwiftCode, bool Platform = false);
public sealed record ImportBranchesRequest(string Csv, bool Platform = false);
public sealed record CreatePayingAccountRequest(Guid LegalEntityId, string Label, string BankCode, string? BranchCode, string AccountName, string AccountNumber, string? CurrencyCode, bool IsDefault);
public sealed record UpdatePayingAccountRequest(string Label, bool IsDefault, bool IsActive);

/// <summary>Bank directory (codes payment files route on) and the company accounts payment batches are debited from.</summary>
public static class BankingEndpoints
{
    public static void MapBanking(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/organisation").RequireAuthorization();

        // Any signed-in user may read the directory: employees pick their bank in self-service.
        g.MapGet("/banks", async (BankDirectoryService svc, CancellationToken ct) => Results.Ok(await svc.ListBanksAsync(ct)));
        g.MapGet("/banks/{code}/branches", async (string code, BankDirectoryService svc, CancellationToken ct) => Results.Ok(await svc.ListBranchesAsync(code, ct)));
        g.MapPost("/banks", async (SaveBankRequest req, BankDirectoryService svc, CancellationToken ct) =>
            Results.Ok(await svc.UpsertBankAsync(req.Code, req.Name, req.SwiftCode, req.Platform, ct)));
        g.MapPost("/banks/import-branches", async (ImportBranchesRequest req, BankDirectoryService svc, CancellationToken ct) =>
            Results.Ok(await svc.ImportBranchesAsync(req.Csv, req.Platform, ct)));

        g.MapGet("/paying-accounts", async (Guid? legalEntityId, ITenantContext ctx, PayrollDbContext db, CancellationToken ct) =>
        {
            if (!ctx.Has(Permissions.OrganisationManage) && !ctx.Has(Permissions.PaymentsManage)) throw new ForbiddenException("Permission 'payments.manage' is required.");
            var q = db.CompanyBankAccounts.AsNoTracking();
            if (legalEntityId is { } le) q = q.Where(a => a.LegalEntityId == le);
            if (ctx.LegalEntityIds.Count > 0) q = q.Where(a => ctx.LegalEntityIds.Contains(a.LegalEntityId));
            var rows = await q.OrderByDescending(a => a.IsActive).ThenByDescending(a => a.IsDefault).ThenBy(a => a.Label).ToListAsync(ct);
            foreach (var a in rows) a.AccountNumber = FieldEncryptor.Mask(a.AccountNumber)!;
            return Results.Ok(rows);
        });

        g.MapPost("/paying-accounts", async (CreatePayingAccountRequest req, ITenantContext ctx, PayrollDbContext db, BankDirectoryService banks, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            ctx.RequireLegalEntity(req.LegalEntityId);
            var entity = await db.LegalEntities.FirstOrDefaultAsync(e => e.Id == req.LegalEntityId, ct) ?? throw new NotFoundException(nameof(LegalEntity), req.LegalEntityId);
            if (string.IsNullOrWhiteSpace(req.Label) || string.IsNullOrWhiteSpace(req.AccountName)) throw new DomainException("Label and account name are required.", "VALIDATION");
            var details = await banks.ResolveAsync(PaymentMethod.BankTransfer, req.BankCode, req.BranchCode, req.AccountNumber, ct);
            var account = new CompanyBankAccount
            {
                TenantId = ctx.RequireTenant(), LegalEntityId = entity.Id, Label = req.Label.Trim(), BankCode = details.BankCode!, BankName = details.BankName,
                BranchCode = details.BranchCode, BranchName = details.BranchName, AccountName = req.AccountName.Trim(), AccountNumber = details.AccountNumber,
                CurrencyCode = string.IsNullOrWhiteSpace(req.CurrencyCode) ? entity.CurrencyCode : req.CurrencyCode.Trim().ToUpperInvariant(),
            };
            var others = await db.CompanyBankAccounts.Where(a => a.LegalEntityId == entity.Id && a.IsActive).ToListAsync(ct);
            account.IsDefault = req.IsDefault || others.Count == 0;
            if (account.IsDefault) foreach (var o in others) o.IsDefault = false;
            db.CompanyBankAccounts.Add(account);
            audit.Record("paying_account.created", nameof(CompanyBankAccount), account.Id, new { account.Label, account.BankCode, account.BranchCode, account = FieldEncryptor.Mask(account.AccountNumber), account.IsDefault }, entity.Id);
            await db.SaveChangesAsync(ct);
            account.AccountNumber = FieldEncryptor.Mask(account.AccountNumber)!;
            return Results.Created($"/api/payroll/organisation/paying-accounts/{account.Id}", account);
        });

        // The number and bank can't be edited: a different account is a new record, so batches keep pointing at what they debited.
        g.MapPut("/paying-accounts/{id:guid}", async (Guid id, UpdatePayingAccountRequest req, ITenantContext ctx, PayrollDbContext db, AuditService audit, CancellationToken ct) =>
        {
            ctx.Require(Permissions.OrganisationManage);
            var account = await db.CompanyBankAccounts.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException(nameof(CompanyBankAccount), id);
            ctx.RequireLegalEntity(account.LegalEntityId);
            if (string.IsNullOrWhiteSpace(req.Label)) throw new DomainException("Label is required.", "VALIDATION");
            account.Label = req.Label.Trim();
            account.IsActive = req.IsActive;
            account.IsDefault = req.IsDefault && req.IsActive;
            if (account.IsDefault)
                foreach (var o in await db.CompanyBankAccounts.Where(a => a.LegalEntityId == account.LegalEntityId && a.Id != id).ToListAsync(ct)) o.IsDefault = false;
            audit.Record("paying_account.updated", nameof(CompanyBankAccount), id, new { account.Label, account.IsDefault, account.IsActive }, account.LegalEntityId);
            await db.SaveChangesAsync(ct);
            account.AccountNumber = FieldEncryptor.Mask(account.AccountNumber)!;
            return Results.Ok(account);
        });
    }
}
