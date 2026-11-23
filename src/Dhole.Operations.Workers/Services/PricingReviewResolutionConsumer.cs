using System.Data;
using System.Text.Json;
using CustomCodeFramework.Messaging.Inbox;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Dhole.Operations.Workers.Services;

/// <summary>Pricing-only decision sync; business-key and event-key dedup in the Operations Inbox.</summary>
public sealed class PricingReviewResolutionConsumer(
    IServiceScopeFactory scopes,
    IConnectionMultiplexer redis,
    ILogger<PricingReviewResolutionConsumer> logger) : BackgroundService
{
    private const string Stream = "dhole.operations.pricing.reviews";
    private const string Group = "dhole-operations-pricing-review-results";
    private const string Consumer = "operations-pricing-review-worker";
    private const string EventType = "pricing.operations.review.resolved.v1";
    private const string InboxName = "operations-pricing-review-resolved-v1";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var db = redis.GetDatabase();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                try { await db.StreamCreateConsumerGroupAsync(Stream, Group, "0-0", createStream: true); }
                catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase)) { }
                var messages = await db.StreamReadGroupAsync(Stream, Group, Consumer, "0-0", count: 20);
                if (messages.Length == 0)
                    messages = await db.StreamReadGroupAsync(Stream, Group, Consumer, ">", count: 20);
                foreach (var message in messages)
                {
                    try
                    {
                        var fields = message.Values.ToDictionary(x => x.Name.ToString(),
                            x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                        if (fields.GetValueOrDefault("message_type") != EventType ||
                            fields.GetValueOrDefault("header:source_service") != "DholePricingService" ||
                            !Guid.TryParse(fields.GetValueOrDefault("header:event_id"), out var eventId) ||
                            !fields.TryGetValue("payload_json", out var json))
                            throw new InvalidDataException("Invalid Pricing review response envelope.");
                        using var document = JsonDocument.Parse(json);
                        await ProcessAsync(eventId, document.RootElement, ct);
                        await db.StreamAcknowledgeAsync(Stream, Group, message.Id);
                    }
                    catch (Exception error) when (!ct.IsCancellationRequested)
                    {
                        logger.LogError(error, "Pricing review resolution {RedisId} held for retry", message.Id);
                    }
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogError(error, "Pricing resolution stream temporarily unavailable");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task ProcessAsync(Guid eventId, JsonElement data, CancellationToken ct)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !Guid.TryParse(Value(data, "reviewRequestId"), out var requestId) ||
            !Guid.TryParse(Value(data, "caseId"), out var caseId) ||
            !Guid.TryParse(Value(data, "quoteId"), out var quoteId) ||
            !Guid.TryParse(Value(data, "resolvedBy"), out var resolvedBy) ||
            data.TryGetProperty("schemaVersion", out var schema) == false ||
            schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1)
            throw new InvalidDataException("Pricing review response contract invalid.");
        var decision = Value(data, "decision");
        var notes = Value(data, "notes");
        if (requestId == Guid.Empty || caseId == Guid.Empty || quoteId == Guid.Empty ||
            decision is not ("APPROVED" or "REJECTED") || string.IsNullOrWhiteSpace(notes) ||
            notes.Length > 2000)
            throw new InvalidDataException("Pricing review decision invalid.");

        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await using var tx = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var lockKey = "operations-pricing-review:" + requestId.ToString("N");
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);
        var handled = await database.InboxMessages.AnyAsync(x =>
            x.ConsumerService == InboxName && x.EventId == eventId, ct);
        if (!handled)
        {
            var service = await database.AdditionalServices.SingleOrDefaultAsync(x =>
                x.Id == requestId && x.CaseId == caseId && x.SourceQuoteId == quoteId, ct);
            if (service is null)
                throw new InvalidOperationException("Original Operations service request not found.");
            if (service.ReviewStatus == "PENDING_PRICING_REVIEW")
            {
                var previous = service.ReviewStatus;
                service.ReviewStatus = decision!;
                service.ReviewedAtUtc =
                    data.TryGetProperty("resolvedAtUtc", out var reviewed) &&
                    reviewed.ValueKind == JsonValueKind.String &&
                    reviewed.TryGetDateTimeOffset(out var at) ? at : DateTimeOffset.UtcNow;
                service.PricingResolutionNotes = notes;
                OperationsAuditWriter.Case(database, caseId, null, resolvedBy,
                    "operations.additional_service.pricing_resolved",
                    new { reviewStatus = previous },
                    new { service.Id, reviewStatus = service.ReviewStatus, notes });
                database.OutboxMessages.Add(OperationsOutboxFactory.Create(
                    "operations.case.additional_service.resolved.v1",
                    JsonSerializer.Serialize(new { caseId, serviceId = requestId,
                        decision, resolvedBy, service.ReviewedAtUtc })));
            }
            else if (service.ReviewStatus != decision)
                throw new InvalidOperationException("Conflicting Pricing review result.");
            database.InboxMessages.Add(new InboxMessage
            {
                ConsumerService = InboxName,
                SourceService = "DholePricingService",
                EventId = eventId, EventName = EventType, EventType = EventType,
                Status = InboxMessageStatus.Processed,
                CreatedAtUtc = DateTime.UtcNow,
                ProcessedAtUtc = DateTime.UtcNow
            });
        }
        await database.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static string? Value(JsonElement data, string key) =>
        data.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() : null;
}
