using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>
/// A single IDTRA workspace. Every action is authorized in the API, not merely in Vue.
/// Does not overwrite Pricing, CRM, or data from another operations department.
/// </summary>
public static class OperationsCaseWorkspaceEndpoints
{
    public sealed record UpdateCaseRequest(
        long ExpectedVersion,
        string ModalityCode,
        short Priority,
        decimal ComplexityPoints,
        string? OriginCountryCode,
        string? DestinationCountryCode);

    public sealed record UpdateOriginStatusRequest(long ExpectedVersion, string StatusCode);

    private static readonly string[] DepartmentTabs =
    [
        "COORDINACION_ORIGEN", "DOCUMENTACION", "TRANSITO", "PANAMA", "COSTA_RICA", "FINANZAS"
    ];

    public static IEndpointRouteBuilder MapOperationsCaseWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization();

        // The backend is authoritative for the visible access levels. An absent
        // department has not been activated; a SKIPPED department is not applicable.
        api.MapGet("/cases/{id:guid}/tabs", async (
            Guid id, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null) return Results.Forbid();
            var operation = await db.Cases.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.Id, x.Status, x.ApertureType, x.CreatedBy })
                .SingleOrDefaultAsync(ct);
            if (operation is null) return Results.NotFound();
            var stages = await db.CaseStages.AsNoTracking()
                .Where(x => x.CaseId == id)
                .Select(x => new { x.Id, x.DepartmentCode, x.Status, x.TeamId })
                .ToArrayAsync(ct);
            var canEditSummary = await CanEditApertureAsync(id, actor.Value, http, access, db, ct);
            var tabs = new List<object>
            {
                new { code = "RESUMEN", accessLevel = canEditSummary ? "EDITABLE" : "READ_ONLY" },
                new { code = "QUO", accessLevel = "READ_ONLY" }
            };
            foreach (var code in DepartmentTabs)
            {
                var stage = stages.FirstOrDefault(s => s.DepartmentCode == code);
                var level = "LOCKED";
                if (stage is not null)
                {
                    level = stage.Status == "SKIPPED" ? "NOT_APPLICABLE" : "READ_ONLY";
                    if (code == "COORDINACION_ORIGEN" &&
                        stage.Status is "READY" or "IN_PROGRESS" &&
                        OperationsCaseAccess.HasScope(http.User, "operations.origin.update") &&
                        await CanEditStageAsync(stage.Id, stage.TeamId, actor.Value, http, db, ct))
                        level = "EDITABLE";
                }
                tabs.Add(new { code, accessLevel = level });
            }
            return Results.Ok(new { caseId = id, tabs });
        });

        // Existing persisted operations data only: no placeholder costs or dummy notes.
        api.MapGet("/cases/{id:guid}/workspace", async (
            Guid id, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var stages = await db.CaseStages.AsNoTracking()
                .Where(s => s.CaseId == id).Select(s => new
                {
                    s.Id, s.DepartmentCode, s.Status, s.TeamId,
                    TeamName = db.Teams.Where(t => t.Id == s.TeamId)
                        .Select(t => t.Name).FirstOrDefault(),
                    AssignedUserId = db.StageAssignments.Where(a =>
                        a.CaseStageId == s.Id && a.ReleasedAtUtc == null)
                        .Select(a => (Guid?)a.UserId).FirstOrDefault(),
                    s.StartedAtUtc, s.CompletedAtUtc
                }).ToArrayAsync(ct);
            var quotes = await db.CaseQuoteLinks.AsNoTracking()
                .Where(q => q.CaseId == id)
                .OrderByDescending(q => q.IsPrimary)
                .Select(q => new { q.Id, q.QuoteId, q.QuoteOptionId, q.QuoteNumber,
                    q.Status, q.IsPrimary, q.SnapshotVersion, q.LinkedAtUtc })
                .ToArrayAsync(ct);
            var milestones = await db.CaseMilestones.AsNoTracking()
                .Where(m => m.CaseId == id).OrderBy(m => m.PlannedAtUtc)
                .Select(m => new { m.Id, m.CaseStageId, m.MilestoneCode, m.Title,
                    m.Status, m.PlannedAtUtc, m.ActualAtUtc }).ToArrayAsync(ct);
            var tasks = await db.CaseTasks.AsNoTracking().Where(t => t.CaseId == id)
                .OrderBy(t => t.DueAtUtc)
                .Select(t => new { t.Id, t.CaseStageId, t.Title, t.Details,
                    t.Status, t.Priority, t.AssignedUserId, t.DueAtUtc,
                    t.CompletedAtUtc }).ToArrayAsync(ct);
            return Results.Ok(new { caseId = id, stages, quotes, milestones, tasks });
        });

        api.MapGet("/cases/{id:guid}/commercial/costs", async (
            Guid id, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.cost.read") ||
                !await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            // Cost visibility does not imply permission to see customer sale values.
            var canSeeSale = OperationsCaseAccess.HasScope(http.User, "operations.finance.manage");
            var costs = await db.CaseQuoteCosts.AsNoTracking()
                .Where(c => c.QuoteLink.CaseId == id)
                .Select(c => new { c.Id, c.CostCode, c.Description,
                    c.SupplierName, c.CurrencyCode, c.CostAmount,
                    SaleAmount = canSeeSale ? (decimal?)c.SaleAmount : null,
                    c.CapturedAtUtc }).ToArrayAsync(ct);
            return Results.Ok(new { caseId = id, items = costs });
        });

        // Editing is intentionally limited to mutable opening metadata. The accepted
        // commercial snapshot, CRM executive and validated workflow are immutable here.
        api.MapPatch("/cases/{id:guid}", async (
            Guid id, UpdateCaseRequest request, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !await CanEditApertureAsync(
                    id, actor.Value, http, access, db, ct)) return Results.Forbid();

            var modality = request.ModalityCode?.Trim().ToUpperInvariant();
            var origin = NormalizeIso(request.OriginCountryCode);
            var destination = NormalizeIso(request.DestinationCountryCode);
            if (modality is not ("FCL" or "LCL" or "FTL" or "LTL" or "AIR" or "MULTIMODAL") ||
                request.Priority is < 1 or > 5 ||
                request.ComplexityPoints is < 0.5m or > 1000m ||
                !ValidIso(origin) || !ValidIso(destination))
                return Results.BadRequest(new { code = "OPERATIONS_CASE_FIELDS_INVALID" });

            var operation = await db.Cases.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (operation is null) return Results.NotFound();
            if (operation.Version != request.ExpectedVersion)
                return Results.Conflict(new { code = "OPERATIONS_CASE_VERSION_CONFLICT",
                    currentVersion = operation.Version });
            if (operation.Status is not ("DRAFT" or "PENDING_VALIDATION" or "APERTURA_REQUIERE_CAMBIOS"))
                return Results.Conflict(new { code = "OPERATIONS_CASE_ALREADY_VALIDATED" });

            var before = new { operation.ModalityCode, operation.Priority,
                operation.ComplexityPoints, operation.OriginCountryCode,
                operation.DestinationCountryCode, operation.Version };
            operation.ModalityCode = modality;
            operation.Priority = request.Priority;
            operation.ComplexityPoints = request.ComplexityPoints;
            operation.OriginCountryCode = origin;
            operation.DestinationCountryCode = destination;
            operation.Version++;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OperationsAuditWriter.Case(db, id, null, actor.Value, "operations.case.updated",
                before, new { operation.ModalityCode, operation.Priority,
                    operation.ComplexityPoints, operation.OriginCountryCode,
                    operation.DestinationCountryCode, operation.Version });
            db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.case.updated.v1",
                JsonSerializer.Serialize(new { caseId = id, version = operation.Version })));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { code = "OPERATIONS_CASE_VERSION_CONFLICT" });
            }
            return Results.Ok(new { operation.Id, operation.Version,
                operation.UpdatedAtUtc });
        });

        // Manual origin progression, not a claim of confirmed physical departure.
        // IN_TRANSIT is exclusively handled by evidence-backed departure confirmation.
        api.MapPost("/cases/{id:guid}/origin/progress", async (
            Guid id, UpdateOriginStatusRequest input, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null ||
                !OperationsCaseAccess.HasScope(http.User, "operations.origin.update") ||
                !await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var operation = await db.Cases.SingleOrDefaultAsync(x => x.Id == id, ct);
            var stage = await db.CaseStages.SingleOrDefaultAsync(x =>
                x.CaseId == id && x.DepartmentCode == "COORDINACION_ORIGEN", ct);
            if (operation is null || stage is null) return Results.NotFound();
            if (operation.Status is "CANCELLED" or "COMPLETED" ||
                stage.Status is not ("READY" or "IN_PROGRESS") ||
                !await CanEditStageAsync(stage.Id, stage.TeamId, actor.Value, http, db, ct))
                return Results.Forbid();

            var status = input.StatusCode?.Trim().ToUpperInvariant();
            var allowed = operation.ModalityCode switch
            {
                "FCL" or "LCL" => new[] { "ORIGIN_COORDINATION", "BOOKING_READY", "PORT_OF_LOADING" },
                "AIR" => new[] { "ORIGIN_COORDINATION", "ORIGIN_WAREHOUSE", "DEPARTURE_AIRPORT" },
                "FTL" or "MULTIMODAL" => new[] { "ORIGIN_COORDINATION", "PREPARING_DISPATCH" },
                "LTL" => new[] { "ORIGIN_COORDINATION", "ORIGIN_WAREHOUSE", "PREPARING_DISPATCH" },
                _ => new[] { "ORIGIN_COORDINATION" }
            };
            if (status is null || !allowed.Contains(status, StringComparer.Ordinal))
                return Results.BadRequest(new { code = "ORIGIN_STATUS_NOT_APPLICABLE" });
            var progress = await db.OriginProgress.SingleOrDefaultAsync(x => x.CaseStageId == stage.Id, ct);
            if (progress?.ActualDepartureAtUtc is not null)
                return Results.Conflict(new { code = "ORIGIN_ALREADY_IN_TRANSIT" });
            var currentVersion = progress?.Version ?? 0L;
            if (currentVersion != input.ExpectedVersion)
                return Results.Conflict(new { code = "ORIGIN_VERSION_CONFLICT", currentVersion });
            if (progress is null)
            {
                progress = new OperationsOriginProgress { CaseStageId = stage.Id, Version = 0 };
                db.OriginProgress.Add(progress);
            }
            var beforeStatus = progress.StatusCode;
            if (beforeStatus == status)
                return Results.Ok(new { progress.StatusCode, progress.Version });
            progress.StatusCode = status;
            progress.UpdatedAtUtc = DateTimeOffset.UtcNow;
            progress.Version++;
            if (stage.Status == "READY")
            {
                stage.Status = "IN_PROGRESS";
                stage.StartedAtUtc ??= DateTimeOffset.UtcNow;
                stage.Version++;
            }
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            operation.Version++;
            OperationsAuditWriter.Case(db, id, stage.Id, actor.Value,
                "operations.origin.status.changed", new { status = beforeStatus },
                new { status, progress.Version });
            db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.origin.status-changed.v1",
                JsonSerializer.Serialize(new { caseId = id, stageId = stage.Id,
                    statusCode = status, version = operation.Version })));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { code = "ORIGIN_VERSION_CONFLICT" });
            }
            return Results.Ok(new { progress.StatusCode, progress.Version });
        });

        return app;
    }

    private static string? NormalizeIso(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static bool ValidIso(string? code) =>
        code is null || (code.Length == 2 && code.All(c => c is >= 'A' and <= 'Z'));

    private static async Task<bool> CanEditApertureAsync(Guid id, Guid actor,
        HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
        CancellationToken ct)
    {
        if (!OperationsCaseAccess.HasScope(http.User, "operations.case.update") ||
            !await access.CanReadAsync(id, http.User, ct)) return false;
        var operation = await db.Cases.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Status, c.ApertureType, c.CreatedBy })
            .SingleOrDefaultAsync(ct);
        if (operation is null ||
            operation.Status is not ("DRAFT" or "PENDING_VALIDATION" or "APERTURA_REQUIERE_CAMBIOS"))
            return false;
        if (OperationsCaseAccess.IsSystemAdministrator(http.User)) return true;
        if (operation.ApertureType == "MANUAL" && operation.CreatedBy == actor) return true;
        var stage = await db.CaseStages.AsNoTracking()
            .Where(s => s.CaseId == id && s.DepartmentCode == "APERTURAS" &&
                s.Status != "COMPLETED" && s.Status != "SKIPPED")
            .Select(s => new { s.Id, s.TeamId }).SingleOrDefaultAsync(ct);
        return stage is not null && await CanEditStageAsync(
            stage.Id, stage.TeamId, actor, http, db, ct);
    }

    private static async Task<bool> CanEditStageAsync(
        Guid stageId, Guid? teamId, Guid actor, HttpContext http,
        OperationsDbContext db, CancellationToken ct)
    {
        if (OperationsCaseAccess.IsSystemAdministrator(http.User)) return true;
        var assigned = await db.StageAssignments.AnyAsync(a =>
            a.CaseStageId == stageId && a.UserId == actor && a.ReleasedAtUtc == null, ct);
        if (assigned) return true;
        return teamId.HasValue && await db.TeamMembers.AnyAsync(m =>
            m.TeamId == teamId.Value && m.UserId == actor &&
            m.IsSupervisor && m.IsAvailable, ct);
    }
}
