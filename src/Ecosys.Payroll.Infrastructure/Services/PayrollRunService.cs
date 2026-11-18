using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Finance;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Domain.Organisation;
using Ecosys.Payroll.Domain.Payroll;
using Ecosys.Payroll.Engine;
using Ecosys.Payroll.Engine.Jurisdictions.Kenya;
using Ecosys.Payroll.Engine.Model;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services;

public sealed record CreateRunRequest(Guid LegalEntityId, Guid PayrollGroupId, Guid PayrollPeriodId, PayrollRunType RunType = PayrollRunType.Regular, string? Notes = null, bool AuthoriseDuplicate = false);

/// <summary>Drives the payroll lifecycle (PRD §5). Every transition is authorised, audited and state-checked.</summary>
public sealed class PayrollRunService(
    PayrollDbContext db,
    ITenantContext tenant,
    AuditService audit,
    StatutoryRuleService rules,
    PayrollInputAssembler assembler,
    PayrollCalculatorRegistry calculators,
    JournalBuilder journals,
    NotificationService notifications)
{
    public async Task<PayrollRun> CreateAsync(CreateRunRequest req, CancellationToken ct = default)
    {
        tenant.Require(Permissions.PayrollPrepare);
        tenant.RequireLegalEntity(req.LegalEntityId);

        var period = await db.PayrollPeriods.FirstOrDefaultAsync(p => p.Id == req.PayrollPeriodId && p.PayrollGroupId == req.PayrollGroupId, ct)
            ?? throw new NotFoundException("PayrollPeriod", req.PayrollPeriodId);
        if (period.Status == PeriodStatus.Closed) throw new InvalidStateException("The payroll period is closed.");
        var entity = await db.LegalEntities.FirstAsync(e => e.Id == req.LegalEntityId, ct);
        var group = await db.PayrollGroups.FirstAsync(g => g.Id == req.PayrollGroupId && g.LegalEntityId == req.LegalEntityId, ct);

        // Duplicate prevention (PRD §9.2): one live regular run per group/period unless explicitly authorised.
        if (req.RunType == PayrollRunType.Regular)
        {
            var existing = await db.PayrollRuns.AnyAsync(r => r.PayrollGroupId == req.PayrollGroupId && r.PayrollPeriodId == req.PayrollPeriodId
                                                                 && r.RunType == PayrollRunType.Regular && r.Status != PayrollRunStatus.Reversed && r.Status != PayrollRunStatus.Rejected, ct);
            if (existing)
            {
                if (!req.AuthoriseDuplicate) throw new InvalidStateException("A payroll run already exists for this group and period. Authorise a duplicate explicitly if intended.");
                tenant.Require(Permissions.PayrollApprove);
            }
        }

        var seq = await db.PayrollRuns.CountAsync(r => r.LegalEntityId == req.LegalEntityId, ct) + 1;
        var run = new PayrollRun
        {
            TenantId = tenant.RequireTenant(),
            LegalEntityId = req.LegalEntityId,
            PayrollGroupId = req.PayrollGroupId,
            PayrollPeriodId = req.PayrollPeriodId,
            Reference = $"PR-{period.PeriodEnd:yyyyMM}-{group.Code}-{seq:D4}",
            RunType = req.RunType,
            Notes = req.Notes,
            Jurisdiction = entity.Jurisdiction,
            CurrencyCode = entity.CurrencyCode,
            DuplicateAuthorised = req.AuthoriseDuplicate,
            PreparedBy = tenant.UserId,
            PreparedAt = DateTimeOffset.UtcNow,
        };
        db.PayrollRuns.Add(run);
        audit.Record("payroll.run.created", nameof(PayrollRun), run.Id, new { run.Reference, req.RunType }, run.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Snapshots the employee list for the run (step 1). Re-running refreshes the list while the run is editable.</summary>
    public async Task<PayrollRun> PrepareAsync(Guid runId, CancellationToken ct = default)
    {
        tenant.Require(Permissions.PayrollPrepare);
        var run = await LoadAsync(runId, includeEmployees: true, ct);
        run.EnsureEditable();
        if (run.Status is PayrollRunStatus.Draft or PayrollRunStatus.Rejected or PayrollRunStatus.Calculated) run.Transition(PayrollRunStatus.Prepared);
        else if (run.Status != PayrollRunStatus.Prepared) throw new InvalidStateException($"Run is {run.Status}.");

        var ctx = await assembler.LoadAsync(run, ct);
        await ClearResultsAsync(run, ct);
        foreach (var e in ctx.Employees)
        {
            var (_, issues) = assembler.Build(ctx, e);
            run.Employees.Add(new PayrollRunEmployee
            {
                TenantId = run.TenantId, LegalEntityId = run.LegalEntityId, PayrollRunId = run.Id, EmployeeId = e.Id,
                EmployeeNumber = e.EmployeeNumber, EmployeeName = e.FullName, DepartmentId = e.DepartmentId, BranchId = e.BranchId,
                CostCentreId = e.CostCentreId, Issues = issues, HasErrors = issues.Any(i => i.Severity == ValidationSeverity.Error),
            });
        }
        run.EmployeeCount = run.Employees.Count;
        run.PreparedBy ??= tenant.UserId;
        run.PreparedAt = DateTimeOffset.UtcNow;
        ResetTotals(run);
        audit.Record("payroll.run.prepared", nameof(PayrollRun), run.Id, new { run.EmployeeCount }, run.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Runs the engine for every employee (steps 2–16) and stores an immutable breakdown.</summary>
    public async Task<PayrollRun> CalculateAsync(Guid runId, CancellationToken ct = default)
    {
        tenant.Require(Permissions.PayrollPrepare);
        var run = await LoadAsync(runId, includeEmployees: true, ct);
        run.EnsureEditable();
        if (run.Status is PayrollRunStatus.Draft or PayrollRunStatus.Rejected) run.Transition(PayrollRunStatus.Prepared);
        if (run.Status is not (PayrollRunStatus.Prepared or PayrollRunStatus.Calculated)) throw new InvalidStateException($"Run is {run.Status}.");

        var ctx = await assembler.LoadAsync(run, ct);
        var ruleSet = await rules.ResolveAsync(run.Jurisdiction, ctx.Period.PeriodEnd, ct);
        var calculator = calculators.For(run.Jurisdiction);
        var periodInput = new PayrollPeriodInput(ctx.Period.Id, ctx.Period.PeriodStart, ctx.Period.PeriodEnd, run.Jurisdiction, run.CurrencyCode, ctx.Frequency);
        var threshold = ctx.Entity.Settings.SalaryVarianceWarningPercent / 100m;

        // Replace previous results wholesale; the run is not yet approved so nothing is lost.
        await ClearResultsAsync(run, ct);
        ResetTotals(run);

        var seenAccounts = new Dictionary<string, string>();
        foreach (var emp in ctx.Employees)
        {
            var (input, issues) = assembler.Build(ctx, emp);
            var pre = new PayrollRunEmployee
            {
                TenantId = run.TenantId, LegalEntityId = run.LegalEntityId, PayrollRunId = run.Id, EmployeeId = emp.Id,
                EmployeeNumber = emp.EmployeeNumber, EmployeeName = emp.FullName, DepartmentId = emp.DepartmentId, BranchId = emp.BranchId,
                CostCentreId = emp.CostCentreId, SalaryAssignmentId = ctx.Assignments.GetValueOrDefault(emp.Id)?.Id,
            };

            if (ctx.PrimaryBankAccounts.TryGetValue(emp.Id, out var bank))
            {
                var key = $"{bank.BankCode}|{bank.AccountNumber}";
                if (seenAccounts.TryGetValue(key, out var other)) issues.Add(new(ValidationSeverity.Error, "DUPLICATE_ACCOUNT", $"Bank account is shared with employee {other}."));
                else seenAccounts[key] = emp.EmployeeNumber;
            }

            if (input is not null)
            {
                var result = calculator.Calculate(input, periodInput, ruleSet);
                issues.AddRange(result.Issues);
                pre.BasicPay = result.BasicPay; pre.GrossPay = result.GrossPay; pre.TaxableEarnings = result.TaxableEarnings;
                pre.PensionableEarnings = result.PensionableEarnings; pre.TaxablePay = result.TaxablePay; pre.GrossTax = result.GrossTax;
                pre.TotalReliefs = result.TotalReliefs; pre.Paye = result.Paye;
                pre.Nssf = result.StatutoryAmount(KenyaRuleCodes.Nssf); pre.Shif = result.StatutoryAmount(KenyaRuleCodes.Shif);
                pre.HousingLevy = result.StatutoryAmount(KenyaRuleCodes.HousingLevy);
                pre.TotalStatutoryDeductions = result.TotalStatutoryDeductions; pre.TotalOtherDeductions = result.TotalOtherDeductions;
                pre.TotalEmployeeDeductions = result.TotalEmployeeDeductions; pre.NetPay = result.NetPay;
                pre.TotalEmployerContributions = result.TotalEmployerContributions; pre.TotalEmployerCost = result.TotalEmployerCost;
                pre.StatutoryRuleVersionIds = result.StatutoryRuleVersionIds.ToList();
                pre.Lines = result.Lines.Select(l => PayrollResultLineRecord.From(l, run.TenantId, pre.Id)).ToList();

                if (ctx.PreviousNetPay.TryGetValue(emp.Id, out var prevNet))
                {
                    pre.PreviousNetPay = prevNet;
                    if (prevNet > 0 && Math.Abs(result.NetPay - prevNet) / prevNet > threshold)
                        issues.Add(new(ValidationSeverity.Warning, "NET_VARIANCE", $"Net pay changed {((result.NetPay - prevNet) / prevNet):P1} from last finalised run ({prevNet:N2})."));
                }

                run.TotalGross += result.GrossPay; run.TotalTaxable += result.TaxablePay; run.TotalPaye += result.Paye;
                run.TotalNssfEmployee += pre.Nssf; run.TotalNssfEmployer += result.EmployerAmount(KenyaRuleCodes.Nssf);
                run.TotalShif += pre.Shif; run.TotalHousingLevyEmployee += pre.HousingLevy; run.TotalHousingLevyEmployer += result.EmployerAmount(KenyaRuleCodes.HousingLevy);
                run.TotalNita += result.EmployerAmount(KenyaRuleCodes.Nita);
                run.TotalOtherDeductions += result.TotalOtherDeductions; run.TotalEmployeeDeductions += result.TotalEmployeeDeductions;
                run.TotalNet += result.NetPay; run.TotalEmployerContributions += result.TotalEmployerContributions; run.TotalEmployerCost += result.TotalEmployerCost;
            }

            pre.Issues = issues;
            pre.HasErrors = issues.Any(i => i.Severity == ValidationSeverity.Error);
            run.Employees.Add(pre);
        }

        run.EmployeeCount = run.Employees.Count;
        run.ErrorCount = run.Employees.Sum(e => e.Issues.Count(i => i.Severity == ValidationSeverity.Error));
        run.WarningCount = run.Employees.Sum(e => e.Issues.Count(i => i.Severity == ValidationSeverity.Warning));
        run.CalculatedAt = DateTimeOffset.UtcNow;
        if (run.Status == PayrollRunStatus.Prepared) run.Transition(PayrollRunStatus.Calculated);
        audit.Record("payroll.run.calculated", nameof(PayrollRun), run.Id, new { run.EmployeeCount, run.TotalGross, run.TotalNet, run.ErrorCount, run.WarningCount, rules = ruleSet.Rules.Select(r => $"{r.RuleCode} v{r.Version}") }, run.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<PayrollRun> SubmitAsync(Guid runId, string? comment, CancellationToken ct = default)
    {
        tenant.Require(Permissions.PayrollPrepare);
        var run = await LoadAsync(runId, includeEmployees: false, ct);
        if (run.Status != PayrollRunStatus.Calculated) throw new InvalidStateException("Only a calculated run can be submitted for review.");
        if (run.ErrorCount > 0) throw new InvalidStateException($"Run has {run.ErrorCount} validation error(s) that must be resolved before submission.");
        if (run.EmployeeCount == 0) throw new InvalidStateException("Run has no employees.");
        var from = run.Status;
        run.Transition(PayrollRunStatus.UnderReview);
        run.SubmittedBy = tenant.UserId;
        run.SubmittedAt = DateTimeOffset.UtcNow;
        Approval(run, ApprovalAction.Submitted, from, comment);
        await notifications.PayrollSubmittedAsync(run, ct);
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Maker-checker approval: the preparer/submitter cannot approve unless the legal entity explicitly allows it.</summary>
    public async Task<PayrollRun> ApproveAsync(Guid runId, string? comment, CancellationToken ct = default)
    {
        tenant.Require(Permissions.PayrollApprove);
        var run = await LoadAsync(runId, includeEmployees: false, ct);
        if (run.Status != PayrollRunStatus.UnderReview) throw new InvalidStateException("Only a run under review can be approved.");
        var entity = await db.LegalEntities.FirstAsync(e => e.Id == run.LegalEntityId, ct);
        var me = tenant.RequireUser();
        if (!entity.Settings.AllowPreparerToApprove && (run.PreparedBy == me || run.SubmittedBy == me))
            throw new ForbiddenException("The preparer of a payroll cannot approve it (separation of duties).");

        var from = run.Status;
        run.Transition(PayrollRunStatus.Approved);
        run.ApprovedBy = me;
        run.ApprovedAt = DateTimeOffset.UtcNow;
        Approval(run, ApprovalAction.Approved, from, comment);
        await notifications.PayrollDecidedAsync(run, approved: true, comment, ct);
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<PayrollRun> RejectAsync(Guid runId, string reason, CancellationToken ct = default)
    {
        if (!tenant.Has(Permissions.PayrollApprove) && !tenant.Has(Permissions.PayrollReview)) throw new ForbiddenException("Reviewer or approver permission required.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A rejection reason is required.", "VALIDATION");
        var run = await LoadAsync(runId, includeEmployees: false, ct);
        if (run.Status != PayrollRunStatus.UnderReview) throw new InvalidStateException("Only a run under review can be rejected.");
        var from = run.Status;
        run.Transition(PayrollRunStatus.Rejected);
        Approval(run, ApprovalAction.Rejected, from, reason);
        await notifications.PayrollDecidedAsync(run, approved: false, reason, ct);
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Generates payment instructions and a bank file for an approved run (step 6).</summary>
    public async Task<(PaymentBatch Batch, byte[] Content)> GeneratePaymentFileAsync(Guid runId, PaymentFileFormat format, DateOnly? valueDate, CancellationToken ct = default)
    {
        tenant.Require(Permissions.PaymentsManage);
        var run = await LoadAsync(runId, includeEmployees: true, ct);
        if (run.Status is not (PayrollRunStatus.Approved or PayrollRunStatus.PaymentProcessing)) throw new InvalidStateException("Payment files can only be generated for an approved run.");

        var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PayrollPeriodId, ct);
        var entity = await db.LegalEntities.FirstAsync(e => e.Id == run.LegalEntityId, ct);
        var ids = run.Employees.Select(e => e.EmployeeId).ToList();
        var banks = (await db.EmployeeBankAccounts.Where(b => ids.Contains(b.EmployeeId) && b.IsVerified).ToListAsync(ct))
            .GroupBy(b => b.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());

        // Cancel any un-sent earlier payments so a regenerated file never double-pays.
        var stale = await db.PayrollPayments.Where(p => p.PayrollRunId == run.Id && (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.FileGenerated)).ToListAsync(ct);
        foreach (var s in stale) s.Status = PaymentStatus.Cancelled;

        var batchNo = await db.PaymentBatches.CountAsync(b => b.PayrollRunId == run.Id, ct) + 1;
        var batch = new PaymentBatch
        {
            TenantId = run.TenantId, LegalEntityId = run.LegalEntityId, PayrollRunId = run.Id,
            Reference = $"{run.Reference}-PAY{batchNo}", Format = format, GeneratedBy = tenant.RequireUser(),
            ValueDate = valueDate ?? period.PayDate, FileName = "", ContentHash = "",
        };
        var payments = new List<PayrollPayment>();
        var n = 0;
        foreach (var e in run.Employees.Where(e => e.NetPay > 0).OrderBy(e => e.EmployeeNumber))
        {
            if (!banks.TryGetValue(e.EmployeeId, out var accounts)) throw new InvalidStateException($"Employee {e.EmployeeNumber} has no verified bank account.");
            foreach (var (bank, amount) in SplitNetPay(e.NetPay, accounts))
            {
                if (format == PaymentFileFormat.MpesaB2C && bank.Method != Domain.Employees.PaymentMethod.MobileMoney) continue;
                payments.Add(new PayrollPayment
                {
                    TenantId = run.TenantId, LegalEntityId = run.LegalEntityId, PayrollRunId = run.Id, PayrollRunEmployeeId = e.Id, EmployeeId = e.EmployeeId,
                    PaymentBatchId = batch.Id, Reference = $"{batch.Reference}-{++n:D4}", Amount = amount, CurrencyCode = run.CurrencyCode,
                    Method = bank.Method, BankName = bank.BankName, BankCode = bank.BankCode, BranchCode = bank.BranchCode,
                    AccountName = bank.AccountName, AccountNumber = bank.AccountNumber, Status = PaymentStatus.FileGenerated,
                });
            }
        }

        var file = PaymentFileWriter.Write(format, entity, batch, payments, period);
        batch.FileName = file.FileName;
        batch.ContentHash = file.Hash;
        batch.PaymentCount = payments.Count;
        batch.TotalAmount = payments.Sum(p => p.Amount);

        // Reconciliation guard (PRD §18): file total must equal approved net total of paid employees.
        var expected = format == PaymentFileFormat.MpesaB2C
            ? payments.Sum(p => p.Amount) // M-PESA files carry only the mobile-money share; bank shares go in a bank-format batch
            : run.Employees.Where(e => e.NetPay > 0).Sum(e => e.NetPay);
        if (payments.Count == 0) throw new InvalidStateException("No payments match this file format.");
        if (batch.TotalAmount != expected) throw new InvalidStateException($"Payment file total {batch.TotalAmount:N2} does not reconcile to approved net {expected:N2}.");

        db.PaymentBatches.Add(batch);
        db.PayrollPayments.AddRange(payments);
        if (run.Status == PayrollRunStatus.Approved) { var from = run.Status; run.Transition(PayrollRunStatus.PaymentProcessing); Approval(run, ApprovalAction.PaymentGenerated, from, $"{batch.Reference} {format} {batch.PaymentCount} payments {batch.TotalAmount:N2}"); }
        audit.Record("payroll.payment.file", nameof(PaymentBatch), batch.Id, new { batch.Reference, format, batch.PaymentCount, batch.TotalAmount, batch.ContentHash }, run.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return (batch, file.Content);
    }

    /// <summary>Records bank confirmation of a batch; marks the run Paid when every payment is confirmed or failed.</summary>
    public async Task<PayrollRun> ConfirmPaymentsAsync(Guid runId, Guid batchId, string? bankReference, IReadOnlyList<Guid>? failedPaymentIds, string? failureReason, CancellationToken ct = default)
    {
        tenant.Require(Permissions.PaymentsManage);
        var run = await LoadAsync(runId, includeEmployees: false, ct);
        if (run.Status != PayrollRunStatus.PaymentProcessing) throw new InvalidStateException("Run is not in payment processing.");
        var batch = await db.PaymentBatches.FirstOrDefaultAsync(b => b.Id == batchId && b.PayrollRunId == runId, ct) ?? throw new NotFoundException("PaymentBatch", batchId);
        var payments = await db.PayrollPayments.Where(p => p.PaymentBatchId == batchId).ToListAsync(ct);
        var failed = failedPaymentIds?.ToHashSet() ?? [];
        foreach (var p in payments)
        {
            if (failed.Contains(p.Id)) { p.Status = PaymentStatus.Failed; p.FailureReason = failureReason ?? "Rejected by bank"; }
            else { p.Status = PaymentStatus.Confirmed; p.ConfirmedAt = DateTimeOffset.UtcNow; p.BankReference = bankReference; }
        }
        batch.Status = failed.Count == 0 ? PaymentStatus.Confirmed : PaymentStatus.Sent;
        batch.ApprovedBy = tenant.UserId;
        batch.ApprovedAt = DateTimeOffset.UtcNow;

        var from = run.Status;
        run.Transition(PayrollRunStatus.Paid);
        Approval(run, ApprovalAction.PaymentConfirmed, from, $"{batch.Reference}: {payments.Count - failed.Count} confirmed, {failed.Count} failed");
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Locks the run, issues payslips, posts the journal and closes the period — atomically (step 7).</summary>
    public async Task<PayrollRun> FinaliseAsync(Guid runId, CancellationToken ct = default)
    {
        tenant.Require(Permissions.PayrollFinalise);
        var run = await LoadAsync(runId, includeEmployees: true, ct);
        if (run.Status is not (PayrollRunStatus.Paid or PayrollRunStatus.Approved)) throw new InvalidStateException("Only a paid (or approved, for cash payrolls) run can be finalised.");
        if (run.Status == PayrollRunStatus.Approved)
        {
            // Allow finalisation without a bank file only when there is nothing to pay through a bank.
            var anyBank = await db.PayrollPayments.AnyAsync(p => p.PayrollRunId == run.Id, ct);
            if (anyBank) throw new InvalidStateException("Payments have been generated; confirm them before finalising.");
            run.Transition(PayrollRunStatus.PaymentProcessing);
            run.Transition(PayrollRunStatus.Paid);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PayrollPeriodId, ct);

        foreach (var e in run.Employees)
        {
            db.Payslips.Add(new Payslip
            {
                TenantId = run.TenantId, LegalEntityId = run.LegalEntityId, PayrollRunId = run.Id, PayrollRunEmployeeId = e.Id, EmployeeId = e.EmployeeId,
                PayslipNumber = $"{run.Reference}-{e.EmployeeNumber}", PeriodStart = period.PeriodStart, PeriodEnd = period.PeriodEnd,
                GrossPay = e.GrossPay, NetPay = e.NetPay,
            });
        }

        // Loan balances and consumed inputs.
        var empIds = run.Employees.Select(e => e.EmployeeId).ToList();
        var lineIds = run.Employees.Select(e => e.Id).ToList();
        var loanLines = await db.PayrollResultLines.Where(l => lineIds.Contains(l.PayrollRunEmployeeId) && l.Type == ResultLineType.Deduction).ToListAsync(ct);
        var loans = await db.StaffLoans.Where(l => empIds.Contains(l.EmployeeId) && l.Status == LoanStatus.Active).ToListAsync(ct);
        foreach (var loan in loans)
        {
            var runEmp = run.Employees.FirstOrDefault(e => e.EmployeeId == loan.EmployeeId);
            var paid = loanLines.Where(l => runEmp != null && l.PayrollRunEmployeeId == runEmp.Id && l.Name.Contains($"({loan.Reference})")).Sum(l => l.Amount);
            if (paid <= 0) continue;
            loan.Balance -= paid;
            if (loan.Balance <= 0) { loan.Balance = 0; loan.Status = LoanStatus.Settled; }
        }
        await db.PayrollAdjustments.Where(a => empIds.Contains(a.EmployeeId) && a.Status == AdjustmentStatus.Pending && a.ApprovedBy != null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AdjustmentStatus.Applied).SetProperty(a => a.AppliedInRunId, run.Id), ct);
        var unpaidTypes = await db.LeaveTypes.Where(t => t.LegalEntityId == run.LegalEntityId && !t.IsPaid).Select(t => t.Id).ToListAsync(ct);
        await db.LeaveRequests.Where(l => empIds.Contains(l.EmployeeId) && unpaidTypes.Contains(l.LeaveTypeId) && l.Status == LeaveRequestStatus.Approved
                                          && l.ProcessedInRunId == null && l.StartDate <= period.PeriodEnd && l.EndDate >= period.PeriodStart)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ProcessedInRunId, run.Id), ct);

        var journal = await journals.BuildAsync(run, period, ct);
        db.PayrollJournals.Add(journal);

        period.Status = PeriodStatus.Closed;
        var from = run.Status;
        run.Transition(PayrollRunStatus.Finalised);
        run.FinalisedBy = tenant.UserId;
        run.FinalisedAt = DateTimeOffset.UtcNow;
        Approval(run, ApprovalAction.Finalised, from, $"Journal {journal.Reference} Dr {journal.TotalDebit:N2} / Cr {journal.TotalCredit:N2}");
        var entitySettings = (await db.LegalEntities.FirstAsync(e => e.Id == run.LegalEntityId, ct)).Settings;
        if (entitySettings.NotifyEmployeesOnPayslip) await notifications.PayslipsIssuedAsync(run, period.Name, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return run;
    }

    /// <summary>Reverses an approved/finalised run: nothing is deleted; a contra journal is posted and the period reopened.</summary>
    public async Task<PayrollRun> ReverseAsync(Guid runId, string reason, CancellationToken ct = default)
    {
        tenant.Require(Permissions.PayrollReverse);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A reversal reason is required.", "VALIDATION");
        var run = await LoadAsync(runId, includeEmployees: false, ct);
        if (run.Status is not (PayrollRunStatus.Approved or PayrollRunStatus.Finalised)) throw new InvalidStateException("Only approved or finalised runs can be reversed.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var journal = await db.PayrollJournals.Include(j => j.Lines).FirstOrDefaultAsync(j => j.PayrollRunId == run.Id && j.Status == JournalStatus.Posted, ct);
        if (journal is not null)
        {
            var contra = JournalBuilder.Reverse(journal, DateOnly.FromDateTime(DateTime.UtcNow), reason);
            journal.Status = JournalStatus.Reversed;
            db.PayrollJournals.Add(contra);
        }
        var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PayrollPeriodId, ct);
        period.Status = PeriodStatus.Open;

        var from = run.Status;
        run.Transition(PayrollRunStatus.Reversed);
        run.ReversedBy = tenant.UserId;
        run.ReversedAt = DateTimeOffset.UtcNow;
        run.ReversalReason = reason;
        Approval(run, ApprovalAction.Reversed, from, reason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return run;
    }

    public async Task<PayrollRun> GetAsync(Guid runId, bool includeEmployees, CancellationToken ct = default) => await LoadAsync(runId, includeEmployees, ct);

    /// <summary>
    /// Result rows are regenerated on every recalculation, so callers should address them by the stable
    /// employee id; the row id is still accepted for backwards compatibility.
    /// </summary>
    public async Task<PayrollRunEmployee> GetEmployeeResultAsync(Guid runId, Guid employeeOrRowId, CancellationToken ct = default)
    {
        var e = await db.PayrollRunEmployees.Include(x => x.Lines.OrderBy(l => l.Sequence))
            .FirstOrDefaultAsync(x => x.PayrollRunId == runId && (x.EmployeeId == employeeOrRowId || x.Id == employeeOrRowId), ct) ?? throw new NotFoundException("PayrollRunEmployee", employeeOrRowId);
        if (!tenant.Has(Permissions.SalaryRead) && tenant.EmployeeId != e.EmployeeId) throw new ForbiddenException("Salary visibility is restricted.");
        return e;
    }

    private async Task<PayrollRun> LoadAsync(Guid runId, bool includeEmployees, CancellationToken ct)
    {
        IQueryable<PayrollRun> q = db.PayrollRuns;
        if (includeEmployees) q = q.Include(r => r.Employees.OrderBy(e => e.EmployeeNumber));
        var run = await q.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new NotFoundException("PayrollRun", runId);
        tenant.RequireLegalEntity(run.LegalEntityId);
        return run;
    }

    private void Approval(PayrollRun run, ApprovalAction action, PayrollRunStatus from, string? comment)
    {
        db.PayrollApprovals.Add(new PayrollApproval
        {
            TenantId = run.TenantId, LegalEntityId = run.LegalEntityId, PayrollRunId = run.Id, Action = action,
            ActorId = tenant.RequireUser(), ActorName = tenant.UserName ?? "unknown", Comment = comment, FromStatus = from, ToStatus = run.Status,
        });
        audit.Record($"payroll.run.{action.ToString().ToLowerInvariant()}", nameof(PayrollRun), run.Id, new { run.Reference, from, to = run.Status, comment }, run.LegalEntityId);
    }

    /// <summary>
    /// Splits net pay across an employee's verified accounts: each non-primary account receives its
    /// <see cref="Domain.Employees.EmployeeBankAccount.SplitPercent"/> of net (rounded to the cent) and the
    /// primary account receives the remainder, so the parts always sum exactly to net pay.
    /// </summary>
    public static List<(Domain.Employees.EmployeeBankAccount Account, decimal Amount)> SplitNetPay(decimal net, IReadOnlyList<Domain.Employees.EmployeeBankAccount> accounts)
    {
        var primary = accounts.FirstOrDefault(a => a.IsPrimary) ?? accounts[0];
        var result = new List<(Domain.Employees.EmployeeBankAccount, decimal)>();
        var allocated = 0m;
        foreach (var a in accounts.Where(a => a != primary && a.SplitPercent > 0 && a.SplitPercent < 100).OrderBy(a => a.CreatedAt))
        {
            var amt = Math.Round(net * a.SplitPercent / 100m, 2, MidpointRounding.AwayFromZero);
            if (allocated + amt > net) amt = net - allocated;
            if (amt <= 0) continue;
            allocated += amt;
            result.Add((a, amt));
        }
        result.Insert(0, (primary, net - allocated));
        return result;
    }

    /// <summary>Hard-deletes un-approved working results (soft deletion would collide with the unique run/employee index).</summary>
    private async Task ClearResultsAsync(PayrollRun run, CancellationToken ct)
    {
        run.EnsureEditable();
        foreach (var e in run.Employees) db.Entry(e).State = EntityState.Detached;
        run.Employees.Clear();
        await db.PayrollResultLines.Where(l => db.PayrollRunEmployees.Any(e => e.PayrollRunId == run.Id && e.Id == l.PayrollRunEmployeeId)).ExecuteDeleteAsync(ct);
        await db.PayrollRunEmployees.Where(e => e.PayrollRunId == run.Id).ExecuteDeleteAsync(ct);
    }

    private static void ResetTotals(PayrollRun r)
    {
        r.TotalGross = r.TotalTaxable = r.TotalPaye = r.TotalNssfEmployee = r.TotalNssfEmployer = r.TotalShif = r.TotalHousingLevyEmployee =
            r.TotalHousingLevyEmployer = r.TotalNita = r.TotalOtherDeductions = r.TotalEmployeeDeductions = r.TotalNet =
                r.TotalEmployerContributions = r.TotalEmployerCost = 0;
        r.ErrorCount = r.WarningCount = 0;
    }
}
