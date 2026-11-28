namespace Dhole.Operations.Domain.Models;

/// <summary>Stable execution identity, independent of the commercial modality
/// and of legacy department stage identity. Draft creation never moves a case.</summary>
public sealed class OperationsItineraryLeg
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public Guid PlanRequestId { get; set; }
    public int Sequence { get; set; }
    public string TransportCode { get; set; } = string.Empty;
    public Guid FromLocationId { get; set; }
    public Guid ToLocationId { get; set; }
    public string FromLabel { get; set; } = string.Empty;
    public string ToLabel { get; set; } = string.Empty;
    public string FromCountryCode { get; set; } = string.Empty;
    public string ToCountryCode { get; set; } = string.Empty;
    public Guid TransitStageId { get; set; }
    public Guid? DestinationStageId { get; set; }
    public string DestinationKind { get; set; } = "NONE";
    public Guid? DestinationServiceLineId { get; set; }
    public string Status { get; set; } = "DRAFT";
    public Guid? SrmProviderId { get; set; }
    public DateTimeOffset? PlannedDepartureAtUtc { get; set; }
    public DateTimeOffset? PlannedArrivalAtUtc { get; set; }
    public DateTimeOffset? EstimatedDepartureAtUtc { get; set; }
    public DateTimeOffset? EstimatedArrivalAtUtc { get; set; }
    public DateTimeOffset? ActualDepartureAtUtc { get; set; }
    public DateTimeOffset? ActualArrivalAtUtc { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public long Version { get; set; } = 1;
}
