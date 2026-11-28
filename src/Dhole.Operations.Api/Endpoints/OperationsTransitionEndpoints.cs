using System.Data;
using System.Text.Json;
using Dhole.Operations.Api.Integrations;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dhole.Operations.Api.Endpoints;

public static class OperationsTransitionEndpoints
{
    public sealed record TransferRequest(Guid RequestId, long ExpectedCaseVersion, long ExpectedStageVersion,
        Guid RuleId, int RuleVersion, string RuleFingerprint);

    public static IEndpointRouteBuilder MapOperationsTransitionEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization();
        api.MapGet("/cases/{id:guid}/origin/transition-checklist", async (
            Guid id, HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
            OperationsTransitionChecklist checklist, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.origin.read") ||
                !await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var company = OperationsCaseAccess.IsSystemAdministrator(http.User) ? null : await access.ResolveCompanyIdAsync(http.User, ct);
            var visible = access.FilterVisibleCases(db.Cases.AsNoTracking().Where(c => c.Id == id),
                http.User, company, department: "COORDINACION_ORIGEN");
            var operation = await visible.SingleOrDefaultAsync(ct);
            if (operation is null) return Results.Forbid();
            var origin = await db.CaseStages.AsNoTracking().SingleOrDefaultAsync(s =>
                s.CaseId == id && s.DepartmentCode == "COORDINACION_ORIGEN", ct);
            if (origin is null) return Results.NotFound();
            var result = await checklist.EvaluateAsync(db, operation, origin, http.Request.Headers.Authorization.ToString(), ct);
            var canExecute = checklist.EnforcementEnabled && result.CanTransfer && result.Mode == "MANUAL" &&
                origin.Status is "READY" or "IN_PROGRESS" &&
                OperationsCaseAccess.HasScope(http.User, "operations.workflow.transfer") &&
                await access.CanEditAssignedStageAsync(origin.Id, http.User, ct);
            http.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(new { caseId = id, caseVersion = operation.Version, stageVersion = origin.Version,
                executionEnabled = checklist.EnforcementEnabled, canExecute, evaluation = result });
        });

