using System.Data;
using System.Text.Json;
using CustomCodeFramework.Messaging.Inbox;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Cases;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Dhole.Operations.Workers.Services;

/// <summary>
/// Consumes immutable Pricing acceptance snapshots from a dedicated Redis Stream.
/// Inbox insert, business-key uniqueness, IDTRA allocation and case creation commit atomically.
/// Never acknowledges uncommitted work. Never calls Pricing synchronously.
/// </summary>
public sealed class PricingAcceptedQuoteConsumer(
    IServiceScopeFactory scopes,
    IConnectionMultiplexer redis,
    IConfiguration settings,
    ILogger<PricingAcceptedQuoteConsumer> logger) : BackgroundService
{
    private const string EventName = "pricing.quote.accepted.v1";
    private const string InboxConsumer = "operations-pricing-accepted-v1";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stream = settings["Redis:Streams:AcceptedQuoteStream"] ?? "dhole.operations.pricing.accepted";
        var group = settings["Redis:Streams:AcceptedQuoteGroup"] ?? "dhole-operations-pricing-accepted";
        var consumer = settings["Redis:Streams:AcceptedQuoteConsumer"] ?? "operations-pricing-worker";
        var db = redis.GetDatabase();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                try { await db.StreamCreateConsumerGroupAsync(stream, group, "0-0", createStream: true); }
                catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase)) { }
                var entries = await db.StreamReadGroupAsync(stream, group, consumer, "0-0", count: 25);
                if (entries.Length == 0)
                    entries = await db.StreamReadGroupAsync(stream, group, consumer, ">", count: 25);

                foreach (var entry in entries)
                {
                    try
                    {
                        var values = entry.Values.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString(),
                            StringComparer.OrdinalIgnoreCase);
                        if (values.GetValueOrDefault("message_type") != EventName ||
                            values.GetValueOrDefault("header:source_service") != "DholePricingService" ||
                            !Guid.TryParse(values.GetValueOrDefault("header:event_id"), out var eventId))
                        {
                            logger.LogError("Malformed Pricing acceptance at {Stream}/{Id}; kept pending", stream, entry.Id);
                            continue;
                        }

                        var payloadJson = values.GetValueOrDefault("payload_json") ?? "{}";
                        using var document = JsonDocument.Parse(payloadJson);
                        await ProcessAsync(eventId, document.RootElement, payloadJson, stoppingToken);
                        await db.StreamAcknowledgeAsync(stream, group, entry.Id);
                    }
                    catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogError(error, "Pricing acceptance {Id} retained for retry", entry.Id);
                    }
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "Pricing acceptance stream unavailable; will retry");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task ProcessAsync(Guid eventId, JsonElement data, string payload, CancellationToken ct)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            GetInt(data, "schemaVersion") != 1 ||
            !Guid.TryParse(GetString(data, "quoteId"), out var quoteId) || quoteId == Guid.Empty ||
            !Guid.TryParse(GetString(data, "selectedQuoteOptionId"), out var selectedId) ||
            selectedId != quoteId)
            throw new InvalidDataException("Pricing acceptance contract invalid.");

        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // Serialize by the business key, regardless of Redis event ID.
        var lockingKey = "accepted-pricing-quote:" + quoteId.ToString("N");
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockingKey}, 0));", ct);

        var previouslyHandled = await database.InboxMessages.AnyAsync(x =>
            x.ConsumerService == InboxConsumer && x.EventId == eventId, ct);
        var existing = await database.Cases.AnyAsync(x =>
            x.ApertureType == "AUTOMATIC" && x.SourceQuoteId == quoteId, ct);
        if (previouslyHandled || existing)
        {
            if (!previouslyHandled)
                RecordReceipt(database, eventId, payload);
            await database.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return;
        }

        // No tenant can be guessed in a multi-company deployment.
        var tenantIds = await database.Teams.AsNoTracking().Select(x => x.CompanyId)
            .Union(database.Cases.AsNoTracking().Select(x => x.CompanyId))
            .Distinct().Take(2).ToArrayAsync(ct);
        var configured = Guid.TryParse(settings["Operations:CompanyId"], out var configId)
            && configId != Guid.Empty ? configId : Guid.Empty;
        if (configured == Guid.Empty && tenantIds.Length != 1)
            throw new InvalidOperationException("Pricing acceptance is pending: Operations tenant is ambiguous.");
        var companyId = configured != Guid.Empty ? configured : tenantIds[0];

        var actorId = Guid.TryParse(GetString(data, "acceptedBy"), out var actor)
            ? actor : Guid.Empty;
        var accountId = Guid.TryParse(GetString(data, "accountId"), out var account)
            ? account : Guid.Empty;
        var modality = (GetString(data, "modalityCode") ?? "FCL").Trim().ToUpperInvariant();
        if (modality is not ("FCL" or "LCL" or "AIR" or "FTL" or "LTL" or "MULTIMODAL"))
            throw new InvalidDataException("Unknown shipment modality from Pricing.");

        var next = await OperationsIdtraSequence.NextAsync(database, ct);
        var caseId = Guid.NewGuid();
        var openingData = JsonSerializer.Serialize(new
        {
            source = "PRICING_ACCEPTED_QUO",
            snapshotVersion = GetInt(data, "revisionNumber"),
            quoteId,
            selectedQuoteOptionId = selectedId,
            quoteNumber = GetString(data, "quoteNumber"),
            clientName = GetString(data, "clientName"),
            incoterm = GetString(data, "incoterm"),
            modality,
            shipmentType = modality,
            pol = NestedString(data, "pol", "name"),
            poe = NestedString(data, "poe", "name"),
            pod = NestedString(data, "pod", "name"),
            carrier = NestedString(data, "carrier", "name"),
            agent = NestedString(data, "agent", "name"),
            pickupAddress = GetString(data, "pickupAddress"),
            // The actual measured weight is owned by Coordinación Origen.
            quotedWeightKg = GetDecimal(data, "quotedWeightKg"),
            quotedVolumeCbm = GetDecimal(data, "quotedVolumeCbm"),
            packages = GetInt(data, "packages"),
            equipment = Clone(data, "equipment"),
            services = Clone(data, "services"),
            commercialSnapshot = data.Clone()
        });

        var operation = new OperationsCase
        {
            Id = caseId,
            CompanyId = companyId,
            AccountId = accountId,
            ExecutiveUserId = actorId,
            CreatedBy = actorId,
            ApertureType = "AUTOMATIC",
            SourceQuoteId = quoteId,
            SourceQuoteOptionId = selectedId,
            SeriesCode = "00000",
            SequenceNumber = next,
            Idtra = $"IDTRA{next}",
            ModalityCode = modality,
            OpeningDataJson = openingData,
            Status = "DRAFT"
        };
        var stage = new OperationsCaseStage
        {
            CaseId = caseId,
            DepartmentCode = "APERTURAS",
            Status = "PENDING"
        };
        operation.Stages.Add(stage);
        database.Cases.Add(operation);

        var link = new OperationsCaseQuoteLink
        {
            CaseId = caseId,
            QuoteId = quoteId,
            QuoteOptionId = selectedId,
            QuoteNumber = (GetString(data, "quoteNumber") ?? quoteId.ToString("N"))[..Math.Min(60,
                (GetString(data, "quoteNumber") ?? quoteId.ToString("N")).Length)],
            IsPrimary = true,
            Status = "ACCEPTED",
            SnapshotVersion = Math.Max(1, GetInt(data, "revisionNumber")),
            LinkedBy = actorId
        };
        if (data.TryGetProperty("costs", out var costs) && costs.ValueKind == JsonValueKind.Array)
        {
            foreach (var cost in costs.EnumerateArray())
            {
                if (cost.ValueKind != JsonValueKind.Object) continue;
                var description = GetString(cost, "description") ?? "Costo cotizado";
                var currency = (GetString(cost, "currency") ?? "USD").Trim().ToUpperInvariant();
                if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)) currency = "USD";
                link.Costs.Add(new OperationsCaseQuoteCost
                {
                    SourceCostId = Guid.TryParse(GetString(cost, "sourceCostId"), out var costId) ? costId : null,
                    CostCode = (GetString(cost, "code") ?? "OTHER")[..Math.Min(60, (GetString(cost, "code") ?? "OTHER").Length)],
                    Description = description[..Math.Min(500, description.Length)],
                    CurrencyCode = currency,
                    CostAmount = GetDecimal(cost, "cost"),
                    SaleAmount = GetDecimal(cost, "sale")
                });
            }
        }
        database.CaseQuoteLinks.Add(link);
        OperationsAuditWriter.Case(database, caseId, stage.Id, actorId,
            "operations.case.created.from_accepted_quote", null,
            new { quoteId, selectedQuoteOptionId = selectedId, idtra = operation.Idtra,
                snapshotRevision = link.SnapshotVersion, costs = link.Costs.Count });
        database.OutboxMessages.Add(OperationsOutboxFactory.Create(
            "operations.case.created.v1",
            JsonSerializer.Serialize(new { caseId, idtra = operation.Idtra,
                sourceQuoteId = quoteId, selectedQuoteOptionId = selectedId, version = operation.Version })));
        RecordReceipt(database, eventId, payload);
        await database.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Created automatic {Idtra} from accepted Pricing quote {QuoteId}",
            operation.Idtra, quoteId);
    }

    private static void RecordReceipt(OperationsDbContext db, Guid eventId, string payload)
    {
        var receipt = new InboxMessage
        {
            ConsumerService = InboxConsumer,
            SourceService = "DholePricingService",
            EventId = eventId,
            EventType = EventName,
            EventName = EventName,
            Status = InboxMessageStatus.Processed,
            ProcessedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.InboxMessages.Add(receipt);
        db.Entry(receipt).Property("PayloadJson").CurrentValue = payload;
    }

    private static string? GetString(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static int GetInt(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number) ? number : 0;
    private static decimal GetDecimal(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out var number) ? number : 0;
    private static string? NestedString(JsonElement data, string parent, string key) =>
        data.TryGetProperty(parent, out var obj) && obj.ValueKind == JsonValueKind.Object
            ? GetString(obj, key) : null;
    private static JsonElement? Clone(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) ? value.Clone() : null;
}
