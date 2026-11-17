using Dhole.Operations.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Persistence;

public sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxReceipt> InboxReceipts => Set<InboxReceipt>();
    public DbSet<OperationsCase> Cases => Set<OperationsCase>();
    public DbSet<OperationsCaseSequence> CaseSequences => Set<OperationsCaseSequence>();
    public DbSet<OperationsCaseStage> CaseStages => Set<OperationsCaseStage>();
    public DbSet<OperationsTeam> Teams => Set<OperationsTeam>();
    public DbSet<OperationsTeamMember> TeamMembers => Set<OperationsTeamMember>();
    public DbSet<OperationsAssignmentRule> AssignmentRules => Set<OperationsAssignmentRule>();
    public DbSet<OperationsStageAssignment> StageAssignments => Set<OperationsStageAssignment>();
    public DbSet<OperationsDocument> Documents => Set<OperationsDocument>();
    public DbSet<OperationsDocumentVersion> DocumentVersions => Set<OperationsDocumentVersion>();
    public DbSet<OperationsDocumentExtractionJob> ExtractionJobs => Set<OperationsDocumentExtractionJob>();
    public DbSet<OperationsCaseQuoteLink> CaseQuoteLinks => Set<OperationsCaseQuoteLink>();
    public DbSet<OperationsCaseQuoteCost> CaseQuoteCosts => Set<OperationsCaseQuoteCost>();
    public DbSet<OperationsCaseMilestone> CaseMilestones => Set<OperationsCaseMilestone>();
    public DbSet<OperationsCaseTask> CaseTasks => Set<OperationsCaseTask>();
    public DbSet<OperationsCaseNote> CaseNotes => Set<OperationsCaseNote>();
    public DbSet<OperationsCaseAuditEntry> CaseAuditEntries => Set<OperationsCaseAuditEntry>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<OutboxMessage>(e =>
        {
            e.ToTable("operations_outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventName).HasMaxLength(200).IsRequired();
            e.Property(x => x.PayloadJson).HasColumnType("text").IsRequired();
            e.Property(x => x.LastError).HasColumnType("text");
            e.HasIndex(x => new { x.PublishedAtUtc, x.OccurredAtUtc });
        });
        m.Entity<InboxReceipt>(e =>
        {
            e.ToTable("operations_inbox");
            e.HasKey(x => new { x.ConsumerName, x.EventId });
            e.Property(x => x.ConsumerName).HasMaxLength(100);
            e.Property(x => x.EventName).HasMaxLength(200).IsRequired();
            e.Property(x => x.PayloadJson).HasColumnType("text").IsRequired();
            e.HasIndex(x => x.ProcessedAtUtc);
        });

        m.Entity<OperationsCase>(e =>
        {
            e.ToTable("operations_cases", t => t.HasCheckConstraint("ck_operations_case_points", "complexity_points > 0 AND priority BETWEEN 1 AND 5"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Idtra).HasMaxLength(32).IsRequired();
            e.Property(x => x.SeriesCode).HasMaxLength(5).IsRequired();
            e.Property(x => x.ModalityCode).HasMaxLength(30).IsRequired();
            e.Property(x => x.ApertureType).HasMaxLength(20).IsRequired();
            e.Property(x => x.OriginCountryCode).HasMaxLength(2);
            e.Property(x => x.DestinationCountryCode).HasMaxLength(2);
            e.Property(x => x.Status).HasMaxLength(40).IsRequired();
            e.Property(x => x.ComplexityPoints).HasPrecision(10, 2);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.Idtra).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.Status, x.UpdatedAtUtc });
            e.HasIndex(x => x.AccountId);
            e.HasIndex(x => x.SourceQuoteId);
            e.HasIndex(x => x.ExecutiveUserId);
        });
        m.Entity<OperationsCaseSequence>(e =>
        {
            e.ToTable("operations_case_sequences");
            e.HasKey(x => x.Id);
            e.Property(x => x.SeriesCode).HasMaxLength(5);
            e.HasIndex(x => x.SeriesCode).IsUnique();
        });
        m.Entity<OperationsCaseStage>(e =>
        {
            e.ToTable("operations_case_stages");
            e.HasKey(x => x.Id);
            e.Property(x => x.DepartmentCode).HasMaxLength(30);
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Case).WithMany(x => x.Stages).HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<OperationsTeam>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.CaseId, x.DepartmentCode }).IsUnique();
            e.HasIndex(x => new { x.DepartmentCode, x.Status });
        });
        m.Entity<OperationsTeam>(e =>
        {
            e.ToTable("operations_teams");
            e.HasKey(x => x.Id);
            e.Property(x => x.DepartmentCode).HasMaxLength(30);
            e.Property(x => x.Name).HasMaxLength(150);
            e.HasIndex(x => new { x.CompanyId, x.DepartmentCode, x.Name }).IsUnique();
        });
        m.Entity<OperationsTeamMember>(e =>
        {
            e.ToTable("operations_team_members", t => t.HasCheckConstraint("ck_operations_member_capacity", "capacity_points > 0"));
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Team).WithMany(x => x.Members).HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.CapacityPoints).HasPrecision(10, 2);
            e.HasIndex(x => new { x.TeamId, x.UserId }).IsUnique();
        });
        m.Entity<OperationsAssignmentRule>(e =>
        {
            e.ToTable("operations_assignment_rules");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.DepartmentCode).HasMaxLength(30);
            e.Property(x => x.ModalityCode).HasMaxLength(30);
            e.Property(x => x.OriginCountryCode).HasMaxLength(2);
            e.Property(x => x.DestinationCountryCode).HasMaxLength(2);
            e.HasIndex(x => new { x.DepartmentCode, x.IsActive, x.Priority });
        });
        m.Entity<OperationsStageAssignment>(e =>
        {
            e.ToTable("operations_stage_assignments");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.CaseStage).WithMany().HasForeignKey(x => x.CaseStageId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.AssignedPoints).HasPrecision(10, 2);
            e.HasOne<OperationsTeam>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.CaseStageId).IsUnique().HasFilter("released_at_utc IS NULL");
            e.HasIndex(x => new { x.UserId, x.ReleasedAtUtc });
        });
        m.Entity<OperationsDocument>(e =>
        {
            e.ToTable("operations_documents");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.DocumentTypeCode).HasMaxLength(60);
            e.Property(x => x.Status).HasMaxLength(30);
            e.HasIndex(x => new { x.CaseId, x.DocumentTypeCode });
        });
        m.Entity<OperationsDocumentVersion>(e =>
        {
            e.ToTable("operations_document_versions");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Document).WithMany(x => x.Versions).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.MimeType).HasMaxLength(150);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.ReviewStatus).HasMaxLength(30);
            e.HasIndex(x => new { x.DocumentId, x.VersionNumber }).IsUnique();
            e.HasIndex(x => x.StorageFileId).IsUnique();
        });
        m.Entity<OperationsDocumentExtractionJob>(e =>
        {
            e.ToTable("operations_document_extraction_jobs");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.DocumentVersion).WithMany().HasForeignKey(x => x.DocumentVersionId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.ExtractedJson).HasColumnType("jsonb");
            e.Property(x => x.ValidationErrorsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.DocumentVersionId, x.RequestedAtUtc });
            e.HasIndex(x => new { x.Status, x.RequestedAtUtc });
        });

        m.Entity<OperationsCaseQuoteLink>(e =>
        {
            e.ToTable("operations_case_quote_links", t => t.HasCheckConstraint(
                "ck_operations_quote_snapshot_version", "snapshot_version >= 1"));
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.QuoteNumber).HasMaxLength(60);
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.HasIndex(x => new { x.CaseId, x.LinkedAtUtc });
            e.HasIndex(x => x.QuoteId);
            e.HasIndex(x => x.CaseId).IsUnique().HasFilter("is_primary");
        });
        m.Entity<OperationsCaseQuoteCost>(e =>
        {
            e.ToTable("operations_case_quote_costs", t => t.HasCheckConstraint(
                "ck_operations_quote_currency", "currency_code ~ '^[A-Z]{3}
        foreach (var entity in m.Model.GetEntityTypes().Where(e =>
            e.ClrType != typeof(OutboxMessage) && e.ClrType != typeof(InboxReceipt)))
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
    }

    private static string ToSnakeCase(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
}
"));
            e.HasKey(x => x.Id);
            e.HasOne(x => x.QuoteLink).WithMany(x => x.Costs).HasForeignKey(x => x.QuoteLinkId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.SupplierName).HasMaxLength(200);
            e.Property(x => x.CostCode).HasMaxLength(60).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500).IsRequired();
            e.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
            e.Property(x => x.CostAmount).HasPrecision(18, 4);
            e.Property(x => x.SaleAmount).HasPrecision(18, 4);
            e.HasIndex(x => x.QuoteLinkId);
            e.HasIndex(x => x.SupplierId);
            e.HasIndex(x => new { x.QuoteLinkId, x.SourceCostId }).IsUnique()
                .HasFilter("source_cost_id IS NOT NULL");
        });
        m.Entity<OperationsCaseMilestone>(e =>
        {
            e.ToTable("operations_case_milestones");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CaseStage).WithMany().HasForeignKey(x => x.CaseStageId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.MilestoneCode).HasMaxLength(60).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.HasIndex(x => new { x.CaseId, x.PlannedAtUtc });
            e.HasIndex(x => x.CaseStageId);
        });
        m.Entity<OperationsCaseTask>(e =>
        {
            e.ToTable("operations_case_tasks", t => t.HasCheckConstraint(
                "ck_operations_task_priority", "priority BETWEEN 1 AND 5"));
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CaseStage).WithMany().HasForeignKey(x => x.CaseStageId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Details).HasColumnType("text");
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.CaseId, x.Status, x.DueAtUtc });
            e.HasIndex(x => new { x.AssignedUserId, x.Status, x.DueAtUtc });
            e.HasIndex(x => x.CaseStageId);
        });
        m.Entity<OperationsCaseNote>(e =>
        {
            e.ToTable("operations_case_notes");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CaseStage).WithMany().HasForeignKey(x => x.CaseStageId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Content).HasColumnType("text").IsRequired();
            e.Property(x => x.Visibility).HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.CaseId, x.CreatedAtUtc });
            e.HasIndex(x => x.CaseStageId);
        });
        m.Entity<OperationsCaseAuditEntry>(e =>
        {
            e.ToTable("operations_case_audit_entries");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CaseStage).WithMany().HasForeignKey(x => x.CaseStageId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ActionCode).HasMaxLength(100).IsRequired();
            e.Property(x => x.BeforeJson).HasColumnType("jsonb");
            e.Property(x => x.AfterJson).HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.CaseId, x.OccurredAtUtc });
            e.HasIndex(x => x.CaseStageId);
        });

        // Keep legacy Outbox/Inbox columns untouched to preserve the existing database.
        foreach (var entity in m.Model.GetEntityTypes().Where(e =>
            e.ClrType != typeof(OutboxMessage) && e.ClrType != typeof(InboxReceipt)))
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
    }

    private static string ToSnakeCase(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
}
