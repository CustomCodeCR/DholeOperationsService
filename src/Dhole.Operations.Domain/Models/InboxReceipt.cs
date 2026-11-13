namespace Dhole.Operations.Domain.Models;

/// <summary>Consumer+event key deduplicates at-least-once Redis stream delivery.</summary>
public sealed class InboxReceipt
{
    public string ConsumerName { get; set; } = string.Empty;
    public Guid EventId { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
