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
