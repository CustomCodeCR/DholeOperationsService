using System.Data;
using System.Text.Json;
using CustomCodeFramework.Messaging.Inbox;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Persistence.Cases;

public sealed record PricingReviewResolutionEvent(
    int SchemaVersion, Guid ReviewRequestId, Guid CaseId,
    Guid? QuoteId, string Decision, string Reason,
    Guid ResolvedBy, DateTimeOffset ResolvedAtUtc);

/// <summary>
/// Pricing decides, Operations reconciles the yellow alert via a transactional
/// Inbox. A duplicate delivery cannot overwrite a later business decision.
/// </summary>
public sealed class PricingReviewResolutionMaterializer(OperationsDbContext db)
{
    public const string ConsumerService = "operations-pricing-review-resolution";
    public async Task<string> ApplyAsync(Guid eventId, string payloadJson, CancellationToken ct)
    {
        if (eventId == Guid.Empty) throw new InvalidOperationException("Review event ID required.");
        var item = JsonSerializer.Deserialize<PricingReviewResolutionEvent>(
            payloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new JsonException("Missing Pricing review resolution.");
        if (item.SchemaVersion != 1 || item.ReviewRequestId == Guid.Empty ||
            item.CaseId == Guid.Empty || item.ResolvedBy == Guid.Empty ||
            item.Decision is not ("APPROVED" or "REJECTED" or "NEEDS_INFORMATION") ||
            string.IsNullOrWhiteSpace(item.Reason))
            throw new InvalidOperationException("Unrecognized Pricing commercial review decision.");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({item.ReviewRequestId.ToString("N")}, 0));", ct);
        if (await db.InboxMessages.AnyAsync(x =>
            x.ConsumerService == ConsumerService && x.EventId == eventId, ct))
        {
            await tx.CommitAsync(ct);
            return "ALREADY_PROCESSED";
        }

        var service = await db.CaseServiceLines.SingleOrDefaultAsync(x =>
            x.Id == item.ReviewRequestId && x.CaseId == item.CaseId &&
            x.Source == "OPERATIONS_ADDED", ct);
        if (service is null)
            throw new InvalidOperationException("Pricing resolution references an unknown Operations service.");
        if (service.ReviewStatus == "PENDING_PRICING")
        {
            var before = service.ReviewStatus;
            service.ReviewStatus = item.Decision;
            service.ReviewReason = item.Reason;
            service.ResolvedBy = item.ResolvedBy;
            service.ResolvedAtUtc = item.ResolvedAtUtc;
            service.Version++;
            OperationsAuditWriter.Case(db, item.CaseId, null, item.ResolvedBy,
                "operations.service.pricing-reviewed",
                new { reviewRequestId = service.Id, status = before },
                new { service.ReviewStatus, service.ReviewReason, item.QuoteId });
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.case.services.changed.v1", JsonSerializer.Serialize(new {
                    caseId = item.CaseId, serviceLineId = service.Id,
                    reviewStatus = service.ReviewStatus,
                    severity = service.ReviewStatus == "REJECTED" ? "RED" :
                        service.ReviewStatus == "APPROVED" ? "GREEN" : "YELLOW"
                })));
        }

        var receipt = new InboxMessage {
            ConsumerService = ConsumerService, SourceService = "DholePricingService",
            EventId = eventId, EventType = "pricing.operations.service-review-resolved.v1",
            EventName = "pricing.operations.service-review-resolved.v1",
            Status = InboxMessageStatus.Processed, CreatedAtUtc = DateTime.UtcNow,
            ProcessedAtUtc = DateTime.UtcNow
        };
        db.InboxMessages.Add(receipt);
        db.Entry(receipt).Property("PayloadJson").CurrentValue = payloadJson;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return service.ReviewStatus;
    }
}
