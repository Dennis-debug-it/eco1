using Ecosys.Payroll.Domain.Common;
using Ecosys.Payroll.Domain.Compensation;
using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Leave;
using Ecosys.Payroll.Infrastructure.Services.Hr;
using Microsoft.EntityFrameworkCore;

namespace Ecosys.Payroll.Infrastructure.Seed;

/// <summary>
/// HR sample data for the demo tenant. Each block is idempotent, so this also upgrades a demo database created before
/// the HR modules existed. Never runs outside demo seeding.
/// </summary>
public sealed partial class DemoDataSeeder
{
    public async Task EnsureDemoHrDataAsync(CancellationToken ct = default)
    {
        db.IgnoreTenantFilter = true;
        var t = await db.Tenants.FirstOrDefaultAsync(x => x.Slug == DemoTenantSlug, ct);
        db.IgnoreTenantFilter = false;
        if (t is null) return;
        tenant.TenantId = t.Id; tenant.UserName = "seed"; tenant.IsSystem = true;
        var le = await db.LegalEntities.FirstAsync(ct);
        var emps = await db.Employees.Where(e => e.LegalEntityId == le.Id).ToDictionaryAsync(e => e.EmployeeNumber, ct);
        Employee E(string no) => emps[no];
        var today = BusinessClock.Today();

        // The company's paying account, needed to generate bank payment files.
        if (!await db.CompanyBankAccounts.AnyAsync(a => a.LegalEntityId == le.Id, ct))
            db.CompanyBankAccounts.Add(new Domain.Organisation.CompanyBankAccount
            {
                TenantId = t.Id, LegalEntityId = le.Id, Label = "Salaries account (KCB)", BankCode = "01", BankName = "KCB Bank Kenya",
                BranchCode = "091", BranchName = "Moi Avenue", AccountName = le.Name, AccountNumber = "1107654321", IsDefault = true,
            });

        // Reporting lines: MD at the top; managers below.
        if (emps.Values.All(e => e.ReportingManagerId is null) && emps.Count >= 16)
        {
            var lines = new Dictionary<string, string>
            {
                ["SL002"] = "SL001", ["SL003"] = "SL001", ["SL004"] = "SL001", ["SL006"] = "SL001", ["SL007"] = "SL001",
                ["SL005"] = "SL002", ["SL013"] = "SL003", ["SL014"] = "SL006", ["SL015"] = "SL007",
                ["SL008"] = "SL004", ["SL009"] = "SL004", ["SL010"] = "SL008", ["SL011"] = "SL008", ["SL012"] = "SL009", ["SL016"] = "SL004",
            };
            foreach (var (emp, mgr) in lines) E(emp).ReportingManagerId = E(mgr).Id;
            E("SL014").ProbationEndDate = E("SL014").HireDate.AddMonths(6);
            E("SL012").ContractEndDate = today.AddDays(30);
        }

        // Public holidays and leave pay tiers.
        if (!await db.PublicHolidays.AnyAsync(h => h.LegalEntityId == le.Id, ct))
            foreach (var y in new[] { 2025, 2026, 2027 })
                foreach (var (date, observed, name) in WorkCalendar.WithObservedDates(WorkCalendar.KenyaHolidays(y)))
                    db.PublicHolidays.Add(new PublicHoliday { TenantId = t.Id, LegalEntityId = le.Id, Date = date, ObservedDate = observed, Name = name });
        var types = await db.LeaveTypes.Where(x => x.LegalEntityId == le.Id).ToDictionaryAsync(x => x.Code, ct);
        if (types.TryGetValue("SICK", out var sick) && sick.FullPayDaysPerYear is null) { sick.FullPayDaysPerYear = 7; sick.HalfPayDaysPerYear = 7; sick.MinServiceMonths = 2; }
        if (types.TryGetValue("MAT", out var mat)) mat.CountsWorkingDaysOnly = false;
        if (!types.ContainsKey("COMP"))
            db.LeaveTypes.Add(new LeaveType { TenantId = t.Id, LegalEntityId = le.Id, Code = "COMP", Name = "Compassionate leave", AnnualEntitlementDays = 5, AccruesMonthly = false });

        // Positions from current job titles.
        if (!await db.Positions.AnyAsync(p => p.LegalEntityId == le.Id, ct))
        {
            // Seniority band from depth in the reporting lines: A for the top, then B, C, D.
            int Depth(Employee e) { var d = 1; for (var m = e.ReportingManagerId; m is { } id && d < 10; m = emps.Values.FirstOrDefault(x => x.Id == id)?.ReportingManagerId) d++; return d; }
            var n = 0;
            var created = new List<(Position Pos, List<Employee> Holders)>();
            foreach (var g in emps.Values.GroupBy(e => (e.JobTitle ?? "Staff", e.DepartmentId)))
            {
                var depth = g.Min(Depth);
                var p = new Position { TenantId = t.Id, LegalEntityId = le.Id, Code = $"POS-{++n:D3}", Title = g.Key.Item1, DepartmentId = g.Key.DepartmentId, BudgetedHeadcount = g.Key.Item1 == "Driver" ? 3 : g.Count(), Grade = $"Band {(char)('A' + Math.Min(depth, 5) - 1)}" };
                db.Positions.Add(p);
                foreach (var e in g) e.PositionId = p.Id;
                created.Add((p, g.ToList()));
            }
            foreach (var (pos, holders) in created)
            {
                var mgr = holders.Select(h => h.ReportingManagerId).FirstOrDefault(m => m is not null);
                var parent = mgr is { } mid ? emps.Values.FirstOrDefault(e => e.Id == mid)?.PositionId : null;
                if (parent != pos.Id) pos.ReportsToPositionId = parent;
            }
        }

        // Learning catalogue, requirements and a completed session with certificates.
        if (!await db.TrainingCourses.AnyAsync(c => c.LegalEntityId == le.Id, ct))
        {
            TrainingCourse C(string code, string title, string provider, DeliveryMode mode, decimal hours, decimal cost, bool nita, int? validity) => new()
            {
                TenantId = t.Id, LegalEntityId = le.Id, Code = code, Title = title, Provider = provider, Delivery = mode, DurationHours = hours, CostPerParticipant = cost,
                NitaApproved = nita, NitaCourseCode = nita ? $"NITA/{code}" : null, CertificateValidityMonths = validity,
            };
            var firstAid = C("FA-L1", "First Aid Level 1", "Kenya Red Cross training (external)", DeliveryMode.Classroom, 16, 8_500, true, 24);
            var driving = C("DD-01", "Defensive Driving", "AA Driving School (external)", DeliveryMode.External, 24, 12_000, true, 36);
            var fire = C("FS-01", "Fire Safety & Evacuation", "In-house safety officer", DeliveryMode.Classroom, 4, 1_500, false, 12);
            var dpa = C("DPA-01", "Data Protection Act Awareness", "Online", DeliveryMode.Online, 2, 0, false, null);
            db.TrainingCourses.AddRange(firstAid, driving, fire, dpa);
            var ops = await db.Departments.FirstOrDefaultAsync(d => d.Code == "OPS", ct);
            db.TrainingRequirements.AddRange(
                new TrainingRequirement { TenantId = t.Id, LegalEntityId = le.Id, TrainingCourseId = driving.Id, JobTitle = "Driver" },
                new TrainingRequirement { TenantId = t.Id, LegalEntityId = le.Id, TrainingCourseId = firstAid.Id, DepartmentId = ops?.Id },
                new TrainingRequirement { TenantId = t.Id, LegalEntityId = le.Id, TrainingCourseId = dpa.Id, JobTitle = "HR Business Partner" });
            var done = new TrainingSession { TenantId = t.Id, LegalEntityId = le.Id, TrainingCourseId = driving.Id, StartDate = today.AddMonths(-35).AddDays(-20), EndDate = today.AddMonths(-35).AddDays(-18), Venue = "Mombasa", Capacity = 10, Status = SessionStatus.Completed };
            db.TrainingSessions.Add(done);
            foreach (var no in new[] { "SL010", "SL011" })
                db.TrainingEnrolments.Add(new TrainingEnrolment { TenantId = t.Id, LegalEntityId = le.Id, TrainingSessionId = done.Id, EmployeeId = E(no).Id, Status = EnrolmentStatus.Completed, Score = 82, CertificateNumber = $"DD-{no}", CertifiedOn = done.EndDate, ExpiresOn = done.EndDate.AddMonths(36), CostAllocated = 12_000 });
            db.TrainingSessions.Add(new TrainingSession { TenantId = t.Id, LegalEntityId = le.Id, TrainingCourseId = firstAid.Id, StartDate = today.AddDays(21), EndDate = today.AddDays(22), Venue = "Nairobi HQ", Trainer = "Red Cross", Capacity = 12 });
        }

        // Expense categories (per diem tax-free cap per Finance Act 2025).
        if (!await db.ExpenseCategories.AnyAsync(c => c.LegalEntityId == le.Id, ct))
            db.ExpenseCategories.AddRange(
                new ExpenseCategory { TenantId = t.Id, LegalEntityId = le.Id, Code = "PERDIEM", Name = "Per diem (official travel)", GlAccountCode = "6210", ReceiptRequired = false, DailyTaxFreeCap = 10_000 },
                new ExpenseCategory { TenantId = t.Id, LegalEntityId = le.Id, Code = "TRAVEL", Name = "Transport & fares", GlAccountCode = "6211" },
                new ExpenseCategory { TenantId = t.Id, LegalEntityId = le.Id, Code = "ACCOM", Name = "Accommodation", GlAccountCode = "6212" },
                new ExpenseCategory { TenantId = t.Id, LegalEntityId = le.Id, Code = "FUEL", Name = "Fuel", GlAccountCode = "6213" },
                new ExpenseCategory { TenantId = t.Id, LegalEntityId = le.Id, Code = "OTHER", Name = "Other business expense", GlAccountCode = "6219", MaxPerClaim = 20_000 });

        // Medical scheme (fictional insurer) with the employee share deducted through payroll.
        if (!await db.MedicalSchemes.AnyAsync(s => s.LegalEntityId == le.Id, ct))
        {
            var comp = await new SystemComponents(db).EnsureAsync(t.Id, le.Id, "MEDICAL", ct);
            var scheme = new MedicalScheme
            {
                TenantId = t.Id, LegalEntityId = le.Id, Name = "Staff medical cover", Provider = "Afya Bora Health (sample insurer)", PolicyNumber = "ABH-2026-0042",
                PremiumByCover = new() { ["M"] = 3_500, ["M+1"] = 6_000, ["M+2"] = 8_000, ["M+3"] = 9_500 }, EmployeeSharePercent = 25, DeductionComponentId = comp.Id,
            };
            db.MedicalSchemes.Add(scheme);
            var brian = E("SL006");
            var child = new Dependant { TenantId = t.Id, LegalEntityId = le.Id, EmployeeId = brian.Id, FullName = "Imani Mwangi", Relationship = "Child", DateOfBirth = new DateOnly(2019, 4, 2), IsBeneficiary = true, BeneficiaryPercent = 100, Status = DependantStatus.Approved, ApprovedAt = DateTimeOffset.UtcNow };
            db.Dependants.Add(child);
            db.BenefitEnrolments.Add(new BenefitEnrolment { TenantId = t.Id, LegalEntityId = le.Id, EmployeeId = brian.Id, MedicalSchemeId = scheme.Id, Cover = "M+1", CoveredDependantIds = [child.Id], StartDate = new DateOnly(today.Year, 1, 1), MembershipNumber = "ABH-0042-118" });
        }

        // Assets.
        if (!await db.Assets.AnyAsync(a => a.LegalEntityId == le.Id, ct))
        {
            var laptop = new Asset { TenantId = t.Id, LegalEntityId = le.Id, Tag = "LT-0001", Name = "Laptop (14\")", Category = "IT", SerialNumber = "SN-88213", Value = 95_000, Status = AssetStatus.Issued };
            var phone = new Asset { TenantId = t.Id, LegalEntityId = le.Id, Tag = "PH-0007", Name = "Field smartphone", Category = "IT", SerialNumber = "SN-55120", Value = 28_000, Status = AssetStatus.Issued };
            var spare = new Asset { TenantId = t.Id, LegalEntityId = le.Id, Tag = "LT-0002", Name = "Laptop (14\")", Category = "IT", SerialNumber = "SN-88214", Value = 95_000 };
            db.Assets.AddRange(laptop, phone, spare);
            db.AssetAssignments.Add(new AssetAssignment { TenantId = t.Id, LegalEntityId = le.Id, AssetId = laptop.Id, EmployeeId = E("SL006").Id, IssuedOn = new DateOnly(2024, 2, 1), ConditionOut = "New" });
            db.AssetAssignments.Add(new AssetAssignment { TenantId = t.Id, LegalEntityId = le.Id, AssetId = phone.Id, EmployeeId = E("SL012").Id, IssuedOn = new DateOnly(2025, 5, 1), ConditionOut = "Good" });
        }

        // Performance cycle in goal setting with department and employee goals.
        if (!await db.ReviewCycles.AnyAsync(c => c.LegalEntityId == le.Id, ct))
        {
            var cycle = new ReviewCycle { TenantId = t.Id, LegalEntityId = le.Id, Name = $"FY{today.Year} Annual review", Type = ReviewCycleType.Annual, PeriodStart = new DateOnly(today.Year, 1, 1), PeriodEnd = new DateOnly(today.Year, 12, 31), Status = ReviewCycleStatus.GoalSetting };
            db.ReviewCycles.Add(cycle);
            var opsDept = await db.Departments.FirstOrDefaultAsync(d => d.Code == "OPS", ct);
            var deptGoal = new Goal { TenantId = t.Id, LegalEntityId = le.Id, ReviewCycleId = cycle.Id, DepartmentId = opsDept?.Id, Title = "On-time delivery ≥ 95%", Measure = "Deliveries on time / total", Target = "95%", WeightPercent = 0, Status = GoalStatus.Agreed };
            db.Goals.Add(deptGoal);
            foreach (var e in emps.Values.Where(e => e.TerminationDate == null))
                db.PerformanceReviews.Add(new PerformanceReview { TenantId = t.Id, LegalEntityId = le.Id, ReviewCycleId = cycle.Id, EmployeeId = e.Id, ReviewerEmployeeId = e.ReportingManagerId, CompetencyRatings = cycle.Competencies.Select(x => new CompetencyRating { Competency = x }).ToList() });
            void G(string no, string title, decimal w, Guid? parent = null) => db.Goals.Add(new Goal { TenantId = t.Id, LegalEntityId = le.Id, ReviewCycleId = cycle.Id, EmployeeId = E(no).Id, ParentGoalId = parent, Title = title, WeightPercent = w, Status = GoalStatus.Draft });
            G("SL008", "Fleet availability ≥ 92%", 50, deptGoal.Id); G("SL008", "Reduce fuel cost per km by 5%", 30); G("SL008", "Zero lost-time incidents", 20);
            G("SL014", "Resolve 90% of tickets within SLA", 60); G("SL014", "Roll out laptop encryption", 40);
        }

        // Recruitment: an open requisition with applicants.
        if (!await db.JobRequisitions.AnyAsync(r => r.LegalEntityId == le.Id, ct))
        {
            var driverPos = await db.Positions.FirstOrDefaultAsync(p => p.LegalEntityId == le.Id && p.Title == "Driver", ct);
            var req = new JobRequisition { TenantId = t.Id, LegalEntityId = le.Id, Reference = $"REQ-{today.Year}-001", Title = "Driver", PositionId = driverPos?.Id, DepartmentId = E("SL010").DepartmentId, BranchId = E("SL010").BranchId, Openings = 1, SalaryMin = 38_000, SalaryMax = 45_000, Justification = "Fleet expansion (third truck)", Status = RequisitionStatus.Open, RequestedBy = Guid.Empty,
                Description = "We are hiring a driver for our Mombasa–Nairobi route.\n\nYou will:\n- Drive a 30-tonne truck safely and on schedule\n- Complete pre-trip vehicle checks and delivery paperwork\n\nYou have:\n- A valid class CE driving licence and a clean driving record\n- At least three years' experience driving heavy commercial vehicles",
                IsPublished = true, PublishedAt = DateTimeOffset.UtcNow };
            db.JobRequisitions.Add(req);
            db.JobApplications.AddRange(
                new JobApplication { TenantId = t.Id, LegalEntityId = le.Id, JobRequisitionId = req.Id, FirstName = "Kevin", LastName = "Otieno", Email = "kevin.otieno@example.com", Phone = "+254711000111", Source = "Referral", Stage = ApplicationStage.Interview, ConsentToRetain = true,
                    Interviews = [new InterviewRecord { ScheduledAt = DateTimeOffset.UtcNow.AddDays(-3), Panel = ["Kipchoge Rotich", "Daniel Kiptoo"], Score = 4, Recommendation = "Proceed to offer" }] },
                new JobApplication { TenantId = t.Id, LegalEntityId = le.Id, JobRequisitionId = req.Id, FirstName = "Ruth", LastName = "Akinyi", Email = "ruth.akinyi@example.com", Source = "Job board", Stage = ApplicationStage.Screening });
        }

        await db.SaveChangesAsync(ct);
    }
}
