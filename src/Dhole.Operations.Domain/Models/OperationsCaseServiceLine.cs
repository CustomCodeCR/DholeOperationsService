namespace Dhole.Operations.Domain.Models;

/// <summary>
/// Actual operative services are distinct from accepted Pricing commercial
/// prices. Operations can request a Pricing review but may not mutate the QUO.
/// </summary>
public sealed class OperationsCaseServiceLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public OperationsCase Case { get; set; } = null!;
    public Guid? PricingServiceLineId { get; set; }
    public Guid? ServiceCatalogId { get; set; }
    public string ServiceCode { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string Source { get; set; } = "OPERATIONS_ADDED";
    public string ReviewStatus { get; set; } = "PENDING_PRICING";
    public string? ReviewReason { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public long Version { get; set; } = 1;
}
