using System.Text.Json;
using CustomCodeFramework.Messaging.Outbox;

namespace Dhole.Operations.Persistence.Messaging;

/// <summary>Config-compatible transactional outbox factory; explicit source and event ID.</summary>
public static class OperationsOutboxFactory
{
    public const string NotificationRequested = "notifications.notification.requested";

    public static OutboxMessage CreateOpeningAssignedNotification(
        Guid caseId, string idtra, Guid assignmentId, Guid userId)
    {
        if (caseId == Guid.Empty || assignmentId == Guid.Empty || userId == Guid.Empty)
            throw new ArgumentException("Opening notification requires a persisted assignment and recipient.");
        return Create(NotificationRequested, JsonSerializer.Serialize(new
        {
            notificationType = "operations.aperture.validation.assigned",
            channel = "System",
            entityType = "OperationsCase",
            entityId = caseId.ToString("D"),
            subject = $"Nueva apertura asignada: {idtra}",
            body = $"Se te asignó el expediente {idtra} para revisar y validar su apertura.",
            idempotencyKey = $"operations:opening-assignment:{assignmentId:D}:{userId:D}:system",
            recipients = new[] { new { userId = userId.ToString("D") } },
            payload = new { caseId, idtra, action = "review-operations-opening",
                route = $"/operations/cases/{caseId:D}" }
        }));
    }

    public static OutboxMessage CreateOriginAssignedNotification(
        Guid caseId, string idtra, Guid assignmentId, Guid userId, string? email = null)
    {
        if (caseId == Guid.Empty || assignmentId == Guid.Empty || userId == Guid.Empty)
            throw new ArgumentException("Origin notification requires a persisted assignment and recipient.");
        var channel = email is null ? "System" : "Email";
        if (email is not null && !System.Net.Mail.MailAddress.TryCreate(email, out _))
            throw new ArgumentException("Invalid registered recipient email.", nameof(email));
        return Create(NotificationRequested, JsonSerializer.Serialize(new
        {
            notificationType = "operations.origin.assigned",
            channel,
            entityType = "OperationsCase",
            entityId = caseId.ToString("D"),
            subject = $"Nuevo trámite asignado: {idtra}",
            body = $"El expediente {idtra} fue asignado a tu equipo de Coordinación Origen. Ingresa a DHOLE para continuar el trámite.",
            idempotencyKey = $"operations:origin-assignment:{assignmentId:D}:{userId:D}:{channel.ToLowerInvariant()}",
            recipients = new[] { new { userId = userId.ToString("D"), address = email } },
            payload = new { caseId, idtra, action = "open-operations-case",
                route = $"/operations/cases/{caseId:D}" }
        }));
    }

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
