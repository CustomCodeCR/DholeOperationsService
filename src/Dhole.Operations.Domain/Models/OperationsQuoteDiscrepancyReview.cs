namespace Dhole.Operations.Domain.Models;

/// <summary>One immutable set of commercial differences reviewed by Pricing.
/// This is not a service line and never changes accepted costs or sales.</summary>
public sealed class OperationsQuoteDiscrepancyReview
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public Guid QuoteId { get; set; }
    public Guid AcceptedOptionId { get; set; }
    public int CommercialRevision { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string DifferencesJson { get; set; } = "[]";
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING_PRICING";
    public Guid RequestedBy { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid? PricingRequestId { get; set; }
    public long LatestPricingVersion { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public string? DecisionReason { get; set; }
}
