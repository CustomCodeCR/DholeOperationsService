namespace Dhole.Operations.Domain.Models;

/// <summary>
/// Server-owned unsent wizard state. No IDTRA, CRM record or commercial
/// acceptance is created until the user explicitly confirms the aperture.
/// </summary>
public sealed class OperationsApertureDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public long Version { get; set; } = 1;
}
