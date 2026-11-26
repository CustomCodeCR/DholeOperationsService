using System.Text.Json;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Messaging;

namespace Dhole.Operations.Persistence.Audit;

// Immutable local history and external AuditLogs event are atomically persisted via Outbox.
public static class OperationsAuditWriter
{
    public static void Team(OperationsDbContext db, Guid teamId, Guid actor, string action,
        object? before, object? after)
    {
        var eventId = Guid.NewGuid();
        var beforeJson = before is null ? null : JsonSerializer.Serialize(before);
        var afterJson = after is null ? null : JsonSerializer.Serialize(after);
        db.TeamAuditEntries.Add(new OperationsTeamAuditEntry {
            TeamId = teamId, EventId = eventId, ActorUserId = actor,
            ActionCode = action, BeforeJson = beforeJson, AfterJson = afterJson
        });
        EnqueueAudit(db, eventId, "OperationsTeam", teamId, actor, action, beforeJson, afterJson);
        // Transactional invalidation. Auth derives permissions from the latest complete
        // membership snapshot, not from possibly reordered individual grant events.
        if (action is "operations.team.member.added" or "operations.team.member.updated" or
            "operations.team.member.removed" or "operations.team.active.changed")
            db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.team.permissions.changed.v1",
                JsonSerializer.Serialize(new { teamId })));
    }
    public static void Case(OperationsDbContext db, Guid caseId, Guid? stageId,
        Guid actor, string action, object? before, object? after)
    {
        var eventId = Guid.NewGuid();
        var beforeJson = before is null ? null : JsonSerializer.Serialize(before);
        var afterJson = after is null ? null : JsonSerializer.Serialize(after);
        db.CaseAuditEntries.Add(new OperationsCaseAuditEntry {
            CaseId = caseId, CaseStageId = stageId, EventId = eventId,
            ActorUserId = actor, ActionCode = action, BeforeJson = beforeJson, AfterJson = afterJson
        });
        EnqueueAudit(db, eventId, "OperationsCase", caseId, actor, action, beforeJson, afterJson);
    }
    // For external entities without an Operations-owned case or team, the Outbox is the
    // durable local record until AuditLogs consumes the event with an idempotent Inbox.
    public static void Entity(OperationsDbContext db, string entityType, Guid entityId,
        Guid actor, string action, object? before, object? after)
    {
        EnqueueAudit(db, Guid.NewGuid(), entityType, entityId, actor, action,
            before is null ? null : JsonSerializer.Serialize(before),
            after is null ? null : JsonSerializer.Serialize(after));
    }

    private static void EnqueueAudit(OperationsDbContext db, Guid eventId, string entityType,
        Guid entityId, Guid actor, string action, string? beforeJson, string? afterJson)
    {
        // Matches DholeAuditLogsService RegisterAuditEventRequest.
        var data = new {
            eventId, correlationId = eventId, sourceService = "DholeOperationsService",
            entityType, entityId, action, eventType = "operations.audit.v1",
            userId = actor, userName = (string?)null, ipAddress = (string?)null,
            userAgent = (string?)null, occurredAt = DateTime.UtcNow,
            beforeJson, afterJson, payloadJson = (string?)null, metadata = (string?)null,
            errorMessage = (string?)null, stackTrace = (string?)null,
            details = (object?)null, entityName = (string?)null, description = (string?)null,
            httpMethod = (string?)null, requestPath = (string?)null
        };
        db.OutboxMessages.Add(OperationsOutboxFactory.Create("audit.event.registered",
            JsonSerializer.Serialize(data)));
    }
}
