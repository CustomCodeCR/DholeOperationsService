namespace Dhole.Operations.Domain.Models;

/// <summary>Immutable, authoritative Pricing snapshot captured when client accepted the option.</summary>
public sealed class OperationsAcceptedQuoteSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public OperationsCase Case { get; set; } = null!;
    public Guid EventId { get; set; }
    public Guid QuoteId { get; set; }
    public Guid AcceptedOptionId { get; set; }
    public int CommercialRevision { get; set; }
    public string SnapshotJson { get; set; } = "{}";
    public DateTimeOffset AcceptedAtUtc { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Operations services are references, not editable Pricing tariffs.</summary>
public sealed class OperationsCaseServiceLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public OperationsCase Case { get; set; } = null!;
    public Guid? PricingServiceId { get; set; }
    public string ServiceCode { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string Source { get; set; } = "QUOTED";
    public bool RequiresPricingReview { get; set; }
    public string ReviewStatus { get; set; } = "NOT_REQUIRED";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OperationsCaseEquipmentLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public OperationsCase Case { get; set; } = null!;
    public Guid EquipmentTypeId { get; set; }
    public string EquipmentCode { get; set; } = string.Empty;
    public string EquipmentName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string UnitWeightsKgJson { get; set; } = "[]";
}
