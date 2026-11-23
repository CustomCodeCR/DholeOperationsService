namespace Dhole.Operations.Domain.Models;

/// <summary>Private, incomplete opening wizard state. No IDTRA is allocated until confirmation.</summary>
public sealed class OperationsOpeningAutosaveDraft
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid? CompanyId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
