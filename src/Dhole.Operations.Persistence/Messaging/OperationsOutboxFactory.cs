using System.Text.Json;
using CustomCodeFramework.Messaging.Outbox;

namespace Dhole.Operations.Persistence.Messaging;

/// <summary>Config-compatible transactional outbox factory; explicit source and event ID.</summary>
public static class OperationsOutboxFactory
{
    public static OutboxMessage Create(string eventName, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            throw new ArgumentException("Event name is required.", nameof(eventName));
        using var parsed = JsonDocument.Parse(payloadJson);
        return new OutboxMessage
        {
            EventId = Guid.NewGuid(),
            EventType = eventName,
            EventName = eventName,
            SourceService = "DholeOperationsService",
            PayloadJson = payloadJson,
            Status = OutboxMessageStatus.Pending,
            RetryCount = 0,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
