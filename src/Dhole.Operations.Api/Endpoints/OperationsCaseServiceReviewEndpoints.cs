using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>
/// Services are operative state only. A new, non-quoted service starts a yellow
/// commercial review; Pricing owns approval/sale amounts and reconciliation.
/// </summary>
public static class OperationsCaseServiceReviewEndpoints
{
    public sealed record AddServiceRequest(
        Guid ServiceCatalogId, string ServiceCode, string ServiceName, string Reason);

    public static IEndpointRouteBuilder MapOperationsCaseServiceReviewEndpoints(
        this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization();

        api.MapGet("/cases/{id:guid}/services", async (Guid id, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var lines = await db.CaseServiceLines.AsNoTracking()
                .Where(x => x.CaseId == id).OrderBy(x => x.CreatedAtUtc)
                .Select(x => new {
                    x.Id, x.ServiceCatalogId, x.PricingServiceLineId, x.ServiceCode,
                    x.ServiceName, x.Source, x.ReviewStatus, x.ReviewReason,
                    x.CreatedBy, x.CreatedAtUtc, x.ResolvedAtUtc
                }).ToArrayAsync(ct);
            return Results.Ok(new {
                caseId = id, items = lines,
                yellowCount = lines.Count(x =>
                    x.Source == "OPERATIONS_ADDED" &&
                    x.ReviewStatus == "PENDING_PRICING"),
                redCount = 0,
                green = lines.All(x =>
                    x.Source == "QUOTED" || x.ReviewStatus == "APPROVED")
            });
        });

        api.MapPost("/cases/{id:guid}/services", async (
            Guid id, AddServiceRequest request, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || (!OperationsCaseAccess.HasScope(http.User, "operations.case.update") &&
                !OperationsCaseAccess.HasScope(http.User, "operations.origin.update")))
                return Results.Forbid();
            if (!await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            if (request.ServiceCatalogId == Guid.Empty ||
                request.ServiceCode is not { Length: > 0 and <= 80 } ||
                request.ServiceName is not { Length: > 0 and <= 255 } ||
                request.Reason is not { Length: >= 8 and <= 2000 })
                return Results.BadRequest(new { code = "OPERATIONS_SERVICE_REVIEW_INVALID" });
            var operation = await db.Cases.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.Id, x.CreatedBy, x.SourceQuoteId,
                    x.SourceQuoteGroupId, x.ApertureType, x.Status })
                .SingleOrDefaultAsync(ct);
            if (operation is null) return Results.NotFound();
            if (operation.Status is "CANCELLED" or "COMPLETED") return Results.Conflict(
                new { code = "OPERATIONS_CASE_NOT_EDITABLE" });
            var permitted = OperationsCaseAccess.IsSystemAdministrator(http.User) ||
                operation.CreatedBy == actor.Value ||
                await db.CaseStages.AnyAsync(s => s.CaseId == id &&
                    s.TeamId.HasValue && db.TeamMembers.Any(m =>
                        m.TeamId == s.TeamId && m.UserId == actor.Value), ct);
            if (!permitted) return Results.Forbid();

            var quoted = await db.CaseServiceLines.AnyAsync(
                x => x.CaseId == id && x.Source == "QUOTED" &&
                    (x.ServiceCatalogId == request.ServiceCatalogId ||
                     x.ServiceCode == request.ServiceCode), ct);
            if (quoted) return Results.Conflict(
                new { code = "OPERATIONS_SERVICE_ALREADY_QUOTED" });

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var service = new OperationsCaseServiceLine {
                CaseId = id, ServiceCatalogId = request.ServiceCatalogId,
                ServiceCode = request.ServiceCode.Trim().ToUpperInvariant(),
                ServiceName = request.ServiceName.Trim(),
                ReviewReason = request.Reason.Trim(),
                Source = "OPERATIONS_ADDED", ReviewStatus = "PENDING_PRICING",
                CreatedBy = actor.Value
            };
            db.CaseServiceLines.Add(service);
            OperationsAuditWriter.Case(db, id, null, actor.Value,
                "operations.service.unquoted-added", null,
                new { service.Id, service.ServiceCatalogId, service.ServiceCode,
                    service.ServiceName, service.ReviewStatus });
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.pricing.review-requested.v1", JsonSerializer.Serialize(new {
                    schemaVersion = 1,
                    reviewRequestId = service.Id, caseId = id, quoteId = operation.SourceQuoteId,
                    quoteGroupId = operation.SourceQuoteGroupId,
                    serviceCatalogId = request.ServiceCatalogId,
                    serviceCode = service.ServiceCode, serviceName = service.ServiceName,
                    reason = service.ReviewReason, requestedBy = actor.Value,
                    requestedAtUtc = service.CreatedAtUtc,
                })));
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.case.services.changed.v1", JsonSerializer.Serialize(new {
                    caseId = id, serviceLineId = service.Id,
                    severity = "YELLOW", status = "PENDING_PRICING"
                })));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/operations/cases/{id}/services/{service.Id}",
                new { service.Id, service.ReviewStatus, severity = "YELLOW" });
        });

        return app;
    }
}
