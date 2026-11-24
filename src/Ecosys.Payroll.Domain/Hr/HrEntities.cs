using Ecosys.Payroll.Domain.Common;

namespace Ecosys.Payroll.Domain.Hr;

// =====================================================================================
// Shared
// =====================================================================================

/// <summary>Uploaded file (attachments, CVs, certificates, contracts). Stored in the database, size-limited, hashed.</summary>
public class StoredFile : TenantEntity
{
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required string Sha256 { get; set; }
    public byte[] Content { get; set; } = [];
    /// <summary>What the file belongs to, e.g. "LeaveRequest", "EmployeeDocument", "Candidate".</summary>
    public required string OwnerType { get; set; }
    public Guid? OwnerId { get; set; }
    /// <summary>Employee whose personal data this is; drives self-service access.</summary>
    public Guid? EmployeeId { get; set; }
}

public enum PayItemStatus { Pending, Processed, Cancelled }

/// <summary>
/// One-off amount to pay or recover in the first regular payroll run whose period contains <see cref="DueDate"/>
/// (bonuses from compensation reviews, final dues, expense reimbursements, recoveries). Taxed through the
/// normal engine via the component's settings.
/// </summary>
public class ScheduledPayItem : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public Guid SalaryComponentId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly DueDate { get; set; }
    public required string Description { get; set; }
    /// <summary>Originating record, e.g. "Separation", "CompensationReview", "ExpenseClaim".</summary>
    public required string SourceType { get; set; }
    public Guid SourceId { get; set; }
    public PayItemStatus Status { get; set; } = PayItemStatus.Pending;
    public Guid? ProcessedInRunId { get; set; }
}

// =====================================================================================
// Exits
// =====================================================================================

public enum SeparationType { Resignation, Termination, Redundancy, Retirement, Death, EndOfContract, SummaryDismissal }
public enum SeparationStatus { Initiated, Clearance, DuesCalculated, Approved, Completed, Cancelled }

public class Separation : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public SeparationType Type { get; set; }
    public DateOnly NoticeDate { get; set; }
    public DateOnly LastWorkingDay { get; set; }
    /// <summary>Notice days the employee actually serves; shortfall against the contractual period is paid (or recovered) in lieu.</summary>
    public int NoticeDaysServed { get; set; }
    public bool PayNoticeInLieu { get; set; }
    public required string Reason { get; set; }
    public SeparationStatus Status { get; set; } = SeparationStatus.Initiated;
    public List<ChecklistItem> Checklist { get; set; } = [];
    public string? ExitInterviewNotes { get; set; }
    public bool EligibleForRehire { get; set; } = true;
    /// <summary>Snapshot of the computed final dues (JSON), frozen at approval.</summary>
    public FinalDues? Dues { get; set; }
    public Guid InitiatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}

