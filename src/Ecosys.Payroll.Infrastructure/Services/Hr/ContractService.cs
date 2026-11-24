using System.Globalization;
using System.Text.RegularExpressions;
using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Identity;
using Ecosys.Payroll.Infrastructure.Documents;
using Ecosys.Payroll.Infrastructure.Persistence;
using Ecosys.Payroll.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Services.Hr;

public sealed record ContractTemplateRequest(string Title, string Body, string? SignatoryName, string? SignatoryTitle);
public sealed record ContractDraftRequest(ContractKind? Kind, DateOnly? StartDate, DateOnly? EndDate);
public sealed record ContractDraftEdit(string Title, string Body, DateOnly StartDate, DateOnly? EndDate, string? SignatoryName, string? SignatoryTitle);

/// <summary>
/// Contracts of service and variation letters (Employment Act 2007 s.9–10): drafted from company templates and the
/// employee's current terms, issued for printing and wet signature, then the signed scan is uploaded and filed. They
/// state the employee's pay, so preparing and reading them needs salary.read plus documents or salary-write access.
/// </summary>
public sealed partial class ContractService(PayrollDbContext db, ITenantContext tenant, FileStore files, AuditService audit, LetterheadService letterheads)
{
    public const string Missing = "[to be completed";
    public const string DescribeChange = "[Describe the change]";

    public static readonly (string Key, string Meaning)[] Placeholders =
    [
        ("employee_name", "Employee's full name"), ("employee_first_name", "First name"), ("employee_number", "Employee number"),
        ("national_id", "National ID or passport (last 4 digits shown)"), ("date_of_birth", "Date of birth"), ("gender", "Sex"),
        ("employee_address", "Employee's physical address"), ("job_title", "Job title"), ("department", "Department"), ("reports_to", "Line manager"),
        ("work_location", "Place of work"), ("employment_type", "Permanent, fixed term, …"), ("start_date", "Start date (variations: effective date)"),
        ("end_date", "End date (fixed-term)"), ("duration", "Length of a fixed term, e.g. 12 months"), ("probation_months", "Probation (months)"),
        ("probation_end_date", "End of probation"), ("basic_salary", "Monthly basic salary"), ("daily_rate", "Daily wage (daily-paid; otherwise monthly basic per working day)"), ("allowances", "Fixed monthly allowances, one per line"),
        ("gross_salary", "Basic plus fixed allowances"), ("currency", "Pay currency"), ("pay_day", "Monthly pay day"), ("annual_leave_days", "Annual leave (days)"),
        ("notice_period", "Notice after probation"), ("changes", "Variation letters: each change, from and to"), ("company_name", "Company name"),
        ("company_address", "Company address"), ("signatory_name", "Person signing for the company"), ("signatory_title", "Their title"),
        ("today", "Date prepared"), ("reference", "Document reference"),
    ];

    public static string DefaultTitle(ContractKind k) => k switch
    {
        ContractKind.PermanentContract => "Contract of service",
        ContractKind.FixedTermContract => "Fixed-term contract of service",
        ContractKind.CasualContract => "Casual employment agreement",
        ContractKind.InternshipContract => "Internship agreement",
        _ => "Variation of your terms of employment",
    };

    private const string Parties = """
        This contract is made between {company_name} of {company_address} ("the Employer") and {employee_name}, ID {national_id}, born {date_of_birth}, {gender}, of {employee_address} ("the Employee"). It sets out the particulars of employment required by section 10 of the Employment Act, 2007.
        """;

    private const string PayAndLeave = """
        ## Remuneration
        - Basic salary: {currency} {basic_salary} per month.
        {allowances}
        - Total fixed monthly pay: {currency} {gross_salary}, before statutory deductions (PAYE, NSSF, SHIF and the Affordable Housing Levy) and any other deductions the Employee authorises in writing.
        - Pay is made by bank transfer or mobile money on or before the {pay_day} of each month, with a payslip.

        ## Hours of work
        45 hours a week, Monday to Friday, under the Employer's working-time policy. Overtime is paid in line with the Regulation of Wages orders and company policy.

        ## Leave
        - Annual leave: {annual_leave_days} working days a year with full pay, after twelve consecutive months' service (section 28), accruing monthly.
        - Sick leave: seven days with full pay and seven days with half pay a year after two months' service, on a medical certificate (section 30).
        - Maternity leave: three months with full pay; paternity leave: two weeks with full pay (section 29).
        - Public holidays as gazetted.
        """;

    private const string Closing = """
        ## Conduct and discipline
        The Employee shall follow the Employer's policies, including the code of conduct and the disciplinary and grievance procedures, which are available from HR (section 12).

        ## Confidentiality and data protection
        The Employee shall keep the Employer's confidential information private during and after employment. The Employer processes the Employee's personal data for employment purposes in line with the Data Protection Act, 2019.

        ## Entire agreement
        This contract, with the policies it refers to, is the whole agreement between the parties. Any change must be in writing and signed by both. It is governed by the laws of Kenya.
        """;

