using Dhole.Operations.Persistence.Audit;
using System.Data;
using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>First functional v1.2 backend slice. No carrier calls or human-account provisioning.</summary>
public static class OperationsPreparationEndpoints
{
    public sealed record NewCarrierConnection(Guid CompanyId, string CarrierCode, string Environment, Guid? SrmProviderId);
    public sealed record ConfirmDeparture(long ExpectedVersion, Guid EventId, DateTimeOffset ActualDepartureAtUtc, Guid EvidenceStorageFileId);
    public sealed record CreateInternalNote(string Body);

    public static IEndpointRouteBuilder MapOperationsPreparationEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization();

        api.MapGet("/carrier-connections", async (HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.carrier.connection.read"))
                return Results.Forbid();
            var company = OperationsCaseAccess.CompanyId(http.User);
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) && company is null) return Results.Forbid();
            var query = db.CarrierConnections.AsNoTracking();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User)) query = query.Where(x => x.CompanyId == company);
            var connections = await query.OrderBy(x => x.CarrierCode).ThenBy(x => x.CreatedAtUtc)
                .Select(x => new { x.Id, x.CompanyId, x.CarrierCode, x.SrmProviderId,
                    x.Environment, x.Status, x.LastValidatedAtUtc, x.CreatedAtUtc,
                    Capabilities = x.Capabilities.Select(c => new { c.ProductCode, c.CapabilityCode,
                        c.Status, c.ApiVersion, c.VerifiedAtUtc }) })
                .ToArrayAsync(ct);
            return Results.Ok(connections);
        });

        api.MapPost("/carrier-connections", async (NewCarrierConnection input,
            HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.carrier.connection.manage") ||
                (!OperationsCaseAccess.IsSystemAdministrator(http.User) && OperationsCaseAccess.CompanyId(http.User) != input.CompanyId))
                return Results.Forbid();
            var carrier = input.CarrierCode?.Trim().ToUpperInvariant();
            var environment = input.Environment?.Trim().ToUpperInvariant();
            if (input.CompanyId == Guid.Empty || !OperationsCarrierCodes.IsKnown(carrier) ||
                environment is not ("UAT" or "PRODUCTION") ||
                input.SrmProviderId == Guid.Empty)
                return Results.BadRequest(new { code = "CARRIER_CONNECTION_INVALID" });
            var connection = new OperationsCarrierConnection
            {
                CompanyId = input.CompanyId, CarrierCode = carrier!, Environment = environment!,
                SrmProviderId = input.SrmProviderId, Status = "REQUESTED"
            };
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null) return Results.Forbid();
            db.CarrierConnections.Add(connection);
            OperationsAuditWriter.Entity(db, "OperationsCarrierConnection", connection.Id,
                actor.Value, "operations.carrier.connection.requested", null,
                new { connection.CompanyId, connection.CarrierCode, connection.Environment, connection.SrmProviderId });
            await db.SaveChangesAsync(ct);
            // Never return ACTIVE, grant scopes, issue credentials or call carriers on registration.
            return Results.Created($"/api/operations/carrier-connections/{connection.Id}",
                new { connection.Id, connection.CarrierCode, connection.Environment, connection.Status });
        });

        api.MapGet("/carriers/{carrierCode}/capabilities", async (string carrierCode,
            HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.carrier.connection.read"))
                return Results.Forbid();
            var company = OperationsCaseAccess.CompanyId(http.User);
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) && company is null) return Results.Forbid();
            var code = carrierCode.Trim().ToUpperInvariant();
            if (!OperationsCarrierCodes.IsKnown(code)) return Results.NotFound();
            var query = db.CarrierConnections.AsNoTracking().Where(x => x.CarrierCode == code);
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User)) query = query.Where(x => x.CompanyId == company);
            var entries = await query.Select(x => new { x.Id, x.CompanyId, x.Environment,
                x.Status, x.LastValidatedAtUtc, Capabilities = x.Capabilities.Select(c =>
                    new { c.ProductCode, c.CapabilityCode, c.Status, c.ApiVersion, c.VerifiedAtUtc }) })
                .ToArrayAsync(ct);
            // Empty means NOT_CONFIGURED, not a fictional enabled integration.
            return Results.Ok(new { carrierCode = code,
                connectionStatus = entries.Length == 0 ? "NOT_CONFIGURED" : "REGISTERED",
                connections = entries });
        });

        api.MapGet("/cases/{id:guid}/origin/progress", async (Guid id, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.origin.read") ||
                !await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var stage = await db.CaseStages.AsNoTracking()
                .Where(x => x.CaseId == id && x.DepartmentCode == "COORDINACION_ORIGEN")
                .Select(x => new { x.Id, x.Status, x.Version }).SingleOrDefaultAsync(ct);
            if (stage is null) return Results.NotFound();
            var progress = await db.OriginProgress.AsNoTracking()
                .Where(x => x.CaseStageId == stage.Id)
                .Select(x => new { x.StatusCode, x.ActualDepartureAtUtc, x.DepartureSource,
                    x.EvidenceStorageFileId, x.Version }).SingleOrDefaultAsync(ct);
            return Results.Ok(new { caseId = id, stage, progress });
        });

        api.MapPost("/cases/{id:guid}/origin/departure-confirmations", async (Guid id,
            ConfirmDeparture input, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.origin.departure.confirm") ||
                !await CanModifyOriginAsync(id, actor.Value, http, access, db, ct))
                return Results.Forbid();
            if (input.EventId == Guid.Empty || input.EvidenceStorageFileId == Guid.Empty ||
                input.ActualDepartureAtUtc == default || input.ActualDepartureAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
                return Results.BadRequest(new { code = "DEPARTURE_EVIDENCE_REQUIRED" });
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var lockKey = "origin-departure:" + id.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);
            var operation = await db.Cases.SingleOrDefaultAsync(x => x.Id == id, ct);
            var stage = await db.CaseStages.SingleOrDefaultAsync(x =>
                x.CaseId == id && x.DepartmentCode == "COORDINACION_ORIGEN", ct);
            if (operation is null || stage is null) return Results.NotFound();
            var progress = await db.OriginProgress.SingleOrDefaultAsync(x => x.CaseStageId == stage.Id, ct);
            if (progress?.ActualDepartureAtUtc is not null)
            {
                // Returning the same result for retries avoids duplicate transitions and Outbox.
                if (progress.DepartureEventId == input.EventId)
                    return Results.Ok(new { progress.StatusCode, progress.Version, duplicate = true });
                return Results.Conflict(new { code = "DEPARTURE_ALREADY_CONFIRMED" });
            }
            if ((progress?.Version ?? 0L) != input.ExpectedVersion)
                return Results.Conflict(new { code = "ORIGIN_VERSION_CONFLICT", currentVersion = progress?.Version ?? 0L });
            if (operation.Status is "CANCELLED" or "COMPLETED" || stage.Status is "SKIPPED" or "COMPLETED")
                return Results.Conflict(new { code = "INVALID_ORIGIN_STATE" });
            if (progress is null)
            {
                progress = new OperationsOriginProgress { CaseStageId = stage.Id, Version = 0 };
                db.OriginProgress.Add(progress);
            }
            progress.StatusCode = "IN_TRANSIT";
            progress.ActualDepartureAtUtc = input.ActualDepartureAtUtc.ToUniversalTime();
            progress.DepartureSource = "MANUAL";
            progress.DepartureEventId = input.EventId;
            progress.EvidenceStorageFileId = input.EvidenceStorageFileId;
            progress.UpdatedAtUtc = DateTimeOffset.UtcNow;
            progress.Version++;
            stage.Status = "COMPLETED";
            stage.CompletedAtUtc = DateTimeOffset.UtcNow;
            stage.Version++;
            if (!await db.CaseStages.AnyAsync(x => x.CaseId == id && x.DepartmentCode == "TRANSITO", ct))
                db.CaseStages.Add(new OperationsCaseStage { CaseId = id, DepartmentCode = "TRANSITO", Status = "READY" });
            operation.Status = "ACTIVE";
            operation.Version++;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.origin.departure-confirmed.v1",
                JsonSerializer.Serialize(new { caseId = id, stageId = stage.Id,
                    eventId = input.EventId, version = operation.Version })));
            OperationsAuditWriter.Case(db, id, stage.Id, actor.Value,
                "operations.origin.departure.confirmed", null,
                new { input.EventId, input.ActualDepartureAtUtc, input.EvidenceStorageFileId,
                    progress.Version, stage.Status });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { progress.StatusCode, progress.Version, duplicate = false });
        });

        api.MapGet("/cases/{id:guid}/internal-notes", async (Guid id, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.internal-note.read") ||
                !await CanModifyOriginAsync(id, actor.Value, http, access, db, ct))
                return Results.Forbid();
            return Results.Ok(await db.InternalNotes.AsNoTracking().Where(x => x.CaseId == id)
                .OrderByDescending(x => x.CreatedAtUtc).Take(100)
                .Select(x => new { x.Id, x.Body, x.CreatedBy, x.CreatedAtUtc, x.UpdatedAtUtc, x.Version })
                .ToArrayAsync(ct));
        });

        api.MapPost("/cases/{id:guid}/internal-notes", async (Guid id,
            CreateInternalNote input, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.internal-note.create") ||
                !await CanModifyOriginAsync(id, actor.Value, http, access, db, ct))
                return Results.Forbid();
            if (string.IsNullOrWhiteSpace(input.Body) || input.Body.Length > 10000)
                return Results.BadRequest(new { code = "INVALID_INTERNAL_NOTE" });
            var note = new OperationsInternalNote
            {
                CaseId = id, CreatedBy = actor.Value, Body = input.Body.Trim()
            };
            db.InternalNotes.Add(note);
            // Never send confidential note content through central AuditLogs, SignalR or customer email.
            OperationsAuditWriter.Case(db, id, null, actor.Value,
                "operations.internal-note.created", null, new { note.Id });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/operations/cases/{id}/internal-notes/{note.Id}",
                new { note.Id, note.CreatedAtUtc });
        });
        return app;
    }

    // Requires BOTH scope and relationship to a live Origen assignment or team supervisor.
    private static async Task<bool> CanModifyOriginAsync(Guid caseId, Guid actor,
        HttpContext http, OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct)
    {
        if (!await access.CanReadAsync(caseId, http.User, ct)) return false;
        var stage = await db.CaseStages.AsNoTracking().Where(x =>
            x.CaseId == caseId && x.DepartmentCode == "COORDINACION_ORIGEN")
            .Select(x => new { x.Id, x.TeamId }).SingleOrDefaultAsync(ct);
        if (stage is null) return false;
        if (OperationsCaseAccess.IsSystemAdministrator(http.User)) return true;
        return await db.StageAssignments.AnyAsync(x =>
                x.CaseStageId == stage.Id && x.UserId == actor && x.ReleasedAtUtc == null, ct) ||
            (stage.TeamId.HasValue && await db.TeamMembers.AnyAsync(x =>
                x.TeamId == stage.TeamId.Value && x.UserId == actor && x.IsSupervisor && x.IsAvailable, ct));
    }
}
