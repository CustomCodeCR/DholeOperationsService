using System.Text.Json;

namespace Dhole.Operations.Domain.Models;

/// <summary>Persist before publishing to Redis Streams; publishing may be retried.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }

    public static OutboxMessage Create(string eventName, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(eventName)) throw new ArgumentException("Event name is required.", nameof(eventName));
        using var parsed = JsonDocument.Parse(payloadJson);
        return new OutboxMessage { EventName = eventName, PayloadJson = payloadJson };
    }
}
