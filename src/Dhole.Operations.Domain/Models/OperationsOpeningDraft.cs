namespace Dhole.Operations.Domain.Models;

/// <summary>
/// User-owned, incomplete and independently recoverable PostgreSQL opening form.
/// This is never an operational case until explicitly submitted.
/// </summary>
public sealed class OperationsOpeningDraft
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
