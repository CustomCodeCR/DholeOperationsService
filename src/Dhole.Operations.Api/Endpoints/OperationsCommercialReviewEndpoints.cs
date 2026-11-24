using System.Data;
using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>
/// Commercial review belongs to Pricing. Operations stores request state and
/// displays an explicit yellow warning, never changes an accepted QUO price.
/// </summary>
public static class OperationsCommercialReviewEndpoints
{
    public sealed record AddServiceRequest(
        Guid IdempotencyKey, string ServiceCode, string ServiceName, string Reason);

    public static IEndpointRouteBuilder MapOperationsCommercialReviewEndpoints(
        this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization();

        api.MapGet("/cases/{caseId:guid}/services-and-reviews", async (
            Guid caseId, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(caseId, http.User, ct))
                return Results.Forbid();
            var services = await db.CaseServiceLines.AsNoTracking()
                .Where(x => x.CaseId == caseId).OrderBy(x => x.CreatedAtUtc)
                .Select(x => new { x.Id, x.ServiceCode, x.ServiceName, x.Source,
                    x.RequiresPricingReview, x.ReviewStatus })
                .ToArrayAsync(ct);
            var alerts = await db.CommercialAlerts.AsNoTracking()
                .Where(x => x.CaseId == caseId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Select(x => new { x.Id, x.ServiceLineId, x.Code, x.Severity,
                    x.Status, x.CreatedAtUtc, x.ResolvedAtUtc })
                .ToArrayAsync(ct);
            var reviews = await db.PricingReviewRequests.AsNoTracking()
                .Where(x => x.CaseId == caseId)
                .OrderByDescending(x => x.RequestedAtUtc)
                .Select(x => new { x.Id, x.ServiceLineId, x.QuoteId,
                    x.AcceptedOptionId, x.Reason, x.Status, x.RequestedAtUtc,
                    x.ResolvedAtUtc })
                .ToArrayAsync(ct);
            return Results.Ok(new { services, alerts, reviews });
        });

        api.MapPost("/cases/{caseId:guid}/services", async (
            Guid caseId, AddServiceRequest input,
            HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null ||
                !(OperationsCaseAccess.HasScope(http.User, "operations.case.update") ||
                  OperationsCaseAccess.HasScope(http.User, "operations.origin.update") ||
                  OperationsCaseAccess.HasScope(http.User, "operations.aperture.update")) ||
                !await access.CanReadAsync(caseId, http.User, ct))
                return Results.Forbid();
            if (input.IdempotencyKey == Guid.Empty ||
                string.IsNullOrWhiteSpace(input.ServiceCode) ||
                input.ServiceCode.Length > 60 ||
                !input.ServiceCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ||
                string.IsNullOrWhiteSpace(input.ServiceName) ||
                input.ServiceName.Length > 200 ||
                string.IsNullOrWhiteSpace(input.Reason) ||
                input.Reason.Trim().Length is < 10 or > 2000)
                return Results.BadRequest(new { code = "OPERATIONS_ADDED_SERVICE_INVALID" });

            await using var tx = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, ct);
            var operation = await db.Cases.SingleOrDefaultAsync(c =>
                c.Id == caseId, ct);
            if (operation is null) return Results.NotFound();
            if (operation.Status is "CANCELLED" or "COMPLETED")
                return Results.Conflict(new { code = "OPERATIONS_CASE_NOT_EDITABLE" });
            // Serializes all additions for the same case. Event retries are
            // idempotent even when different browser tabs submit together.
            var lockKey = "operations-added-service:" + caseId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);
            var existing = await db.CaseServiceLines.AsNoTracking()
                .Where(x => x.CaseId == caseId &&
                    x.IdempotencyKey == input.IdempotencyKey &&
                    x.Source == "OPERATIONS_ADDED")
                .Select(x => new { x.Id, x.ServiceCode, x.ReviewStatus })
                .SingleOrDefaultAsync(ct);
            if (existing is not null)
            {
                var review = await db.PricingReviewRequests.AsNoTracking()
                    .Where(x => x.ServiceLineId == existing.Id)
                    .Select(x => new { x.Id, x.Status }).SingleAsync(ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new {
                    serviceId = existing.Id, existing.ServiceCode,
                    reviewId = review.Id, review.Status, severity = "YELLOW",
                    duplicate = true
                });
            }

            var service = new OperationsCaseServiceLine
            {
                CaseId = caseId, IdempotencyKey = input.IdempotencyKey,
                ServiceCode = input.ServiceCode.Trim().ToUpperInvariant(),
                ServiceName = input.ServiceName.Trim(), CreatedBy = actor.Value,
                Source = "OPERATIONS_ADDED", RequiresPricingReview = true,
                ReviewStatus = "PENDING_PRICING"
            };
            var alert = new OperationsCommercialAlert
            {
                CaseId = caseId, ServiceLineId = service.Id,
                Code = "UNQUOTED_SERVICE", Severity = "YELLOW",
                Status = "PENDING_PRICING", CreatedBy = actor.Value
            };
            var reviewRequest = new OperationsPricingReviewRequest
            {
                CaseId = caseId, ServiceLineId = service.Id,
                QuoteId = operation.SourceQuoteId,
                AcceptedOptionId = operation.SourceQuoteOptionId,
                Reason = input.Reason.Trim(), RequestedBy = actor.Value
            };
            db.CaseServiceLines.Add(service);
            db.CommercialAlerts.Add(alert);
            db.PricingReviewRequests.Add(reviewRequest);
            operation.Version++;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OperationsAuditWriter.Case(db, caseId, null, actor.Value,
                "operations.pricing-review.requested", null,
                new { serviceId = service.Id, alertId = alert.Id,
                    reviewId = reviewRequest.Id, service.ServiceCode });
            // Separate durable signals: commercial request goes to Pricing's
            // own stream; user-facing realtime includes IDs/versions only.
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.pricing-review.requested.v1",
                JsonSerializer.Serialize(new {
                    caseId, operation.Idtra, operation.CompanyId,
                    reviewRequestId = reviewRequest.Id,
                    serviceId = service.Id, service.ServiceCode,
                    reviewRequest.Reason, requestedBy = actor.Value,
                    quoteId = operation.SourceQuoteId,
                    acceptedOptionId = operation.SourceQuoteOptionId
                })));
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.discrepancy.detected.v1",
                JsonSerializer.Serialize(new {
                    caseId, alertId = alert.Id, reviewRequestId = reviewRequest.Id,
                    severity = "YELLOW", version = operation.Version
                })));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/operations/cases/{caseId}/services-and-reviews",
                new { serviceId = service.Id, reviewId = reviewRequest.Id,
                    alertId = alert.Id, severity = alert.Severity,
                    reviewStatus = reviewRequest.Status, duplicate = false });
        });

        return app;
    }
}
