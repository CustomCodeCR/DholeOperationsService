namespace Dhole.Operations.Domain.Models;

/// <summary>
/// A recoverable manual-opening workspace. This is deliberately separate from an
/// OperationsCase: incomplete drafts do not consume IDTRA numbers, create a
/// commercial quotation, or assign an operator.
/// </summary>
public sealed class OperationsManualOpeningDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid CreatedBy { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
