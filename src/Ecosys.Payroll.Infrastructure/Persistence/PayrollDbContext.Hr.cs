using Ecosys.Payroll.Domain.Employees;
using Ecosys.Payroll.Domain.Hr;
using Ecosys.Payroll.Domain.Leave;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecosys.Payroll.Infrastructure.Persistence;

/// <summary>HR modules: leave calendar, exits, performance, learning, recruitment, documents, cases, positions, benefits, expenses, assets.</summary>
public partial class PayrollDbContext
{
    public DbSet<PublicHoliday> PublicHolidays => Set<PublicHoliday>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<ScheduledPayItem> ScheduledPayItems => Set<ScheduledPayItem>();
    public DbSet<Separation> Separations => Set<Separation>();
    public DbSet<ReviewCycle> ReviewCycles => Set<ReviewCycle>();
    public DbSet<Goal> Goals => Set<Goal>();
    public DbSet<PerformanceReview> PerformanceReviews => Set<PerformanceReview>();
    public DbSet<FeedbackRequest> FeedbackRequests => Set<FeedbackRequest>();
    public DbSet<ImprovementPlan> ImprovementPlans => Set<ImprovementPlan>();
    public DbSet<CompensationReview> CompensationReviews => Set<CompensationReview>();
    public DbSet<CompensationReviewLine> CompensationReviewLines => Set<CompensationReviewLine>();
    public DbSet<TrainingCourse> TrainingCourses => Set<TrainingCourse>();
    public DbSet<TrainingRequirement> TrainingRequirements => Set<TrainingRequirement>();
    public DbSet<TrainingSession> TrainingSessions => Set<TrainingSession>();
    public DbSet<TrainingEnrolment> TrainingEnrolments => Set<TrainingEnrolment>();
    public DbSet<TrainingBond> TrainingBonds => Set<TrainingBond>();
    public DbSet<NitaClaim> NitaClaims => Set<NitaClaim>();
    public DbSet<NitaClaimLine> NitaClaimLines => Set<NitaClaimLine>();
    public DbSet<JobRequisition> JobRequisitions => Set<JobRequisition>();
    public DbSet<JobApplication> JobApplications => Set<JobApplication>();
    public DbSet<OnboardingTask> OnboardingTasks => Set<OnboardingTask>();
    public DbSet<EmployeeDocument> EmployeeDocuments => Set<EmployeeDocument>();
    public DbSet<HrCase> HrCases => Set<HrCase>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Dependant> Dependants => Set<Dependant>();
    public DbSet<MedicalScheme> MedicalSchemes => Set<MedicalScheme>();
    public DbSet<BenefitEnrolment> BenefitEnrolments => Set<BenefitEnrolment>();
    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();
    public DbSet<ExpenseClaim> ExpenseClaims => Set<ExpenseClaim>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetAssignment> AssetAssignments => Set<AssetAssignment>();

    private static void JsonColumn<T>(PropertyBuilder<T> p, Func<T> fallback) where T : class =>
        p.HasColumnType("jsonb").HasConversion(JsonConverter(fallback), JsonComparer<T>());

    private void ConfigureLeaveAndJournals(ModelBuilder b)
    {
        Scoped<PublicHoliday>(b, e => e.HasIndex(x => new { x.LegalEntityId, x.Date }).IsUnique());
        b.Entity<Domain.Finance.PayrollJournal>().HasIndex(x => new { x.LegalEntityId, x.Source });
    }

    private void ConfigureHr(ModelBuilder b)
    {
        TenantEntity<StoredFile>(b, e => { e.HasIndex(x => new { x.OwnerType, x.OwnerId }); e.HasIndex(x => x.EmployeeId); });
        Scoped<ScheduledPayItem>(b, e => { e.HasIndex(x => new { x.EmployeeId, x.Status, x.DueDate }); e.HasIndex(x => new { x.SourceType, x.SourceId }); });

        Scoped<Separation>(b, e =>
        {
            e.HasIndex(x => x.EmployeeId);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            JsonColumn(e.Property(x => x.Checklist), () => new List<ChecklistItem>());
            e.Property(x => x.Dues).HasColumnType("jsonb").HasConversion(JsonConverter(() => new FinalDues())!, JsonComparer<FinalDues>()!);
        });

        Scoped<ReviewCycle>(b, e => e.Property(x => x.Competencies).HasColumnType("jsonb"));
        Scoped<Goal>(b, e => { e.HasIndex(x => new { x.ReviewCycleId, x.EmployeeId }); e.HasOne<ReviewCycle>().WithMany().HasForeignKey(x => x.ReviewCycleId); });
        Scoped<PerformanceReview>(b, e =>
        {
            e.HasIndex(x => new { x.ReviewCycleId, x.EmployeeId }).IsUnique();
            e.HasIndex(x => x.ReviewerEmployeeId);
            e.HasOne<ReviewCycle>().WithMany().HasForeignKey(x => x.ReviewCycleId);
            JsonColumn(e.Property(x => x.GoalRatings), () => new List<GoalRating>());
            JsonColumn(e.Property(x => x.CompetencyRatings), () => new List<CompetencyRating>());
        });
        Scoped<FeedbackRequest>(b, e => { e.HasIndex(x => x.PerformanceReviewId); e.HasIndex(x => x.ProviderEmployeeId); });
        Scoped<ImprovementPlan>(b, e => { e.HasIndex(x => x.EmployeeId); JsonColumn(e.Property(x => x.Objectives), () => new List<PlanObjective>()); });
        Scoped<CompensationReview>(b, e =>
        {
            JsonColumn(e.Property(x => x.MeritMatrix), () => new Dictionary<int, decimal>());
            JsonColumn(e.Property(x => x.BonusMatrix), () => new Dictionary<int, decimal>());
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.CompensationReviewId).OnDelete(DeleteBehavior.Cascade);
        });
        TenantEntity<CompensationReviewLine>(b, e => e.HasIndex(x => new { x.CompensationReviewId, x.EmployeeId }).IsUnique());