    public static string DefaultBody(ContractKind k) => Dedent(k switch
    {
        ContractKind.PermanentContract => Parties + "\n\n" + """
            ## Appointment
            - Position: {job_title}, in the {department} department, reporting to {reports_to}.
            - Place of work: {work_location}. The Employer may ask the Employee to work at other locations reasonably required by the role.
            - Type of employment: {employment_type}, starting on {start_date}. Continuous employment begins on that date.

            ## Probation
            The first {probation_months} months, to {probation_end_date}, are probation. During probation either party may end this contract by giving seven days' notice (section 42).
            """ + "\n\n" + PayAndLeave + "\n\n" + """
            ## Termination
            After probation either party may end this contract by giving {notice_period} notice in writing, or pay in lieu of notice. The Employer may end it without notice only for gross misconduct, after a fair hearing (sections 41 and 44).
            """ + "\n\n" + Closing,
        ContractKind.FixedTermContract => Parties + "\n\n" + """
            ## Appointment and term
            - Position: {job_title}, in the {department} department, reporting to {reports_to}.
            - Place of work: {work_location}.
            - Term: {duration}, from {start_date} to {end_date}. The contract ends on that date unless renewed in writing before it ends. Renewal is at the Employer's discretion and does not create an expectation of further renewal.
            """ + "\n\n" + PayAndLeave + "\n\n" + """
            ## Early termination
            Either party may end this contract before the end date by giving {notice_period} notice in writing, or pay in lieu of notice. The Employer may end it without notice only for gross misconduct, after a fair hearing (sections 41 and 44).
            """ + "\n\n" + Closing,
        ContractKind.CasualContract => Parties + "\n\n" + """
            ## Engagement
            - The Employee is engaged as a casual employee as {job_title}, at {work_location}, from {start_date}.
            - Casual employment is paid at the end of each day or as agreed. If the Employee works continuously for more than one month, or performs work that cannot reasonably be completed within three months, the terms convert to monthly employment under section 37 of the Employment Act.

            ## Pay
            {currency} {daily_rate} for each day worked, before statutory deductions.
            """ + "\n\n" + Closing,
        ContractKind.InternshipContract => Parties + "\n\n" + """
            ## Internship
            - The Employee joins as an intern ({job_title}) in the {department} department, supervised by {reports_to}, from {start_date} to {end_date} ({duration}).
            - The internship is for learning and practical experience. It does not create an expectation of permanent employment.

            ## Stipend
            {currency} {basic_salary} per month, before statutory deductions, paid on or before the {pay_day} of each month.

            ## Hours and leave
            Normal working hours of the department apply. The intern is entitled to public holidays and to sick leave on a medical certificate.

            ## Ending the internship
            Either party may end the internship by giving seven days' notice in writing.
            """ + "\n\n" + Closing,
        _ => """
            Dear {employee_first_name},

            This letter confirms changes to your terms of employment with {company_name}, effective {start_date}. It is the written statement of changes required by section 10(5) of the Employment Act, 2007.

            ## What is changing
            {changes}

            ## Your terms from {start_date}
            - Position: {job_title}, {department} department, reporting to {reports_to}.
            - Place of work: {work_location}.
            - Basic salary: {currency} {basic_salary} per month.
            {allowances}

            All other terms of your contract of service stay the same. Please sign the acknowledgement below and return a copy to HR.
            """,
    });

    // ------------------------------------------------------------------ Templates

    public async Task<ContractTemplate> TemplateAsync(Guid legalEntityId, ContractKind kind, CancellationToken ct = default)
    {
        if (!tenant.Has(Permissions.HrDocuments)) tenant.Require(Permissions.OrganisationManage);
        tenant.RequireLegalEntity(legalEntityId);
        var t = await db.ContractTemplates.FirstOrDefaultAsync(x => x.LegalEntityId == legalEntityId && x.Kind == kind, ct);
        if (t is not null) return t;
        if (!await db.LegalEntities.AnyAsync(e => e.Id == legalEntityId, ct)) throw new NotFoundException("LegalEntity", legalEntityId);
        t = new ContractTemplate { LegalEntityId = legalEntityId, Kind = kind, Title = DefaultTitle(kind), Body = DefaultBody(kind) };
        db.ContractTemplates.Add(t);
        await db.SaveChangesAsync(ct);
        return t;
    }

