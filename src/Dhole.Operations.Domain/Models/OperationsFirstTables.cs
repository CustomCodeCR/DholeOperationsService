namespace Dhole.Operations.Domain.Models;

// First operational data layer. Source CRM, Pricing and SRM entities are referenced by UUID;
// Operations never creates foreign keys against external microservices.
public sealed class OperationsCaseQuoteLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public OperationsCase Case { get; set; } = null!;
    public Guid QuoteId { get; set; }
    public Guid? QuoteOptionId { get; set; }
    public string? QuoteNumber { get; set; }
    public bool IsPrimary { get; set; }
    public string Status { get; set; } = "LINKED";
    public int SnapshotVersion { get; set; } = 1;
    public string? CommercialSnapshotJson { get; set; }
    public Guid LinkedBy { get; set; }
    public DateTimeOffset LinkedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<OperationsCaseQuoteCost> Costs { get; set; } = [];
}

// Informational snapshot. No mutation of Pricing costs or SRM suppliers is performed here.
public sealed class OperationsCaseQuoteCost
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid QuoteLinkId { get; set; }
    public OperationsCaseQuoteLink QuoteLink { get; set; } = null!;
    public Guid? SourceCostId { get; set; }
    public Guid? SupplierId { get; set; }
    public string? SupplierName { get; set; }
    public string CostCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = "USD";
    public decimal CostAmount { get; set; }
    public decimal SaleAmount { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OperationsCaseMilestone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public OperationsCase Case { get; set; } = null!;
    public Guid? CaseStageId { get; set; }
    public OperationsCaseStage? CaseStage { get; set; }
    public string MilestoneCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING";
    public DateTimeOffset? PlannedAtUtc { get; set; }
    public DateTimeOffset? ActualAtUtc { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OperationsCaseTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public OperationsCase Case { get; set; } = null!;
    public Guid? CaseStageId { get; set; }
    public OperationsCaseStage? CaseStage { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string Status { get; set; } = "PENDING";
    public short Priority { get; set; } = 3;
    public Guid? AssignedUserId { get; set; }
    public DateTimeOffset? DueAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public long Version { get; set; } = 1;
}

public sealed class OperationsCaseNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public OperationsCase Case { get; set; } = null!;
    public Guid? CaseStageId { get; set; }
    public OperationsCaseStage? CaseStage { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Visibility { get; set; } = "TEAM";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}

// Append-only intent: the business layer will populate this table in later releases.
// Immutable event IDs allow consumers to retry safely without producing duplicate history.
public sealed class OperationsCaseAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public OperationsCase Case { get; set; } = null!;
    public Guid? CaseStageId { get; set; }
    public OperationsCaseStage? CaseStage { get; set; }
    public Guid EventId { get; set; }
    public string ActionCode { get; set; } = string.Empty;
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
