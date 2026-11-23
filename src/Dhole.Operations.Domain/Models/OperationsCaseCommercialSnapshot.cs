namespace Dhole.Operations.Domain.Models;

/// <summary>Immutable commercial acceptance version owned by Operations, separate from editable aperture data.</summary>
public sealed class OperationsCaseCommercialSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public Guid QuoteId { get; set; }
    public int RevisionNumber { get; set; }
    public string SnapshotJson { get; set; } = "{}";
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
