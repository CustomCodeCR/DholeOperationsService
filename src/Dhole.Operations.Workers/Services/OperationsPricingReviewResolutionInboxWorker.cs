using System.Data;
using System.Text.Json;
using CustomCodeFramework.Messaging.Inbox;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Dhole.Operations.Workers.Services;

/// <summary>
/// Pricing owns the commercial decision; Operations safely reconciles a
/// signed-off review status without changing the original quote, cost or sale.
/// A separate consumer group prevents a failure from blocking Operations'
/// own realtime events.
/// </summary>
public sealed class OperationsPricingReviewResolutionInboxWorker(
    IServiceScopeFactory scopeFactory,
    IConnectionMultiplexer redis, IConfiguration config,
    ILogger<OperationsPricingReviewResolutionInboxWorker> logger) : BackgroundService
{
    private const string EventType = "pricing.operations-review.resolved.v1";
    private sealed record Decision(
        Guid PricingReviewId, Guid OperationsRequestId, Guid CaseId, Guid CompanyId,
        Guid ServiceId, Guid? QuoteId, Guid? AcceptedOptionId,
        string Status, Guid? DecidedBy, DateTimeOffset? DecidedAtUtc,
        string? DecisionReason, long Version);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stream = config["Redis:Streams:Name"] ?? "dhole.operations.events";
        var group = "dhole-operations-pricing-review-resolutions";
        var consumer = config["Redis:PricingReview:ConsumerName"] ??
            "operations-pricing-review-worker";
        var dbRedis = redis.GetDatabase();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                try
                {
                    await dbRedis.StreamCreateConsumerGroupAsync(
                        stream, group, "0-0", createStream: true);
                }
                catch (RedisServerException error) when (
                    error.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase)) { }

                var pending = await dbRedis.StreamReadGroupAsync(
                    stream, group, consumer, "0-0", count:25);
                var fresh = await dbRedis.StreamReadGroupAsync(
                    stream, group, consumer, ">", count:25);
                foreach (var entry in pending.Concat(fresh).DistinctBy(x => x.Id))
                {
                    if (Field(entry, "message_type") != EventType)
                    {
                        await dbRedis.StreamAcknowledgeAsync(stream, group, entry.Id);
                        continue;
                    }
                    var eventIdText = Field(entry, "header:event_id");
                    if (string.IsNullOrWhiteSpace(eventIdText))
                        eventIdText = Field(entry, "header:message_id");
                    if (!Guid.TryParse(eventIdText, out var eventId) ||
                        eventId == Guid.Empty ||
                        Field(entry, "header:source_service") != "DholePricingService")
                    {
                        logger.LogError("Review resolution event at {EntryId} has invalid identity", entry.Id);
                        continue;
                    }
                    try
                    {
                        await ApplyAsync(eventId, Field(entry, "payload_json"), stoppingToken);
                        await dbRedis.StreamAcknowledgeAsync(stream, group, entry.Id);
                    }
                    catch (OperationCanceledException) when(stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        logger.LogError(error,
                            "Unapplied Pricing review result {EventId}; pending for replay",
                            eventId);
                    }
                }
            }
            catch (OperationCanceledException) when(stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Operations Pricing review recovery cycle failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private static string Field(StreamEntry entry, string key) =>
        entry.Values.FirstOrDefault(x => x.Name == key).Value.ToString() ?? "";

    private async Task ApplyAsync(Guid eventId, string payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > 32768)
            throw new InvalidOperationException("Invalid Pricing review resolution payload.");
        using var doc = JsonDocument.Parse(payload);
        var decision = doc.RootElement.Deserialize<Decision>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web) {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException("Empty Pricing review decision.");
        if (decision.PricingReviewId == Guid.Empty ||
            decision.OperationsRequestId == Guid.Empty ||
            decision.CaseId == Guid.Empty || decision.CompanyId == Guid.Empty ||
            decision.ServiceId == Guid.Empty || decision.DecidedBy is null ||
            decision.DecidedBy == Guid.Empty || decision.Version < 2 ||
            decision.Status is not (
                "NO_PRICE_CHANGE_REQUIRED" or "REVISION_REQUIRED" or "REJECTED"))
            throw new InvalidOperationException("Invalid Pricing decision or reviewer.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        var key = "operations-pricing-review:" + decision.OperationsRequestId.ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));", ct);
        if (await db.InboxMessages.AnyAsync(x =>
            x.ConsumerService == "operations-pricing-review" &&
            x.EventId == eventId, ct))
        {
            await tx.CommitAsync(ct);
            return;
        }

        var request = await db.PricingReviewRequests.SingleOrDefaultAsync(x =>
            x.Id == decision.OperationsRequestId &&
            x.CaseId == decision.CaseId &&
            x.ServiceLineId == decision.ServiceId, ct);
        var operation = await db.Cases.SingleOrDefaultAsync(x =>
            x.Id == decision.CaseId && x.CompanyId == decision.CompanyId, ct);
        if (operation is null) throw new InvalidOperationException("Pricing resolution cannot match Operations case.");
        if (request is null)
        {
            var discrepancy = await db.QuoteDiscrepancyReviews.SingleOrDefaultAsync(x =>
                x.Id == decision.OperationsRequestId && x.Id == decision.ServiceId &&
                x.CaseId == decision.CaseId, ct);
            if (discrepancy is null || discrepancy.QuoteId != decision.QuoteId ||
                discrepancy.AcceptedOptionId != decision.AcceptedOptionId)
                throw new InvalidOperationException("Pricing resolution cannot match a persisted discrepancy.");
            if (decision.Version > discrepancy.LatestPricingVersion)
            {
                discrepancy.LatestPricingVersion = decision.Version;
                discrepancy.PricingRequestId = decision.PricingReviewId;
                discrepancy.Status = decision.Status == "NO_PRICE_CHANGE_REQUIRED" ? "RESOLVED_NO_PRICE_CHANGE" :
                    decision.Status == "REVISION_REQUIRED" ? "REVIEWING" : "REJECTED";
                discrepancy.ResolvedAtUtc = decision.Status == "REVISION_REQUIRED" ? null : decision.DecidedAtUtc;
                discrepancy.DecisionReason = decision.DecisionReason;
                operation.Version++;
                operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
                OperationsAuditWriter.Case(db, operation.Id, null, decision.DecidedBy.Value,
                    "operations.pricing-review.resolved", null, new { reviewId = discrepancy.Id, decision.Status, decision.Version });
                db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.pricing-review.resolved.v1",
                    JsonSerializer.Serialize(new { caseId = operation.Id, reviewId = discrepancy.Id,
                        discrepancy.Status, version = operation.Version })));
            }
        }
        else if (request.QuoteId != decision.QuoteId || request.AcceptedOptionId != decision.AcceptedOptionId)
            throw new InvalidOperationException("Pricing resolution cannot match a persisted service review.");
        else if (decision.Version > request.LatestPricingVersion)
        {
            var service = await db.CaseServiceLines.SingleAsync(x =>
                x.Id == decision.ServiceId && x.CaseId == decision.CaseId, ct);
            var alert = await db.CommercialAlerts.SingleAsync(x =>
                x.ServiceLineId == service.Id, ct);
            request.LatestPricingVersion = decision.Version;
            request.PricingRequestId = decision.PricingReviewId;
            request.Status = decision.Status == "NO_PRICE_CHANGE_REQUIRED"
                ? "RESOLVED_NO_PRICE_CHANGE"
                : decision.Status == "REVISION_REQUIRED" ? "REVIEWING" : "REJECTED";
            request.ResolvedAtUtc = decision.Status == "REVISION_REQUIRED"
                ? null : decision.DecidedAtUtc;
            service.ReviewStatus = request.Status;
            alert.Status = decision.Status == "NO_PRICE_CHANGE_REQUIRED"
                ? "RESOLVED" : decision.Status == "REVISION_REQUIRED"
                    ? "PENDING_PRICING" : "PENDING_ACTION";
            if (decision.Status == "NO_PRICE_CHANGE_REQUIRED")
            {
                alert.Severity = "GREEN";
                alert.ResolvedAtUtc = decision.DecidedAtUtc;
            }
            operation.Version++;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OperationsAuditWriter.Case(db, operation.Id, null, decision.DecidedBy.Value,
                "operations.pricing-review.resolved", null,
                new { reviewId = request.Id, decision.Status,
                    decision.Version, decision.DecidedAtUtc });
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.pricing-review.resolved.v1",
                JsonSerializer.Serialize(new { caseId = operation.Id,
                    reviewId = request.Id, request.Status, alertId = alert.Id,
                    alert.Severity, version = operation.Version })));
        }

        var receipt = new InboxMessage
        {
            EventId = eventId, EventType = EventType, EventName = EventType,
            ConsumerService = "operations-pricing-review",
            SourceService = "DholePricingService", Status = InboxMessageStatus.Processed,
            CreatedAtUtc = DateTime.UtcNow, ProcessedAtUtc = DateTime.UtcNow
        };
        db.InboxMessages.Add(receipt);
        db.Entry(receipt).Property("PayloadJson").CurrentValue = "{}";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