    public async Task<ContractTemplate> SaveTemplateAsync(Guid legalEntityId, ContractKind kind, ContractTemplateRequest r, CancellationToken ct = default)
    {
        var t = await TemplateAsync(legalEntityId, kind, ct);
        if (string.IsNullOrWhiteSpace(r.Title) || string.IsNullOrWhiteSpace(r.Body)) throw new DomainException("The template needs a title and some text.", "VALIDATION");
        var unknown = PlaceholderRx().Matches(r.Title + "\n" + r.Body).Select(m => m.Groups[1].Value).Distinct().Where(k => !Placeholders.Any(p => p.Key == k)).ToList();
        if (unknown.Count > 0) throw new DomainException($"Unknown fields: {string.Join(", ", unknown.Select(u => "{" + u + "}"))}. Use only the fields listed beside the template.", "VALIDATION");
        t.Title = r.Title.Trim(); t.Body = r.Body.Replace("\r", "").Trim(); t.SignatoryName = r.SignatoryName?.Trim(); t.SignatoryTitle = r.SignatoryTitle?.Trim();
        audit.Record("contract.template.updated", nameof(ContractTemplate), t.Id, new { kind = kind.ToString() }, legalEntityId);
        await db.SaveChangesAsync(ct);
        return t;
    }

    // ------------------------------------------------------------------ Documents

    public Task<ContractDocument> GetAsync(Guid id, CancellationToken ct = default) { RequireAccess(); return LoadAsync(id, ct); }

    public async Task<List<ContractDocument>> ForEmployeeAsync(Guid employeeId, CancellationToken ct = default)
    {
        RequireAccess();
        var emp = await EmployeeAsync(employeeId, ct);
        return await db.ContractDocuments.Where(d => d.EmployeeId == emp.Id).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
    }

    public static ContractKind KindFor(EmploymentType t) => t switch
    {
        EmploymentType.Contract or EmploymentType.Expatriate => ContractKind.FixedTermContract,
        EmploymentType.Casual => ContractKind.CasualContract,
        EmploymentType.Intern => ContractKind.InternshipContract,
        _ => ContractKind.PermanentContract,
    };

