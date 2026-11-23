using System.Net;
using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>Aperturas requests a documented Pricing review without altering accepted costs.</summary>
public static class OperationsPricingReviewEndpoints
{
    public sealed record RequestReview(Guid RequestId, string Reason);

    public static IEndpointRouteBuilder MapOperationsPricingReviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/operations/cases/{id:guid}/pricing/review-requests", async (
            Guid id, RequestReview request, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.update") ||
                !await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var reason = request.Reason?.Trim();
            if (request.RequestId == Guid.Empty || reason is null || reason.Length is < 10 or > 1000)
                return Results.BadRequest(new { code = "OPERATIONS_PRICING_REVIEW_REASON_REQUIRED" });

            var operation = await db.Cases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (operation is null) return Results.NotFound();
            if (operation.Status is "CANCELLED" or "COMPLETED")
                return Results.Conflict(new { code = "OPERATIONS_CASE_CLOSED" });
            var quote = await db.CaseQuoteLinks.AsNoTracking()
                .FirstOrDefaultAsync(x => x.CaseId == id && x.IsPrimary && x.Status == "ACCEPTED", ct);
            if (quote is null) return Results.Conflict(new { code = "OPERATIONS_REVIEW_REQUIRES_ACCEPTED_QUO" });

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var lockKey = "operations-pricing-review:" + request.RequestId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);
            if (await db.CaseTasks.AnyAsync(x => x.Id == request.RequestId, ct))
                return Results.Ok(new { taskId = request.RequestId, duplicate = true });

            var task = new OperationsCaseTask
            {
                Id = request.RequestId,
                CaseId = id,
                Title = "Revisión solicitada a Pricing",
                Details = reason,
                Status = "AWAITING_PRICING",
                CreatedBy = actor.Value,
                Priority = 2
            };
            db.CaseTasks.Add(task);
            OperationsAuditWriter.Case(db, id, null, actor.Value,
                "operations.pricing.review.requested", null,
                new { taskId = task.Id, quoteId = quote.QuoteId, reason });
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.pricing.review-requested.v1",
                JsonSerializer.Serialize(new
                {
                    requestId = task.Id, caseId = id, operation.Idtra,
                    quoteId = quote.QuoteId, quoteNumber = quote.QuoteNumber,
                    reason, actorId = actor.Value
                })));

            // Send to the existing Pricing shared inbox. Delivery is retried by
            // the Operations Outbox publisher; the accepted financial snapshot stays immutable.
            var title = $"Solicitud de revisión Pricing - {operation.Idtra} - {quote.QuoteNumber}";
            var body = $"<p>Operaciones solicita revisar una cotización aceptada.</p>" +
                $"<p><strong>Trámite:</strong> {WebUtility.HtmlEncode(operation.Idtra)}</p>" +
                $"<p><strong>QUO:</strong> {WebUtility.HtmlEncode(quote.QuoteNumber ?? "")}</p>" +
                $"<p><strong>Motivo:</strong> {WebUtility.HtmlEncode(reason)}</p>";
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "notifications.notification.requested",
                JsonSerializer.Serialize(new
                {
                    notificationType = "operations.pricing.review-requested",
                    channel = "Email",
                    entityType = "OperationsCase",
                    entityId = operation.Id.ToString("D"),
                    subject = title,
                    body,
                    payload = new { caseId = id, quoteId = quote.QuoteId, requestId = task.Id },
                    maxAttempts = 5,
                    idempotencyKey = "operations-pricing-review:" + task.Id.ToString("N"),
                    recipients = new[] { new { userId = (string?)null,
                        address = "pricingcf@castrofallas.com",
                        displayName = "Pricing" } }
                })));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Accepted($"/api/operations/cases/{id}/workspace",
                new { taskId = task.Id, requested = true });
        }).RequireAuthorization();
        return app;
    }
}
