using Dhole.Operations.Domain.Models;
using CustomCodeFramework.Messaging.Outbox;
using CustomCodeFramework.Messaging.Inbox;
using CustomCodeFramework.Postgres.EntityFramework.DbContexts;
using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Persistence;

public sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options) : AppDbContextBase(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<OperationsCase> Cases => Set<OperationsCase>();
    public DbSet<OperationsManualOpeningDraft> ManualOpeningDrafts => Set<OperationsManualOpeningDraft>();
    public DbSet<OperationsCaseSequence> CaseSequences => Set<OperationsCaseSequence>();
    public DbSet<OperationsCaseStage> CaseStages => Set<OperationsCaseStage>();
    public DbSet<OperationsTeam> Teams => Set<OperationsTeam>();
    public DbSet<OperationsTeamMember> TeamMembers => Set<OperationsTeamMember>();
    public DbSet<OperationsAssignmentRule> AssignmentRules => Set<OperationsAssignmentRule>();
    public DbSet<OperationsStageAssignment> StageAssignments => Set<OperationsStageAssignment>();
    public DbSet<OperationsDocument> Documents => Set<OperationsDocument>();
    public DbSet<OperationsDocumentVersion> DocumentVersions => Set<OperationsDocumentVersion>();
    public DbSet<OperationsDocumentExtractionJob> ExtractionJobs => Set<OperationsDocumentExtractionJob>();
    public DbSet<OperationsAcceptedQuoteSnapshot> AcceptedQuoteSnapshots => Set<OperationsAcceptedQuoteSnapshot>();
    public DbSet<OperationsCaseServiceLine> CaseServiceLines => Set<OperationsCaseServiceLine>();
    public DbSet<OperationsCommercialAlert> CommercialAlerts => Set<OperationsCommercialAlert>();
    public DbSet<OperationsPricingReviewRequest> PricingReviewRequests => Set<OperationsPricingReviewRequest>();
    public DbSet<OperationsQuoteDiscrepancyReview> QuoteDiscrepancyReviews => Set<OperationsQuoteDiscrepancyReview>();
    public DbSet<OperationsCaseEquipmentLine> CaseEquipmentLines => Set<OperationsCaseEquipmentLine>();
    public DbSet<OperationsCaseQuoteLink> CaseQuoteLinks => Set<OperationsCaseQuoteLink>();
    public DbSet<OperationsCaseQuoteCost> CaseQuoteCosts => Set<OperationsCaseQuoteCost>();
    public DbSet<OperationsCaseMilestone> CaseMilestones => Set<OperationsCaseMilestone>();
    public DbSet<OperationsCaseTask> CaseTasks => Set<OperationsCaseTask>();
    public DbSet<OperationsCaseNote> CaseNotes => Set<OperationsCaseNote>();
    public DbSet<OperationsCaseAuditEntry> CaseAuditEntries => Set<OperationsCaseAuditEntry>();
    public DbSet<OperationsOriginProgress> OriginProgress => Set<OperationsOriginProgress>();
    public DbSet<OperationsOriginTransfer> OriginTransfers => Set<OperationsOriginTransfer>();
    public DbSet<OperationsItineraryLeg> ItineraryLegs => Set<OperationsItineraryLeg>();
    public DbSet<OperationsInternalNote> InternalNotes => Set<OperationsInternalNote>();
    public DbSet<OperationsCarrierConnection> CarrierConnections => Set<OperationsCarrierConnection>();
    public DbSet<OperationsCarrierCapability> CarrierCapabilities => Set<OperationsCarrierCapability>();
    public DbSet<OperationsApertureValidation> ApertureValidations => Set<OperationsApertureValidation>();
    public DbSet<OperationsTeamAuditEntry> TeamAuditEntries => Set<OperationsTeamAuditEntry>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        // Exact same framework mappings and message entities as DholeConfigService.
        m.HasDefaultSchema("operations");
        base.OnModelCreating(m);
        m.ApplyConfiguration(new OutboxMessageConfiguration());
        m.ApplyConfiguration(new InboxMessageConfiguration());
        // Config inbox does not retain payloads. Operations needs payload for
        // authorized SignalR replay, so an additional shadow column is retained.
        m.Entity<InboxMessage>().Property<string>("PayloadJson")
            .HasColumnName("payload_json").HasColumnType("text").IsRequired();

        m.Entity<OperationsManualOpeningDraft>(e =>
        {
            e.ToTable("operations_manual_opening_drafts");
            e.HasKey(x => x.Id);
            e.Property(x => x.PayloadJson).HasColumnName("payload_json")
                .HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.CompanyId, x.CreatedBy }).IsUnique();
        });
        m.Entity<OperationsCase>(e =>
        {
            e.ToTable("operations_cases", t => t.HasCheckConstraint("ck_operations_case_points", "complexity_points > 0 AND priority BETWEEN 1 AND 5"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Idtra).HasMaxLength(32).IsRequired();
            e.Property(x => x.OpeningDataJson).HasColumnName("opening_data_json").HasColumnType("jsonb").IsRequired();
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
        m.Entity<OperationsItineraryLeg>(e =>
        {
            e.ToTable("operations_itinerary_legs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.CaseId, x.Sequence }).IsUnique();
            e.HasIndex(x => x.PlanRequestId);
            e.Property(x => x.TransportCode).HasMaxLength(20).IsRequired();
            e.Property(x => x.FromLabel).HasMaxLength(250).IsRequired();
            e.Property(x => x.ToLabel).HasMaxLength(250).IsRequired();
            e.Property(x => x.FromCountryCode).HasMaxLength(2).IsRequired();
            e.Property(x => x.ToCountryCode).HasMaxLength(2).IsRequired();
            e.Property(x => x.DestinationKind).HasMaxLength(20).IsRequired();
            e.Property(x => x.EstimatedDepartureSource).HasMaxLength(30);
            e.Property(x => x.EstimatedArrivalSource).HasMaxLength(30);
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<OperationsCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OperationsCaseStage>().WithMany().HasForeignKey(x => new { x.TransitStageId, x.CaseId })
                .HasPrincipalKey(x => new { x.Id, x.CaseId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OperationsCaseStage>().WithMany().HasForeignKey(x => new { x.DestinationStageId, x.CaseId })
                .HasPrincipalKey(x => new { x.Id, x.CaseId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OperationsCaseServiceLine>().WithMany().HasForeignKey(x => new { x.DestinationServiceLineId, x.CaseId })
                .HasPrincipalKey(x => new { x.Id, x.CaseId }).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<OperationsOriginTransfer>(e =>
        {
            e.ToTable("operations_origin_transfers");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RequestId).IsUnique();
            e.HasIndex(x => x.OriginStageId).IsUnique();
            e.HasIndex(x => x.TransitStageId).IsUnique();
            e.Property(x => x.RuleFingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.RuleSnapshotJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.ChecklistJson).HasColumnType("jsonb").IsRequired();
            e.HasOne<OperationsCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OperationsCaseStage>().WithMany().HasForeignKey(x => x.OriginStageId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OperationsCaseStage>().WithMany().HasForeignKey(x => x.TransitStageId).OnDelete(DeleteBehavior.Restrict);
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
            e.Property(x => x.RoleCode).HasMaxLength(30);
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
            e.Property(x => x.AccessClass).HasMaxLength(30);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.OwnerStage).WithMany().HasForeignKey(x => x.OwnerStageId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.OwnerStageId);
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


        m.Entity<OperationsAcceptedQuoteSnapshot>(e =>
        {
            e.ToTable("operations_accepted_quote_snapshots");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.SnapshotJson).HasColumnName("snapshot_json")
                .HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => x.CaseId).IsUnique();
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.QuoteId, x.AcceptedOptionId, x.CommercialRevision });
        });
        m.Entity<OperationsCaseServiceLine>(e =>
        {
            e.ToTable("operations_case_service_lines");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ServiceCode).HasMaxLength(60).IsRequired();
            e.Property(x => x.ServiceName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Source).HasMaxLength(30).IsRequired();
            e.Property(x => x.ReviewStatus).HasMaxLength(40).IsRequired();
            e.HasIndex(x => x.CaseId);
            e.HasIndex(x => new { x.CaseId, x.IdempotencyKey }).IsUnique()
                .HasFilter("source = 'OPERATIONS_ADDED' AND idempotency_key IS NOT NULL");
        });
        m.Entity<OperationsCaseEquipmentLine>(e =>
        {
            e.ToTable("operations_case_equipment_lines");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.EquipmentCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.EquipmentName).HasMaxLength(200).IsRequired();
            e.Property(x => x.UnitWeightsKgJson).HasColumnName("unit_weights_kg_json")
                .HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => x.CaseId);
        });

        m.Entity<OperationsCommercialAlert>(e =>
        {
            e.ToTable("operations_commercial_alerts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(50);
            e.Property(x => x.Severity).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(40);
            e.HasOne<OperationsCaseServiceLine>().WithMany()
                .HasForeignKey(x => x.ServiceLineId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<OperationsCase>().WithMany()
                .HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ServiceLineId).IsUnique();
            e.HasIndex(x => new { x.CaseId, x.Status });
        });
        m.Entity<OperationsPricingReviewRequest>(e =>
        {
            e.ToTable("operations_pricing_review_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Reason).HasColumnType("text").IsRequired();
            e.Property(x => x.Status).HasMaxLength(40);
            e.HasOne<OperationsCaseServiceLine>().WithMany()
                .HasForeignKey(x => x.ServiceLineId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<OperationsCase>().WithMany()
                .HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ServiceLineId).IsUnique();
            e.HasIndex(x => new { x.CaseId, x.Status });
        });

        m.Entity<OperationsQuoteDiscrepancyReview>(e =>
        {
            e.ToTable("operations_quote_discrepancy_reviews");
            e.HasKey(x => x.Id);
            e.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.DifferencesJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Reason).HasColumnType("text").IsRequired();
            e.Property(x => x.Status).HasMaxLength(40);
            e.HasOne<OperationsCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CaseId, x.Fingerprint }).IsUnique();
            e.HasIndex(x => new { x.CaseId, x.Status });
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
                "ck_operations_quote_currency", "currency_code ~ '^[A-Z]{3}$'"));
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

        m.Entity<OperationsOriginProgress>(e =>
        {
            e.ToTable("operations_origin_progress");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.CaseStage).WithMany().HasForeignKey(x => x.CaseStageId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.CaseStageId).IsUnique();
            e.HasIndex(x => x.DepartureEventId).IsUnique();
            e.Property(x => x.StatusCode).HasMaxLength(40).IsRequired();
            e.Property(x => x.DepartureSource).HasMaxLength(40);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        m.Entity<OperationsInternalNote>(e =>
        {
            e.ToTable("operations_internal_notes");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<OperationsCaseStage>().WithMany().HasForeignKey(x => x.CaseStageId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Body).HasColumnType("text").IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.CaseId, x.CreatedAtUtc });
        });
        m.Entity<OperationsCarrierConnection>(e =>
        {
            e.ToTable("operations_carrier_connections");
            e.HasKey(x => x.Id);
            e.Property(x => x.CarrierCode).HasMaxLength(30).IsRequired();
            e.Property(x => x.Environment).HasMaxLength(12).IsRequired();
            e.Property(x => x.Status).HasMaxLength(40).IsRequired();
            e.HasIndex(x => new { x.CompanyId, x.CarrierCode, x.Status });
        });
        m.Entity<OperationsCarrierCapability>(e =>
        {
            e.ToTable("operations_carrier_capabilities");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Connection).WithMany(x => x.Capabilities).HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ProductCode).HasMaxLength(60).IsRequired();
            e.Property(x => x.CapabilityCode).HasMaxLength(60).IsRequired();
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.ApiVersion).HasMaxLength(30);
            e.Property(x => x.EvidenceRef).HasMaxLength(200);
            e.HasIndex(x => new { x.ConnectionId, x.ProductCode, x.CapabilityCode }).IsUnique();
        });

        m.Entity<OperationsApertureValidation>(e =>
        {
            e.ToTable("operations_aperture_validations");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.CaseStage).WithMany().HasForeignKey(x => x.CaseStageId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<OperationsTeam>().WithMany().HasForeignKey(x => x.TeamId)
                .OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.Comment).HasMaxLength(2000);
            e.HasIndex(x => new { x.CaseStageId, x.Slot }).IsUnique();
            e.HasIndex(x => new { x.CaseStageId, x.UserId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.Status });
        });
        m.Entity<OperationsTeamAuditEntry>(e =>
        {
            e.ToTable("operations_team_audit_entries");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId)
                .OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ActionCode).HasMaxLength(100).IsRequired();
            e.Property(x => x.BeforeJson).HasColumnType("jsonb");
            e.Property(x => x.AfterJson).HasColumnType("jsonb");
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.TeamId, x.OccurredAtUtc });
        });

        // Business entities use the established snake_case convention from Config.
        // Messaging columns are mapped by CustomCodeFramework configurations.
        foreach (var entity in m.Model.GetEntityTypes().Where(e =>
            e.ClrType != typeof(OutboxMessage) && e.ClrType != typeof(InboxMessage)))
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
    }

    private static string ToSnakeCase(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
}