public sealed class ChecklistItem
{
    public required string Key { get; set; }
    public required string Title { get; set; }
    public string Owner { get; set; } = "HR";
    public bool Done { get; set; }
    public string? DoneBy { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
    public string? Note { get; set; }
}

public sealed class FinalDues
{
    public decimal MonthlyBasic { get; set; }
    public decimal DailyRateWorking { get; set; }
    public decimal DailyRateCalendar { get; set; }
    public int CompletedYearsOfService { get; set; }
    public List<FinalDuesLine> Lines { get; set; } = [];
    public decimal TotalEarnings => Lines.Where(l => l.Amount > 0).Sum(l => l.Amount);
    public decimal TotalRecoveries => -Lines.Where(l => l.Amount < 0).Sum(l => l.Amount);
    public DateOnly PayableInPeriodEnding { get; set; }
    public List<string> Notes { get; set; } = [];
}

public sealed class FinalDuesLine
{
    public required string Code { get; set; }
    public required string Description { get; set; }
    /// <summary>Positive = earning to pay, negative = recovery.</summary>
    public decimal Amount { get; set; }
    public required string Working { get; set; }
}

// =====================================================================================
// Performance
// =====================================================================================

public enum ReviewCycleType { Probation, MidYear, Annual }
public enum ReviewCycleStatus { Draft, GoalSetting, Review, Calibration, Closed }

public class ReviewCycle : LegalEntityScopedEntity
{
    public required string Name { get; set; }
    public ReviewCycleType Type { get; set; } = ReviewCycleType.Annual;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public ReviewCycleStatus Status { get; set; } = ReviewCycleStatus.Draft;
    /// <summary>Weight of goals in the overall score; competencies take the remainder.</summary>
    public decimal GoalWeightPercent { get; set; } = 70m;
    public List<string> Competencies { get; set; } = ["Job knowledge", "Quality of work", "Teamwork", "Integrity", "Customer focus"];
    public bool Include360 { get; set; }
}

public enum GoalStatus { Draft, Agreed, InProgress, Achieved, PartiallyAchieved, Missed, Cancelled }

/// <summary>Employee goal (EmployeeId set) or department goal (EmployeeId null) that employee goals cascade from.</summary>
public class Goal : LegalEntityScopedEntity
{
    public Guid ReviewCycleId { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? ParentGoalId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public string? Measure { get; set; }
    public string? Target { get; set; }
    public decimal WeightPercent { get; set; }
    public decimal ProgressPercent { get; set; }
    public DateOnly? DueDate { get; set; }
    public GoalStatus Status { get; set; } = GoalStatus.Draft;
}

public enum ReviewStatus { NotStarted, SelfAssessment, ManagerReview, Calibration, Completed, Acknowledged }

public class PerformanceReview : LegalEntityScopedEntity
{
    public Guid ReviewCycleId { get; set; }
    public Guid EmployeeId { get; set; }
    /// <summary>Line manager (employee) responsible for the review.</summary>
    public Guid? ReviewerEmployeeId { get; set; }
    public ReviewStatus Status { get; set; } = ReviewStatus.NotStarted;
    public List<GoalRating> GoalRatings { get; set; } = [];
    public List<CompetencyRating> CompetencyRatings { get; set; } = [];
    public string? SelfComments { get; set; }
    public string? ManagerComments { get; set; }
    /// <summary>Weighted score on the 1–5 scale computed from manager ratings.</summary>
    public decimal? ManagerScore { get; set; }
    /// <summary>Rating after calibration (1–5); null until calibrated.</summary>
    public int? FinalRating { get; set; }
    public string? CalibrationNote { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? EmployeeResponse { get; set; }
}

public sealed class GoalRating
{
    public Guid GoalId { get; set; }
    public int? SelfRating { get; set; }
    public int? ManagerRating { get; set; }
    public string? Comment { get; set; }
}

public sealed class CompetencyRating
{
    public required string Competency { get; set; }
    public int? SelfRating { get; set; }
    public int? ManagerRating { get; set; }
}

public enum FeedbackStatus { Requested, Submitted, Declined }

/// <summary>360 feedback from a peer, report or other colleague.</summary>
public class FeedbackRequest : LegalEntityScopedEntity
{
    public Guid PerformanceReviewId { get; set; }
    public Guid SubjectEmployeeId { get; set; }
    public Guid ProviderEmployeeId { get; set; }
    public string Relationship { get; set; } = "Peer";
    public FeedbackStatus Status { get; set; } = FeedbackStatus.Requested;
    public int? Rating { get; set; }
    public string? Strengths { get; set; }
    public string? Improvements { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
}

public enum PlanStatus { Active, Extended, Successful, Unsuccessful, Cancelled }

public class ImprovementPlan : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public Guid? ManagerEmployeeId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public required string Reason { get; set; }
    public List<PlanObjective> Objectives { get; set; } = [];
    public PlanStatus Status { get; set; } = PlanStatus.Active;
    public string? Outcome { get; set; }
}

public sealed class PlanObjective
{
    public required string Description { get; set; }
    public string? SuccessMeasure { get; set; }
    public DateOnly DueDate { get; set; }
    public bool Met { get; set; }
    public string? ReviewNote { get; set; }
}

public enum CompensationReviewStatus { Draft, Submitted, Approved, Rejected, Applied }

/// <summary>Merit increase and bonus exercise driven by a review cycle's final ratings.</summary>
public class CompensationReview : LegalEntityScopedEntity
{
    public Guid ReviewCycleId { get; set; }
    public required string Name { get; set; }
    public DateOnly EffectiveDate { get; set; }
    /// <summary>Rating (1–5) → increase percent, e.g. {5: 10, 4: 7, 3: 4, 2: 0, 1: 0}.</summary>
    public Dictionary<int, decimal> MeritMatrix { get; set; } = new() { [5] = 10m, [4] = 7m, [3] = 4m, [2] = 0m, [1] = 0m };
    /// <summary>Rating → bonus as percent of monthly basic.</summary>
    public Dictionary<int, decimal> BonusMatrix { get; set; } = new() { [5] = 100m, [4] = 60m, [3] = 25m, [2] = 0m, [1] = 0m };
    public decimal? IncreaseBudget { get; set; }
    public decimal? BonusBudget { get; set; }
    public CompensationReviewStatus Status { get; set; } = CompensationReviewStatus.Draft;
    public List<CompensationReviewLine> Lines { get; set; } = [];
    public Guid? PreparedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}

public class CompensationReviewLine : TenantEntity
{
    public Guid CompensationReviewId { get; set; }
    public Guid EmployeeId { get; set; }
    public decimal CurrentBasic { get; set; }
    public int? Rating { get; set; }
    public decimal IncreasePercent { get; set; }
    public decimal NewBasic { get; set; }
    public decimal BonusAmount { get; set; }
    public string? Note { get; set; }
    public bool Excluded { get; set; }
}

// =====================================================================================
// Learning & development
// =====================================================================================

public enum DeliveryMode { Classroom, Online, OnTheJob, External, Blended }

public class TrainingCourse : LegalEntityScopedEntity
{
    public required string Code { get; set; }
    public required string Title { get; set; }
    public string? Provider { get; set; }
    public DeliveryMode Delivery { get; set; }
    public decimal DurationHours { get; set; }
    public decimal CostPerParticipant { get; set; }
    /// <summary>Course approved by NITA, making costs eligible for levy reimbursement claims.</summary>
    public bool NitaApproved { get; set; }
    public string? NitaCourseCode { get; set; }
    /// <summary>Certificate validity; null = does not expire.</summary>
    public int? CertificateValidityMonths { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Training a role must hold: by job title and/or department.</summary>
public class TrainingRequirement : LegalEntityScopedEntity
{
    public Guid TrainingCourseId { get; set; }
    public string? JobTitle { get; set; }
    public Guid? DepartmentId { get; set; }
    public bool Mandatory { get; set; } = true;
}

public enum SessionStatus { Planned, Completed, Cancelled }

public class TrainingSession : LegalEntityScopedEntity
{
    public Guid TrainingCourseId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string? Venue { get; set; }
    public string? Trainer { get; set; }
    public int Capacity { get; set; } = 20;
    /// <summary>Actual total cost; when set, it is shared across attendees instead of the course's per-participant cost.</summary>
    public decimal? ActualTotalCost { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Planned;
    public Guid? CostJournalId { get; set; }
}

public enum EnrolmentStatus { Nominated, Approved, Attended, Completed, Failed, NoShow, Withdrawn }

public class TrainingEnrolment : LegalEntityScopedEntity
{
    public Guid TrainingSessionId { get; set; }
    public Guid EmployeeId { get; set; }
    public EnrolmentStatus Status { get; set; } = EnrolmentStatus.Nominated;
    public decimal? Score { get; set; }
    public string? CertificateNumber { get; set; }
    public DateOnly? CertifiedOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public Guid? CertificateFileId { get; set; }
    public decimal CostAllocated { get; set; }
}

public enum BondStatus { Active, Fulfilled, Recovered, Waived }

/// <summary>Training bond: if the employee leaves within the bond period, the unexpired share of the cost is recoverable.</summary>
public class TrainingBond : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public Guid? TrainingEnrolmentId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly StartDate { get; set; }
    public int Months { get; set; }
    public BondStatus Status { get; set; } = BondStatus.Active;
    public string? Notes { get; set; }

    public decimal OutstandingOn(DateOnly date)
    {
        if (Status != BondStatus.Active || Months <= 0) return 0m;
        var end = StartDate.AddMonths(Months);
        if (date >= end) return 0m;
        var totalDays = end.DayNumber - StartDate.DayNumber;
        var remaining = end.DayNumber - Math.Max(date.DayNumber, StartDate.DayNumber);
        return Math.Round(Amount * remaining / totalDays, 2, MidpointRounding.AwayFromZero);
    }
}

public enum ClaimStatus { Draft, Submitted, Approved, Paid, Rejected }

/// <summary>Claim to NITA for reimbursement of approved training out of the industrial training levy.</summary>
public class NitaClaim : LegalEntityScopedEntity
{
    public required string Reference { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public ClaimStatus Status { get; set; } = ClaimStatus.Draft;
    public decimal TotalClaimed { get; set; }
    public decimal? AmountApproved { get; set; }
    public string? NitaReference { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public List<NitaClaimLine> Lines { get; set; } = [];
}

public class NitaClaimLine : TenantEntity
{
    public Guid NitaClaimId { get; set; }
    public Guid TrainingEnrolmentId { get; set; }
    public Guid EmployeeId { get; set; }
    public required string CourseTitle { get; set; }
    public string? NitaCourseCode { get; set; }
    public DateOnly CompletedOn { get; set; }
    public decimal Amount { get; set; }
}

// =====================================================================================
// Recruitment & onboarding
// =====================================================================================

public enum RequisitionStatus { Draft, PendingApproval, Open, OnHold, Filled, Cancelled }

public class JobRequisition : LegalEntityScopedEntity
{
    /// <summary>Generated by the server.</summary>
    public string Reference { get; set; } = "";
    public required string Title { get; set; }
    public Guid? PositionId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? BranchId { get; set; }
    public int Openings { get; set; } = 1;
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }
    public string EmploymentType { get; set; } = "Permanent";
    /// <summary>Internal only: never shown on the careers page.</summary>
    public string? Justification { get; set; }
    /// <summary>The job description. Shown publicly on the careers page when the requisition is published.</summary>
    public string? Description { get; set; }
    public DateOnly? ClosingDate { get; set; }
    public RequisitionStatus Status { get; set; } = RequisitionStatus.Draft;
    /// <summary>Listed on the public careers page while Open and before the closing date. Salary is never published.</summary>
    public bool IsPublished { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
}

public enum ApplicationStage { Applied, Screening, Interview, Offer, Hired, Rejected, Withdrawn }

/// <summary>A candidate's application to a requisition. Personal data is retained only per the retention policy (Data Protection Act 2019).</summary>
public class JobApplication : LegalEntityScopedEntity
{
    public Guid JobRequisitionId { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public required string Email { get; set; }
    public string? Phone { get; set; }
    public string? Source { get; set; }
    /// <summary>The candidate's own note, from the careers page.</summary>
    public string? CoverNote { get; set; }
    public Guid? CvFileId { get; set; }
    public ApplicationStage Stage { get; set; } = ApplicationStage.Applied;
    public List<InterviewRecord> Interviews { get; set; } = [];
    public decimal? OfferBasic { get; set; }
    public DateOnly? OfferStartDate { get; set; }
    public string? OfferJobTitle { get; set; }
    public string? OfferStatus { get; set; }
    public string? RejectionReason { get; set; }
    public Guid? HiredEmployeeId { get; set; }
    public bool ConsentToRetain { get; set; }
}

/// <summary>
/// The company's offer letter wording, with {placeholders} filled from the offer. One per legal entity, created from
/// a Kenyan default on first use. Plain text: a blank line starts a paragraph, "## " a heading, "- " a bullet.
/// </summary>
public class OfferLetterTemplate : LegalEntityScopedEntity
{
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public string? SignatoryName { get; set; }
    public string? SignatoryTitle { get; set; }
    public int ProbationMonths { get; set; } = 3;
    /// <summary>Days the candidate has to respond, from the day the letter is sent.</summary>
    public int AcceptWithinDays { get; set; } = 7;
}

public enum OfferLetterStatus { Draft, Sent, Accepted, Declined, Withdrawn, Expired }

/// <summary>
/// One version of an offer letter to a candidate. Drafted from the template, edited by HR, then sent: the PDF as sent is
/// kept, and the candidate can accept or decline through a private link. The key terms are copied in so a later change to
/// the offer cannot silently disagree with the letter.
/// </summary>
public class OfferLetter : LegalEntityScopedEntity
{
    public Guid JobApplicationId { get; set; }
    public int Version { get; set; } = 1;
    public required string Reference { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public string? SignatoryName { get; set; }
    public string? SignatoryTitle { get; set; }
    public decimal Basic { get; set; }
    public DateOnly StartDate { get; set; }
    public required string JobTitle { get; set; }
    public DateOnly AcceptBy { get; set; }
    public OfferLetterStatus Status { get; set; } = OfferLetterStatus.Draft;
    public DateTimeOffset? SentAt { get; set; }
    public string? SentBy { get; set; }
    /// <summary>The PDF as sent, and the PDF with the acceptance recorded on it.</summary>
    public Guid? SentFileId { get; set; }
    public Guid? AcceptedFileId { get; set; }
    /// <summary>SHA-256 of the candidate's response link token. Null when the letter was not sent with a link.</summary>
    public string? ResponseTokenHash { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    /// <summary>Name the candidate typed to accept (their electronic signature), or the HR user who recorded a paper response.</summary>
    public string? ResponseName { get; set; }
    public string? ResponseIp { get; set; }
    public bool RecordedByHr { get; set; }
    public string? DeclineReason { get; set; }
}

// =====================================================================================
// Contracts of service and variation letters
// =====================================================================================

public enum ContractKind { PermanentContract, FixedTermContract, CasualContract, InternshipContract, VariationLetter }

/// <summary>Company wording for one kind of contract or the variation letter, with {placeholders}. Created from a Kenyan default on first use.</summary>
public class ContractTemplate : LegalEntityScopedEntity
{
    public ContractKind Kind { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public string? SignatoryName { get; set; }
    public string? SignatoryTitle { get; set; }
}

public enum ContractStatus { Draft, Issued, Signed, Cancelled }

/// <summary>The terms a contract or variation states, kept so the next variation can say exactly what changed.</summary>
public sealed class ContractTerms
{
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? Location { get; set; }
    public string? EmploymentType { get; set; }
    public string? ReportsTo { get; set; }
    public decimal Basic { get; set; }
    /// <summary>The daily wage, for a worker paid by the day; null for a monthly salary. Absent in terms stored before daily pay existed.</summary>
    public decimal? DailyRate { get; set; }
    public string Currency { get; set; } = "KES";
    public List<ContractAllowance> Allowances { get; set; } = [];
    public int NoticeDays { get; set; }
    public DateOnly? EndDate { get; set; }
}

public sealed class ContractAllowance
{
    public required string Name { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>
/// A contract of service or a variation letter for an employee (Employment Act 2007 s.9–10). Drafted from the
/// template, edited, issued (text frozen, PDF kept) and printed for signature; the signed copy is then uploaded and
/// filed in the employee's documents.
/// </summary>
public class ContractDocument : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public ContractKind Kind { get; set; }
    public required string Reference { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public string? SignatoryName { get; set; }
    public string? SignatoryTitle { get; set; }
    /// <summary>Contracts: when the employment (or this term) starts. Variations: when the change takes effect.</summary>
    public DateOnly StartDate { get; set; }
    /// <summary>Fixed-term contracts only.</summary>
    public DateOnly? EndDate { get; set; }
    public ContractStatus Status { get; set; } = ContractStatus.Draft;
    public ContractTerms Terms { get; set; } = new();
    /// <summary>Variations: the terms being changed from.</summary>
    public ContractTerms? PreviousTerms { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public string? IssuedBy { get; set; }
    public Guid? IssuedFileId { get; set; }
    public DateOnly? SignedOn { get; set; }
    public Guid? SignedFileId { get; set; }
    public Guid? EmployeeDocumentId { get; set; }
    public string? CancelReason { get; set; }

    public bool IsContract => Kind != ContractKind.VariationLetter;
}

public sealed class InterviewRecord
{
    public DateTimeOffset ScheduledAt { get; set; }
    public List<string> Panel { get; set; } = [];
    public int? Score { get; set; }
    public string? Recommendation { get; set; }
    public string? Notes { get; set; }
}

public class OnboardingTask : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public required string Title { get; set; }
    public string Owner { get; set; } = "HR";
    public DateOnly DueDate { get; set; }
    public bool Done { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
    public string? DoneBy { get; set; }
}

// =====================================================================================
// Documents
// =====================================================================================

public enum DocumentCategory { Contract, Identification, KraPinCertificate, AcademicCertificate, ProfessionalCertificate, Medical, Disciplinary, Other }

public class EmployeeDocument : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public DocumentCategory Category { get; set; }
    public required string Title { get; set; }
    public Guid StoredFileId { get; set; }
    public DateOnly? IssuedOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public int Version { get; set; } = 1;
    public bool VisibleToEmployee { get; set; } = true;
}

// =====================================================================================
// Disciplinary & grievance
// =====================================================================================

public enum CaseType { Disciplinary, Grievance }
public enum CaseStatus { Reported, Investigation, ShowCauseIssued, HearingScheduled, Decided, Appealed, Closed }
public enum CaseOutcome { None, NoAction, VerbalWarning, WrittenWarning, FinalWarning, Suspension, Dismissal, GrievanceUpheld, GrievanceNotUpheld }

/// <summary>
/// Confidential HR case. A dismissal outcome requires the Employment Act s.41 steps: the employee was told the
/// grounds (show cause) and a hearing was held where they could be accompanied by a colleague or union rep.
/// </summary>
public class HrCase : LegalEntityScopedEntity
{
    /// <summary>Generated by the server.</summary>
    public string Reference { get; set; } = "";
    public CaseType Type { get; set; }
    public Guid EmployeeId { get; set; }
    public required string Category { get; set; }
    public required string Summary { get; set; }
    public DateOnly IncidentDate { get; set; }
    public CaseStatus Status { get; set; } = CaseStatus.Reported;
    public DateOnly? ShowCauseIssuedOn { get; set; }
    public DateOnly? ResponseDueOn { get; set; }
    public DateTimeOffset? HearingAt { get; set; }
    public bool HearingHeld { get; set; }
    public string? Companion { get; set; }
    public CaseOutcome Outcome { get; set; }
    public string? OutcomeReason { get; set; }
    public DateOnly? AppealDeadline { get; set; }
    public List<CaseEvent> Events { get; set; } = [];
}

public sealed class CaseEvent
{
    public DateTimeOffset At { get; set; }
    public required string By { get; set; }
    public required string Kind { get; set; }
    public required string Note { get; set; }
}

// =====================================================================================
// Positions
// =====================================================================================

public class Position : LegalEntityScopedEntity
{
    public required string Code { get; set; }
    public required string Title { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? Grade { get; set; }
    public int BudgetedHeadcount { get; set; } = 1;
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }
    public Guid? ReportsToPositionId { get; set; }
    public bool IsActive { get; set; } = true;
}

// =====================================================================================
// Benefits
// =====================================================================================

/// <summary>Submitted → Verified (HR checks the documents) → Approved, by two different people. Rejected can be resubmitted as a new record.</summary>
public enum DependantStatus { Submitted, Verified, Approved, Rejected }

/// <summary>
/// A dependant (medical cover) and/or nominated beneficiary (death-in-service dues, insurance). Takes effect only once
/// approved: HR verifies the supporting documents (ID, birth or marriage certificate), then a second person approves.
/// </summary>
public class Dependant : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public required string FullName { get; set; }
    public required string Relationship { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? IdNumber { get; set; }
    public string? Phone { get; set; }
    public bool IsBeneficiary { get; set; }
    public decimal BeneficiaryPercent { get; set; }

    public DependantStatus Status { get; set; } = DependantStatus.Submitted;
    /// <summary>Supporting documents in <c>stored_files</c> (owner type Dependant), served through /files/{id}.</summary>
    public List<Guid> DocumentFileIds { get; set; } = [];
    public Guid? VerifiedBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? RejectedBy { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }
}

public class MedicalScheme : LegalEntityScopedEntity
{
    public required string Name { get; set; }
    public required string Provider { get; set; }
    public string? PolicyNumber { get; set; }
    /// <summary>Cover level → monthly premium per member, e.g. {"M": 3500, "M+1": 6000, "M+2": 8000, "M+3": 9500}.</summary>
    public Dictionary<string, decimal> PremiumByCover { get; set; } = [];
    /// <summary>Share of the premium the employee pays through payroll (0–100).</summary>
    public decimal EmployeeSharePercent { get; set; }
    /// <summary>Deduction component used for the employee share (insurance relief treatment comes from the component).</summary>
    public Guid? DeductionComponentId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class BenefitEnrolment : LegalEntityScopedEntity
{
    public Guid EmployeeId { get; set; }
    public Guid MedicalSchemeId { get; set; }
    public required string Cover { get; set; }
    public List<Guid> CoveredDependantIds { get; set; } = [];
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? MembershipNumber { get; set; }
}

// =====================================================================================
// Expenses
// =====================================================================================

public class ExpenseCategory : LegalEntityScopedEntity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? GlAccountCode { get; set; }
    public bool ReceiptRequired { get; set; } = true;
    /// <summary>Tax-free amount per day (e.g. per diem KES 10,000/day under the Finance Act 2025); the excess is taxable.</summary>
    public decimal? DailyTaxFreeCap { get; set; }
    public decimal? MaxPerClaim { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum ExpenseStatus { Draft, Submitted, ManagerApproved, Approved, Rejected, Paid }
public enum ReimbursementRoute { Payroll, Direct }

public class ExpenseClaim : LegalEntityScopedEntity
{
    public required string Reference { get; set; }
    public Guid EmployeeId { get; set; }
    public required string Title { get; set; }
    public List<ExpenseLine> Lines { get; set; } = [];
    public decimal Total { get; set; }
    public decimal TaxableAmount { get; set; }
    public ExpenseStatus Status { get; set; } = ExpenseStatus.Draft;
    public ReimbursementRoute Route { get; set; } = ReimbursementRoute.Payroll;
    public Guid? ManagerApprovedBy { get; set; }
    public Guid? FinanceApprovedBy { get; set; }
    public string? RejectionReason { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
}

public sealed class ExpenseLine
{
    public Guid CategoryId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public decimal Days { get; set; } = 1;
    public required string Description { get; set; }
    public Guid? ReceiptFileId { get; set; }
}

// =====================================================================================
// Assets
// =====================================================================================

public enum AssetStatus { InStock, Issued, UnderRepair, Lost, Retired }

public class Asset : LegalEntityScopedEntity
{
    public required string Tag { get; set; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public string? SerialNumber { get; set; }
    public decimal Value { get; set; }
    public DateOnly? PurchasedOn { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.InStock;
}

public class AssetAssignment : LegalEntityScopedEntity
{
    public Guid AssetId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly IssuedOn { get; set; }
    public DateOnly? ReturnedOn { get; set; }
    public string? ConditionOut { get; set; }
    public string? ConditionIn { get; set; }
    /// <summary>Recoverable from final dues if not returned.</summary>
    public bool ChargeableIfNotReturned { get; set; } = true;
}
