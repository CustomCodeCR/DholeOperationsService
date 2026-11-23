namespace Dhole.Operations.Domain.Models;

/// <summary>Incomplete manual opening. Does not allocate an IDTRA until submitted.</summary>
public sealed class OperationsManualOpeningDraft
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
