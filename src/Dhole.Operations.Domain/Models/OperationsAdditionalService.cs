namespace Dhole.Operations.Domain.Models;

/// <summary>An unquoted Operations service with a durable, separately resolved Pricing review.</summary>
public sealed class OperationsAdditionalService
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public Guid? SourceQuoteId { get; set; }
    public string ServiceCode { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Source { get; set; } = "OPERATIONS_ADDED";
    public string ReviewStatus { get; set; } = "PENDING_PRICING_REVIEW";
    public Guid AddedBy { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public string? PricingResolutionNotes { get; set; }
}