    /// <summary>Drafts a contract of service from the template for the employee's type, filled from their current terms.</summary>
    public async Task<ContractDocument> DraftContractAsync(Guid employeeId, ContractDraftRequest r, CancellationToken ct = default)
    {
        RequireAccess();
        var emp = await EmployeeAsync(employeeId, ct);
        var kind = r.Kind ?? KindFor(emp.EmploymentType);
        if (kind == ContractKind.VariationLetter) throw new DomainException("Use the variation letter action for changes to terms.", "VALIDATION");
        await EnsureNoOpenDraftAsync(emp.Id, ct);
        var template = await TemplateAsync(emp.LegalEntityId, kind, ct);
        var start = r.StartDate ?? emp.HireDate;
        var end = r.EndDate ?? (kind is ContractKind.FixedTermContract or ContractKind.InternshipContract ? emp.ContractEndDate : null);
        if (end is { } e && e <= start) throw new DomainException("The end date must be after the start date.", "VALIDATION");
        var doc = new ContractDocument
        {
            LegalEntityId = emp.LegalEntityId, EmployeeId = emp.Id, Kind = kind, Reference = await ReferenceAsync(emp, "CON", ct), Title = template.Title, Body = template.Body,
            SignatoryName = template.SignatoryName, SignatoryTitle = template.SignatoryTitle, StartDate = start, EndDate = end, Terms = await CurrentTermsAsync(emp, end, ct),
        };
        db.ContractDocuments.Add(doc);
        audit.Record("contract.drafted", nameof(ContractDocument), doc.Id, new { doc.Reference, kind = kind.ToString() }, emp.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    /// <summary>
    /// Drafts a variation letter: compares the employee's current terms with the last contract or variation issued (or,
    /// failing that, with their previous employment and pay records) and lists each change.
    /// </summary>
    public async Task<ContractDocument> DraftVariationAsync(Guid employeeId, DateOnly? effective, CancellationToken ct = default)
    {
        RequireAccess();
        var emp = await EmployeeAsync(employeeId, ct);
        await EnsureNoOpenDraftAsync(emp.Id, ct);
        var current = await CurrentTermsAsync(emp, emp.ContractEndDate, ct);
        var last = await db.ContractDocuments.Where(d => d.EmployeeId == emp.Id && (d.Status == ContractStatus.Issued || d.Status == ContractStatus.Signed))
            .OrderByDescending(d => d.IssuedAt).FirstOrDefaultAsync(ct);
        var (previous, changedOn) = last is not null ? (last.Terms, (DateOnly?)null) : await PreviousTermsFromHistoryAsync(emp, ct);
        var template = await TemplateAsync(emp.LegalEntityId, ContractKind.VariationLetter, ct);
        var doc = new ContractDocument
        {
            LegalEntityId = emp.LegalEntityId, EmployeeId = emp.Id, Kind = ContractKind.VariationLetter, Reference = await ReferenceAsync(emp, "VAR", ct),
            Title = template.Title, Body = template.Body, SignatoryName = template.SignatoryName, SignatoryTitle = template.SignatoryTitle,
            StartDate = effective ?? changedOn ?? BusinessClock.Today(), EndDate = current.EndDate, Terms = current, PreviousTerms = previous,
        };
        db.ContractDocuments.Add(doc);
        audit.Record("contract.variation.drafted", nameof(ContractDocument), doc.Id, new { doc.Reference, changes = Changes(previous, current).Count }, emp.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<ContractDocument> UpdateDraftAsync(Guid id, ContractDraftEdit e, CancellationToken ct = default)
    {
        RequireAccess();
        var d = await LoadAsync(id, ct);
        if (d.Status != ContractStatus.Draft) throw new InvalidStateException("Only a draft can be edited.");
        if (string.IsNullOrWhiteSpace(e.Title) || string.IsNullOrWhiteSpace(e.Body)) throw new DomainException("The document needs a title and some text.", "VALIDATION");
        if (e.EndDate is { } end && end <= e.StartDate) throw new DomainException("The end date must be after the start date.", "VALIDATION");
        d.Title = e.Title.Trim(); d.Body = e.Body.Replace("\r", "").Trim(); d.StartDate = e.StartDate; d.EndDate = e.EndDate;
        d.SignatoryName = e.SignatoryName?.Trim(); d.SignatoryTitle = e.SignatoryTitle?.Trim();
        if (d.IsContract) d.Terms.EndDate = e.EndDate;
        d.Terms = Clone(d.Terms);
        await db.SaveChangesAsync(ct);
        return d;
    }

    /// <summary>The filled-in text, and anything still to complete before issuing.</summary>
    public async Task<(string Title, string Body, List<string> Problems)> PreviewAsync(ContractDocument d, CancellationToken ct = default)
    {
        if (d.Status != ContractStatus.Draft) return (d.Title, d.Body, []);
        var emp = await db.Employees.FirstAsync(x => x.Id == d.EmployeeId, ct);
        var values = await ValuesAsync(d, emp, ct);
        var problems = new List<string>();
        string Fill(string text) => PlaceholderRx().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : Unknown(m.Value));
        string Unknown(string raw) { problems.Add($"Unknown field {raw}"); return raw; }
        var title = Fill(d.Title);
        var body = Fill(d.Body);
        problems.AddRange(MissingRx().Matches(body).Select(m => $"Missing {m.Groups[1].Value}: add it to the employee record, or type it into the text"));
        if (body.Contains(DescribeChange)) problems.Add("Describe the change in the \"What is changing\" section");
        if (d.Kind is ContractKind.FixedTermContract && d.EndDate is null) problems.Add("Set the end date of the fixed term");
        return (title, body, problems.Distinct().ToList());
    }

    public async Task<byte[]> PdfAsync(ContractDocument d, CancellationToken ct = default)
    {
        var emp = await db.Employees.FirstAsync(x => x.Id == d.EmployeeId, ct);
        var entity = await db.LegalEntities.FirstAsync(x => x.Id == d.LegalEntityId, ct);
        var (title, body, _) = await PreviewAsync(d, ct);
        var shown = new ContractDocument { Kind = d.Kind, Reference = d.Reference, Title = title, Body = body, SignatoryName = d.SignatoryName, SignatoryTitle = d.SignatoryTitle, StartDate = d.StartDate };
        var on = d.IssuedAt is { } at ? DateOnly.FromDateTime(BusinessClock.ToLocal(at).DateTime) : BusinessClock.Today();
        return HrDocumentRenderer.ContractDocument(entity, shown, emp, on, draft: d.Status == ContractStatus.Draft, await letterheads.BrandAsync(d.LegalEntityId, ct));
    }

    /// <summary>Freezes the text and keeps the PDF to print for signature.</summary>
    public async Task<ContractDocument> IssueAsync(Guid id, CancellationToken ct = default)
    {
        RequireAccess();
        var d = await LoadAsync(id, ct);
        if (d.Status != ContractStatus.Draft) throw new InvalidStateException("This document has already been issued.");
        var (title, body, problems) = await PreviewAsync(d, ct);
        if (problems.Count > 0) throw new DomainException("Before issuing: " + string.Join("; ", problems) + ".", "VALIDATION");
        d.Title = title; d.Body = body; d.Status = ContractStatus.Issued; d.IssuedAt = DateTimeOffset.UtcNow; d.IssuedBy = tenant.UserName;
        d.IssuedFileId = (await files.SaveAsync($"{d.Reference}.pdf", await PdfAsync(d, ct), nameof(ContractDocument), d.Id, d.EmployeeId, ct)).Id;
        audit.Record("contract.issued", nameof(ContractDocument), d.Id, new { d.Reference, kind = d.Kind.ToString() }, d.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return d;
    }

    /// <summary>
    /// Records the signed copy: stores the scan, files it in the employee's documents (visible to them), ticks the
    /// onboarding item for a first contract, and for a fixed term sets the employee's contract end date.
    /// </summary>
    public async Task<ContractDocument> UploadSignedAsync(Guid id, string fileName, byte[] content, DateOnly signedOn, CancellationToken ct = default)
    {
        RequireAccess();
        var d = await LoadAsync(id, ct);
        if (d.Status != ContractStatus.Issued) throw new InvalidStateException(d.Status == ContractStatus.Signed ? "The signed copy is already on file." : "Issue and print the document first.");
        if (signedOn > BusinessClock.Today()) throw new DomainException("The signing date can't be in the future.", "VALIDATION");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".pdf" or ".jpg" or ".jpeg" or ".png")) throw new DomainException("Upload the signed copy as a PDF or a photo (JPG or PNG).", "VALIDATION");
        var emp = await db.Employees.FirstAsync(x => x.Id == d.EmployeeId, ct);

        d.SignedFileId = (await files.SaveAsync(fileName, content, nameof(ContractDocument), d.Id, emp.Id, ct)).Id;
        d.SignedOn = signedOn; d.Status = ContractStatus.Signed;
        var title = d.IsContract ? $"{d.Title} (signed)" : $"Variation of contract, effective {d.StartDate:d MMM yyyy} (signed)";
        var filed = new EmployeeDocument
        {
            LegalEntityId = d.LegalEntityId, EmployeeId = emp.Id, Category = DocumentCategory.Contract, Title = title, IssuedOn = signedOn,
            ExpiresOn = d.Kind is ContractKind.FixedTermContract or ContractKind.InternshipContract ? d.EndDate : null, VisibleToEmployee = true,
            Version = await db.EmployeeDocuments.CountAsync(x => x.EmployeeId == emp.Id && x.Title == title, ct) + 1,
        };
        filed.StoredFileId = (await files.SaveAsync(fileName, content, nameof(EmployeeDocument), filed.Id, emp.Id, ct)).Id;
        db.EmployeeDocuments.Add(filed);
        d.EmployeeDocumentId = filed.Id;

        if (d.IsContract)
            foreach (var task in await db.OnboardingTasks.Where(t => t.EmployeeId == emp.Id && !t.Done && t.Title.Contains("contract")).ToListAsync(ct))
            { task.Done = true; task.DoneAt = DateTimeOffset.UtcNow; task.DoneBy = tenant.UserName; }
        if (d.Kind is ContractKind.FixedTermContract or ContractKind.InternshipContract && d.EndDate is { } end && emp.ContractEndDate != end)
        {
            audit.Record("employee.contract_end.updated", nameof(Employee), emp.Id, new { from = emp.ContractEndDate, to = end, d.Reference }, emp.LegalEntityId);
            emp.ContractEndDate = end;
        }
        audit.Record("contract.signed", nameof(ContractDocument), d.Id, new { d.Reference, signedOn }, d.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return d;
    }

    public async Task<ContractDocument> CancelAsync(Guid id, string reason, CancellationToken ct = default)
    {
        RequireAccess();
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Give a reason.", "VALIDATION");
        var d = await LoadAsync(id, ct);
        if (d.Status is not (ContractStatus.Draft or ContractStatus.Issued)) throw new InvalidStateException("A signed document can't be cancelled. Issue a variation letter instead.");
        d.Status = ContractStatus.Cancelled; d.CancelReason = reason.Trim();
        audit.Record("contract.cancelled", nameof(ContractDocument), d.Id, new { d.Reference, reason }, d.LegalEntityId);
        await db.SaveChangesAsync(ct);
        return d;
    }

    // ------------------------------------------------------------------ Terms and values

    /// <summary>The employee's terms today: job, place, pay (basic and fixed allowances, as payroll computes them) and notice.</summary>
    public async Task<ContractTerms> CurrentTermsAsync(Employee emp, DateOnly? endDate, CancellationToken ct)
    {
        var today = BusinessClock.Today();
        var assignment = await db.EmployeeSalaryAssignments.Include(a => a.Lines).Where(a => a.EmployeeId == emp.Id && a.EffectiveFrom <= today)
            .OrderByDescending(a => a.EffectiveFrom).FirstOrDefaultAsync(ct)
            ?? await db.EmployeeSalaryAssignments.Include(a => a.Lines).Where(a => a.EmployeeId == emp.Id).OrderBy(a => a.EffectiveFrom).FirstOrDefaultAsync(ct);
        return new ContractTerms
        {
            JobTitle = emp.JobTitle, Department = await DepartmentAsync(emp.DepartmentId, ct), Location = await LocationAsync(emp.BranchId, emp.WorkLocation, emp.LegalEntityId, ct),
            EmploymentType = Words(emp.EmploymentType.ToString()), ReportsTo = await ManagerAsync(emp.ReportingManagerId, ct), NoticeDays = emp.NoticePeriodDays,
            Basic = assignment?.BasicSalary ?? 0m, DailyRate = assignment is { PayBasis: PayBasis.Daily } ? assignment.DailyRate : null,
            Currency = assignment?.CurrencyCode ?? "KES", Allowances = assignment is null ? [] : await AllowancesAsync(assignment, ct), EndDate = endDate,
        };
    }

    /// <summary>Without an earlier contract on file: the previous employment record and pay assignment, and when the latest change took effect.</summary>
    private async Task<(ContractTerms Terms, DateOnly? ChangedOn)> PreviousTermsFromHistoryAsync(Employee emp, CancellationToken ct)
    {
        var records = await db.EmploymentRecords.Where(r => r.EmployeeId == emp.Id).OrderByDescending(r => r.EffectiveFrom).Take(2).ToListAsync(ct);
        var pay = await db.EmployeeSalaryAssignments.Include(a => a.Lines).Where(a => a.EmployeeId == emp.Id).OrderByDescending(a => a.EffectiveFrom).Take(2).ToListAsync(ct);
        var current = await CurrentTermsAsync(emp, emp.ContractEndDate, ct);
        var prevRecord = records.Count > 1 ? records[1] : null;
        var prevPay = pay.Count > 1 ? pay[1] : null;
        var terms = Clone(current);
        if (prevRecord is not null)
        {
            terms.JobTitle = prevRecord.JobTitle; terms.Department = await DepartmentAsync(prevRecord.DepartmentId, ct);
            terms.Location = await LocationAsync(prevRecord.BranchId, emp.WorkLocation, emp.LegalEntityId, ct); terms.EmploymentType = Words(prevRecord.EmploymentType.ToString());
        }
        if (prevPay is not null) { terms.Basic = prevPay.BasicSalary; terms.DailyRate = prevPay.PayBasis == PayBasis.Daily ? prevPay.DailyRate : null; terms.Allowances = await AllowancesAsync(prevPay, ct); }
        var changedOn = new[] { records.FirstOrDefault()?.EffectiveFrom, pay.FirstOrDefault()?.EffectiveFrom }.Where(x => x is not null).Max();
        return (terms, changedOn);
    }

    /// <summary>One line per changed term, e.g. "Basic salary: from KES 40,000.00 to KES 45,000.00 a month".</summary>
    public static List<string> Changes(ContractTerms? from, ContractTerms to)
    {
        var list = new List<string>();
        if (from is null) return list;
        void Add(string what, string? a, string? b) { if (!string.Equals(a ?? "", b ?? "", StringComparison.Ordinal)) list.Add($"{what}: from {a ?? "none"} to {b ?? "none"}"); }
        Add("Job title", from.JobTitle, to.JobTitle);
        Add("Department", from.Department, to.Department);
        Add("Place of work", from.Location, to.Location);
        Add("Type of employment", from.EmploymentType, to.EmploymentType);
        Add("Reporting to", from.ReportsTo, to.ReportsTo);
        // Daily-paid: the change that matters is the daily wage; its monthly equivalent would only repeat it.
        if (from.DailyRate is { } fd && to.DailyRate is { } td) { if (fd != td) list.Add($"Daily wage: from {to.Currency} {fd:N2} to {to.Currency} {td:N2} a day"); }
        else if (from.DailyRate != to.DailyRate) list.Add($"Pay: from {Pay(from)} to {Pay(to)}");
        else if (from.Basic != to.Basic) list.Add($"Basic salary: from {to.Currency} {from.Basic:N2} to {to.Currency} {to.Basic:N2} a month");
        var names = from.Allowances.Select(a => a.Name).Union(to.Allowances.Select(a => a.Name));
        foreach (var n in names)
        {
            var a = from.Allowances.FirstOrDefault(x => x.Name == n)?.Amount;
            var b = to.Allowances.FirstOrDefault(x => x.Name == n)?.Amount;
            if (a == b) continue;
            list.Add(a is null ? $"{n}: new, {to.Currency} {b:N2} a month" : b is null ? $"{n}: removed (was {to.Currency} {a:N2} a month)" : $"{n}: from {to.Currency} {a:N2} to {to.Currency} {b:N2} a month");
        }
        if (from.NoticeDays != to.NoticeDays) list.Add($"Notice period: from {Notice(from.NoticeDays)} to {Notice(to.NoticeDays)}");
        if (from.EndDate != to.EndDate) list.Add($"Contract end date: from {from.EndDate?.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) ?? "none"} to {to.EndDate?.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) ?? "none"}");
        return list;
    }

    private async Task<Dictionary<string, string>> ValuesAsync(ContractDocument d, Employee emp, CancellationToken ct)
    {
        var entity = await db.LegalEntities.FirstAsync(e => e.Id == d.LegalEntityId, ct);
        var t = d.Terms;
        string Need(string? v, string what) => string.IsNullOrWhiteSpace(v) ? $"{Missing}: {what}]" : v;
        string Date(DateOnly? v) => v?.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) ?? "";
        var allowanceLines = t.Allowances.Count == 0 ? "- Allowances: none." : string.Join("\n", t.Allowances.Select(a => $"- {a.Name}: {t.Currency} {a.Amount.ToString("N2", CultureInfo.InvariantCulture)} per month."));
        // Default probation: three months, ending the day before the three-month anniversary.
        var probationEnd = emp.ProbationEndDate ?? (d.Kind == ContractKind.PermanentContract ? d.StartDate.AddMonths(3).AddDays(-1) : null);
        var probationMonths = probationEnd is { } pe ? (int)Math.Round((pe.DayNumber - d.StartDate.DayNumber + 1) / 30.44m, MidpointRounding.AwayFromZero) : 0;
        var payDay = await db.PayrollGroups.Where(g => g.Id == emp.PayrollGroupId).Select(g => (int?)g.PayDay).FirstOrDefaultAsync(ct);
        var leave = await db.LeaveTypes.Where(x => x.LegalEntityId == d.LegalEntityId && x.Code == "ANNUAL").Select(x => (decimal?)x.AnnualEntitlementDays).FirstOrDefaultAsync(ct);
        var changes = Changes(d.PreviousTerms, t);
        return new()
        {
            ["employee_name"] = emp.FullName, ["employee_first_name"] = emp.FirstName, ["employee_number"] = emp.EmployeeNumber,
            ["national_id"] = Need(Mask(emp.NationalId ?? emp.PassportNumber), "national ID or passport number"), ["date_of_birth"] = Need(Date(emp.DateOfBirth), "date of birth"),
            ["gender"] = emp.Gender switch { Gender.Female => "female", Gender.Male => "male", Gender.Other => "other", _ => Need(null, "sex") },
            ["employee_address"] = Need(emp.PhysicalAddress, "employee's address"), ["job_title"] = Need(t.JobTitle, "job title"), ["department"] = t.Department ?? "general",
            ["reports_to"] = t.ReportsTo ?? "the person the Employer designates", ["work_location"] = t.Location ?? entity.Address ?? Need(null, "place of work"),
            ["employment_type"] = (t.EmploymentType ?? "permanent").ToLowerInvariant(), ["start_date"] = Date(d.StartDate), ["end_date"] = d.EndDate is null ? Need(null, "end date") : Date(d.EndDate),
            ["duration"] = d.EndDate is { } end ? Duration(d.StartDate, end) : Need(null, "end date"),
            ["probation_months"] = probationMonths.ToString(CultureInfo.InvariantCulture), ["probation_end_date"] = Date(probationEnd),
            ["basic_salary"] = t.Basic > 0 ? t.Basic.ToString("N2", CultureInfo.InvariantCulture) : Need(null, "basic salary (set the employee's pay first)"),
            ["daily_rate"] = t.DailyRate is { } dr ? dr.ToString("N2", CultureInfo.InvariantCulture)
                : t.Basic > 0 ? Math.Round(t.Basic / 22m, 2).ToString("N2", CultureInfo.InvariantCulture) : Need(null, "daily wage (set the employee's pay first)"),
            ["allowances"] = allowanceLines, ["gross_salary"] = (t.Basic + t.Allowances.Sum(a => a.Amount)).ToString("N2", CultureInfo.InvariantCulture), ["currency"] = t.Currency,
            ["pay_day"] = payDay is { } pd ? Ordinal(pd) : "last working day", ["annual_leave_days"] = (leave ?? 21m).ToString("0.##", CultureInfo.InvariantCulture),
            ["notice_period"] = Possessive(Notice(t.NoticeDays)), ["changes"] = changes.Count == 0 ? $"- {DescribeChange}" : string.Join("\n", changes.Select(c => "- " + c + ".")),
            ["company_name"] = entity.Name, ["company_address"] = entity.Address ?? "", ["signatory_name"] = d.SignatoryName ?? "Authorised signatory",
            ["signatory_title"] = d.SignatoryTitle ?? "", ["today"] = Date(BusinessClock.Today()), ["reference"] = d.Reference,
        };
    }

    // ------------------------------------------------------------------ Helpers

    /// <summary>Contracts state pay: salary.read, plus document (HR) or salary-write (payroll) access.</summary>
    private void RequireAccess()
    {
        if (tenant.Has(Permissions.SalaryRead) && (tenant.Has(Permissions.HrDocuments) || tenant.Has(Permissions.SalaryWrite))) return;
        throw new ForbiddenException("Contracts state the employee's pay, so preparing them needs salary access as well as HR or payroll rights.");
    }

    private async Task<Employee> EmployeeAsync(Guid id, CancellationToken ct)
    {
        var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(Employee), id);
        tenant.RequireLegalEntity(e.LegalEntityId);
        return e;
    }

    private async Task<ContractDocument> LoadAsync(Guid id, CancellationToken ct)
    {
        var d = await db.ContractDocuments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(nameof(ContractDocument), id);
        tenant.RequireLegalEntity(d.LegalEntityId);
        return d;
    }

    private async Task EnsureNoOpenDraftAsync(Guid employeeId, CancellationToken ct)
    {
        if (await db.ContractDocuments.AnyAsync(d => d.EmployeeId == employeeId && d.Status == ContractStatus.Draft, ct))
            throw new InvalidStateException("There is already a draft for this employee. Finish or cancel it first.");
    }

    private async Task<string> ReferenceAsync(Employee emp, string prefix, CancellationToken ct)
    {
        var n = await db.ContractDocuments.CountAsync(d => d.EmployeeId == emp.Id, ct) + 1;
        return $"{prefix}-{emp.EmployeeNumber}-{n:D2}";
    }

    private async Task<List<ContractAllowance>> AllowancesAsync(EmployeeSalaryAssignment a, CancellationToken ct)
    {
        var lines = new Dictionary<Guid, (CalculationMethod Method, decimal? Amount, decimal? Rate)>();
        if (a.SalaryStructureId is { } sid)
            foreach (var l in await db.SalaryStructureLines.Where(l => l.SalaryStructureId == sid).OrderBy(l => l.SortOrder).ToListAsync(ct)) lines[l.SalaryComponentId] = (l.Method, l.Amount, l.Rate);
        foreach (var l in a.Lines) lines[l.SalaryComponentId] = (l.Method, l.Amount, l.Rate);
        var ids = lines.Keys.ToList();
        var components = await db.SalaryComponents.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var result = new List<ContractAllowance>();
        foreach (var (id, cfg) in lines)
        {
            if (!components.TryGetValue(id, out var c) || !c.IsActive || c.IsBasic || c.Type != ComponentType.Earning) continue;
            var amount = cfg.Method switch
            {
                CalculationMethod.FixedAmount => cfg.Amount ?? c.DefaultAmount ?? 0m,
                CalculationMethod.PercentOfBasic => Math.Round(a.BasicSalary * (cfg.Rate ?? c.DefaultRate ?? 0m), 2),
                _ => 0m, // variable and %-of-gross items are not fixed terms
            };
            if (amount > 0) result.Add(new ContractAllowance { Name = c.Name, Amount = amount });
        }
        return result.OrderBy(x => x.Name).ToList();
    }

    private async Task<string?> DepartmentAsync(Guid? id, CancellationToken ct) => id is { } d ? await db.Departments.Where(x => x.Id == d).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
    private async Task<string?> ManagerAsync(Guid? id, CancellationToken ct) =>
        id is { } m ? await db.Employees.Where(x => x.Id == m).Select(x => x.FirstName + " " + x.LastName + (x.JobTitle != null ? ", " + x.JobTitle : "")).FirstOrDefaultAsync(ct) : null;
    private async Task<string?> LocationAsync(Guid? branchId, string? workLocation, Guid legalEntityId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(workLocation)) return workLocation;
        if (branchId is { } b && await db.Branches.Where(x => x.Id == b).Select(x => new { x.Name, x.Location }).FirstOrDefaultAsync(ct) is { } br)
            return string.IsNullOrWhiteSpace(br.Location) ? br.Name : $"{br.Name}, {br.Location}";
        return await db.LegalEntities.Where(e => e.Id == legalEntityId).Select(e => e.Address).FirstOrDefaultAsync(ct);
    }