        Scoped<TrainingCourse>(b, e => e.HasIndex(x => new { x.LegalEntityId, x.Code }).IsUnique());
        Scoped<TrainingRequirement>(b, e => e.HasOne<TrainingCourse>().WithMany().HasForeignKey(x => x.TrainingCourseId));
        Scoped<TrainingSession>(b, e => e.HasOne<TrainingCourse>().WithMany().HasForeignKey(x => x.TrainingCourseId));
        Scoped<TrainingEnrolment>(b, e =>
        {
            e.HasIndex(x => new { x.TrainingSessionId, x.EmployeeId }).IsUnique();
            e.HasIndex(x => new { x.EmployeeId, x.ExpiresOn });
            e.HasOne<TrainingSession>().WithMany().HasForeignKey(x => x.TrainingSessionId);
            e.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.CertificateNumber);
        });
        Scoped<TrainingBond>(b, e => e.HasIndex(x => new { x.EmployeeId, x.Status }));
        Scoped<NitaClaim>(b, e =>
        {
            e.HasIndex(x => new { x.LegalEntityId, x.Reference }).IsUnique();
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.NitaClaimId).OnDelete(DeleteBehavior.Cascade);
        });
        TenantEntity<NitaClaimLine>(b, e => e.HasIndex(x => x.TrainingEnrolmentId).IsUnique());

        Scoped<JobRequisition>(b, e => e.HasIndex(x => new { x.LegalEntityId, x.Reference }).IsUnique());
        Scoped<JobApplication>(b, e =>
        {
            e.HasIndex(x => new { x.JobRequisitionId, x.Stage });
            e.HasOne<JobRequisition>().WithMany().HasForeignKey(x => x.JobRequisitionId);
            e.Property(x => x.Email).HasConversion(encryptor.RequiredConverter());
            e.Property(x => x.Phone).HasConversion(encryptor.Converter());
            JsonColumn(e.Property(x => x.Interviews), () => new List<InterviewRecord>());
        });
        Scoped<OnboardingTask>(b, e => e.HasIndex(x => new { x.EmployeeId, x.Done }));
        Scoped<EmployeeDocument>(b, e => { e.HasIndex(x => new { x.EmployeeId, x.Category }); e.HasIndex(x => x.ExpiresOn); });
        Scoped<HrCase>(b, e =>
        {
            e.HasIndex(x => new { x.LegalEntityId, x.Reference }).IsUnique();
            e.HasIndex(x => x.EmployeeId);
            JsonColumn(e.Property(x => x.Events), () => new List<CaseEvent>());
        });
        Scoped<Position>(b, e => e.HasIndex(x => new { x.LegalEntityId, x.Code }).IsUnique());
        Scoped<Dependant>(b, e =>
        {
            e.HasIndex(x => x.EmployeeId);
            e.Property(x => x.IdNumber).HasConversion(encryptor.Converter());
        });
        Scoped<MedicalScheme>(b, e => JsonColumn(e.Property(x => x.PremiumByCover), () => new Dictionary<string, decimal>()));
        Scoped<BenefitEnrolment>(b, e => { e.HasIndex(x => x.EmployeeId); e.Property(x => x.CoveredDependantIds).HasColumnType("jsonb"); e.HasOne<MedicalScheme>().WithMany().HasForeignKey(x => x.MedicalSchemeId); });
        Scoped<ExpenseCategory>(b, e => e.HasIndex(x => new { x.LegalEntityId, x.Code }).IsUnique());
        Scoped<ExpenseClaim>(b, e =>
        {
            e.HasIndex(x => new { x.LegalEntityId, x.Reference }).IsUnique();
            e.HasIndex(x => new { x.EmployeeId, x.Status });
            JsonColumn(e.Property(x => x.Lines), () => new List<ExpenseLine>());
        });
        Scoped<Asset>(b, e => e.HasIndex(x => new { x.LegalEntityId, x.Tag }).IsUnique());
        Scoped<AssetAssignment>(b, e => { e.HasIndex(x => new { x.AssetId, x.ReturnedOn }); e.HasIndex(x => x.EmployeeId); e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId); });
    }
}