        api.MapPost("/cases/{id:guid}/origin/transfers", async (
            Guid id, TransferRequest input, HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
            OperationsTransitionChecklist checklist, CancellationToken ct) =>
        {
            if (!checklist.EnforcementEnabled) return Results.Conflict(new { code = "OPERATIONS_TRANSFER_DISABLED" });
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.workflow.transfer") ||
                !OperationsCaseAccess.HasScope(http.User, "operations.origin.read") ||
                !await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            if (input.RequestId == Guid.Empty || input.RuleId == Guid.Empty || input.RuleVersion < 1 ||
                input.ExpectedCaseVersion < 1 || input.ExpectedStageVersion < 1 ||
                input.RuleFingerprint is not { Length: 64 } || !input.RuleFingerprint.All(Uri.IsHexDigit))
                return Results.BadRequest(new { code = "OPERATIONS_TRANSFER_INVALID" });

            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                // Share the case lock with the existing departure endpoint.
                var requestLock = "origin-transfer-request:" + input.RequestId.ToString("N");
                var caseLock = "origin-departure:" + id.ToString("N");
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({requestLock}, 0));", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({caseLock}, 0));", ct);
                var replay = await db.OriginTransfers.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == input.RequestId, ct);
                if (replay is not null)
                    return replay.Matches(id, actor.Value, input.ExpectedCaseVersion, input.ExpectedStageVersion,
                        input.RuleId, input.RuleVersion, input.RuleFingerprint)
                        ? Results.Ok(new { transferId = replay.Id, caseVersion = replay.ResultCaseVersion, duplicate = true })
                        : Results.Conflict(new { code = "OPERATIONS_TRANSFER_REQUEST_CONFLICT" });

                var operation = await db.Cases.SingleOrDefaultAsync(c => c.Id == id, ct);
                var origin = await db.CaseStages.SingleOrDefaultAsync(s =>
                    s.CaseId == id && s.DepartmentCode == "COORDINACION_ORIGEN", ct);
                if (operation is null || origin is null) return Results.NotFound();
                if (origin.Status is not ("READY" or "IN_PROGRESS") ||
                    operation.Status is "CANCELLED" or "COMPLETED" ||
                    !await access.CanEditAssignedStageAsync(origin.Id, http.User, ct)) return Results.Forbid();
                if (operation.Version != input.ExpectedCaseVersion || origin.Version != input.ExpectedStageVersion)
                    return Results.Conflict(new { code = "OPERATIONS_TRANSFER_VERSION_CONFLICT" });
                if (await db.OriginTransfers.AnyAsync(x => x.OriginStageId == origin.Id, ct))
                    return Results.Conflict(new { code = "OPERATIONS_TRANSFER_ALREADY_COMPLETED" });
                var evaluation = await checklist.EvaluateAsync(db, operation, origin, http.Request.Headers.Authorization.ToString(), ct);
                if (!evaluation.CanTransfer || evaluation.Mode != "MANUAL" ||
                    evaluation.RuleId != input.RuleId || evaluation.RuleVersion != input.RuleVersion ||
                    !string.Equals(evaluation.RuleFingerprint, input.RuleFingerprint, StringComparison.Ordinal) ||
                    evaluation.RuleSnapshotJson is null)
                    return Results.Conflict(new { code = "OPERATIONS_TRANSFER_CHECKLIST_REJECTED", evaluation });

                var transit = await db.CaseStages.SingleOrDefaultAsync(s =>
                    s.CaseId == id && s.DepartmentCode == "TRANSITO", ct);
                if (transit is not null && (transit.Status != "PENDING" || transit.TeamId.HasValue ||
                    await db.StageAssignments.AnyAsync(a => a.CaseStageId == transit.Id && a.ReleasedAtUtc == null, ct)))
                    return Results.Conflict(new { code = "OPERATIONS_TRANSIT_STAGE_ALREADY_ACTIVE" });
                if (transit is null)
                {
                    transit = new OperationsCaseStage { CaseId = id, DepartmentCode = "TRANSITO", Status = "PENDING" };
                    db.CaseStages.Add(transit);
                }
                var now = DateTimeOffset.UtcNow;
                origin.Status = "COMPLETED"; origin.CompletedAtUtc = now; origin.Version++;
                foreach (var assignment in await db.StageAssignments.Where(a =>
                    a.CaseStageId == origin.Id && a.ReleasedAtUtc == null).ToListAsync(ct))
                    assignment.ReleasedAtUtc = now;
                operation.Status = "ACTIVE"; operation.Version++; operation.UpdatedAtUtc = now;
                var receipt = new OperationsOriginTransfer {
                    RequestId = input.RequestId, CaseId = id, OriginStageId = origin.Id, TransitStageId = transit.Id,
                    ActorUserId = actor.Value, ExpectedCaseVersion = input.ExpectedCaseVersion,
                    ExpectedStageVersion = input.ExpectedStageVersion, ResultCaseVersion = operation.Version,
                    RuleId = input.RuleId, RuleVersion = input.RuleVersion, RuleFingerprint = input.RuleFingerprint,
                    RuleSnapshotJson = evaluation.RuleSnapshotJson, ChecklistJson = JsonSerializer.Serialize(evaluation),
                    TransferredAtUtc = now
                };
                db.OriginTransfers.Add(receipt);
                db.CaseTasks.Add(new OperationsCaseTask { CaseId = id, CaseStageId = transit.Id,
                    Title = "Pendiente de asignación a Tránsito", Details = "Resolver mediante el pool autorizado de Tránsito.",
                    CreatedBy = actor.Value, Status = "PENDING", Priority = 1 });
                OperationsAuditWriter.Case(db, id, origin.Id, actor.Value, "operations.origin.transferred", null,
                    new { transferId = receipt.Id, transitStageId = transit.Id, receipt.RuleId, receipt.RuleVersion,
                        receipt.RuleFingerprint, receipt.ResultCaseVersion });
                db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.origin.transferred.v1",
                    JsonSerializer.Serialize(new { caseId = id, stageId = origin.Id, transitStageId = transit.Id,
                        transferId = receipt.Id, version = operation.Version })));
                // Transfer never changes Pricing requests or fabricates actual departure.
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { transferId = receipt.Id, caseVersion = operation.Version, duplicate = false });
            }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "OPERATIONS_TRANSFER_VERSION_CONFLICT" }); }
            catch (DbUpdateException e) when (e.InnerException is PostgresException p && p.SqlState is "23505" or "40001" or "40P01")
            { return Results.Conflict(new { code = "OPERATIONS_TRANSFER_RETRY_REQUIRED" }); }
            catch (PostgresException e) when (e.SqlState is "40001" or "40P01")
            { return Results.Conflict(new { code = "OPERATIONS_TRANSFER_RETRY_REQUIRED" }); }
        });
        return app;
    }
}