    private static string Pay(ContractTerms t) => t.DailyRate is { } d ? $"{t.Currency} {d:N2} a day" : $"{t.Currency} {t.Basic:N2} a month";

    private static ContractTerms Clone(ContractTerms t) => System.Text.Json.JsonSerializer.Deserialize<ContractTerms>(System.Text.Json.JsonSerializer.Serialize(t))!;
    private static string? Mask(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Length <= 4 ? id : new string('*', id.Length - 4) + id[^4..];
    private static string Notice(int days) => days switch { 30 => "one month", 60 => "two months", 90 => "three months", 7 => "seven days", 14 => "two weeks", 28 => "twenty-eight days", _ => $"{days} days" };
    /// <summary>"12 months" when the term is a whole number of months (1 Jan – 31 Dec), otherwise the number of days.</summary>
    private static string Duration(DateOnly start, DateOnly end)
    {
        var after = end.AddDays(1);
        var months = (after.Year - start.Year) * 12 + after.Month - start.Month;
        return months >= 1 && start.AddMonths(months) == after ? $"{months} month{(months == 1 ? "" : "s")}" : $"{end.DayNumber - start.DayNumber + 1} days";
    }

    /// <summary>"one month" → "one month's", "seven days" → "seven days'" (as in "one month's notice").</summary>
    private static string Possessive(string s) => s.EndsWith('s') ? s + "'" : s + "'s";
    private static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
    private static string Words(string s) => WordsRx().Replace(s, " $1").Trim();
    private static string Dedent(string s) => string.Join('\n', s.Replace("\r", "").Split('\n').Select(l => l.Trim()));

    [GeneratedRegex(@"\{([a-z_]+)\}")] private static partial Regex PlaceholderRx();
    [GeneratedRegex(@"\[to be completed: ([^\]]+)\]")] private static partial Regex MissingRx();
    [GeneratedRegex("(?<!^)([A-Z])")] private static partial Regex WordsRx();
}
