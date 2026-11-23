using System.Data;
using System.Text.Json;
using CustomCodeFramework.Messaging.Inbox;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using StackExchange.Redis;

namespace Dhole.Operations.Workers.Services;

/// <summary>
/// Pricing -> Operations: durable Redis Stream + transactional PostgreSQL Inbox.
/// Group quote number is the business idempotency identity (one IDTRA per A/B group).
/// </summary>
public sealed class AcceptedQuoteOpeningConsumer(
    IServiceScopeFactory scopes, IConnectionMultiplexer redis, IConfiguration config,
    ILogger<AcceptedQuoteOpeningConsumer> logger) : BackgroundService
{
    public const string EventName = "pricing.quote.accepted.for-opening.v1";
    private const string Receiver = "operations-pricing-openings-v1";
    private const string DefaultStream = "dhole.operations.openings.v1";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stream = config["Redis:Streams:OpeningsStreamName"] ?? DefaultStream;
        var group = config["Redis:Streams:OpeningsConsumerGroup"] ?? Receiver;
        var consumer = config["Redis:Streams:OpeningsConsumerName"] ?? "operations-openings-worker";
        var database = redis.GetDatabase();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                try { await database.StreamCreateConsumerGroupAsync(stream, group, "0-0", createStream: true); }
                catch (RedisServerException e) when (e.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase)) { }

                var batch = await database.StreamReadGroupAsync(stream, group, consumer, "0-0", count: 25);
                if (batch.Length == 0)
                    batch = await database.StreamReadGroupAsync(stream, group, consumer, ">", count: 25);
                foreach (var entry in batch)
                {
                    try
                    {
                        var values = entry.Values.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString(),
                            StringComparer.OrdinalIgnoreCase);
                        if (!values.TryGetValue("message_type", out var type) || type != EventName)
                        {
                            // This stream is exclusively for the accepted quote contract.
                            logger.LogError("Unexpected event type {Type} in opening stream at {EntryId}", type, entry.Id);
                            continue;
                        }

                        var id = values.GetValueOrDefault("header:event_id") ??
                            values.GetValueOrDefault("header:message_id");
                        if (!Guid.TryParse(id, out var eventId))
                        {
                            logger.LogError("Missing event ID at {EntryId}; retaining pending for inspection", entry.Id);
                            continue;
                        }

                        var payload = values.GetValueOrDefault("payload_json") ?? "{}";
                        using var document = JsonDocument.Parse(payload);
                        var root = document.RootElement;
                        if (root.ValueKind != JsonValueKind.Object ||
                            !TryGuid(root, "quoteId", out var quoteId) ||
                            !TryString(root, "groupQuoteNumber", out var number) ||
                            !TryString(root, "quoteNumber", out var selectedNumber))
                        {
                            logger.LogError("Malformed accepted QUO event {EventId}; retaining pending", eventId);
                            continue;
                        }
                        var consumed = await ApplyAsync(eventId, quoteId, number, selectedNumber, root, payload, stoppingToken);
                        if (consumed)
                            await database.StreamAcknowledgeAsync(stream, group, entry.Id);
                    }
                    catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogError(e, "Accepted quote opening delivery failed at {EntryId}; retaining pending", entry.Id);
                        // A failed entry remains in this consumer's pending list for retry.
                    }
                }
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Pricing accepted QUO consumer cycle failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task<bool> ApplyAsync(
        Guid eventId, Guid quoteId, string groupNumber, string selectedNumber,
        JsonElement snapshot, string payload, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // Serialize all accepted alternatives of the same group, including new EventIds.
        var lockKey = "operations:quote-group:" + groupNumber;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);

        if (await db.InboxMessages.AnyAsync(x =>
            x.ConsumerService == Receiver && x.EventId == eventId, ct))
            return true;

        // A/B alternatives are mutually exclusive; never create another IDTRA.
        var existing = await db.CaseQuoteLinks.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsPrimary && x.QuoteNumber == groupNumber, ct);
        if (existing is null)
        {
            if (!TryGuid(snapshot, "accountId", out var accountId))
            {
                logger.LogWarning("Accepted QUO {QuoteId} does not have a CRM AccountId. Retaining event for retry.", quoteId);
                return false;
            }

            // Auth currently issues tokens without a tenant claim. Resolve a tenant only
            // if the database has ONE known company. Never guess in multi-tenant contexts.
            var knownCompanies = await db.Teams.AsNoTracking().Select(x => x.CompanyId)
                .Union(db.Cases.AsNoTracking().Select(x => x.CompanyId))
                .Distinct().Take(2).ToArrayAsync(ct);
            if (knownCompanies.Length != 1)
            {
                logger.LogWarning("Cannot resolve unique Operations company for accepted QUO {QuoteId}: {Count} known tenants.",
                    quoteId, knownCompanies.Length);
                return false;
            }
            var actorId = TryGuid(snapshot, "acceptedBy", out var acceptedBy)
                ? acceptedBy : Guid.Empty;
            var nextIdtra = await NextIdtraAsync(db, ct);
            var caseId = Guid.NewGuid();
            var operation = new OperationsCase
            {
                Id = caseId, CompanyId = knownCompanies[0],
                Idtra = "IDTRA" + nextIdtra, SequenceNumber = nextIdtra, SeriesCode = "00000",
                AccountId = accountId, ExecutiveUserId = actorId, CreatedBy = actorId,
                SourceQuoteId = quoteId,
                SourceQuoteOptionId = TryGuid(snapshot, "quoteOptionId", out var optionId) ? optionId : null,
                ApertureType = "AUTOMATIC",
                ModalityCode = String(snapshot, "modalityCode", "MARITIMO"),
                OriginCountryCode = null, DestinationCountryCode = null,
                OpeningDataJson = JsonSerializer.Serialize(new
                {
                    source = "PRICING_ACCEPTED_QUO",
                    acceptedQuoteNumber = selectedNumber,
                    groupQuoteNumber = groupNumber,
                    incoterm = String(snapshot, "incoterm"),
                    shipmentMode = String(snapshot, "shipmentMode"),
                    operationType = String(snapshot, "operationType"),
                    commercialSnapshot = snapshot.Clone()
                }),
                Status = "PENDING_VALIDATION"
            };
            var stage = new OperationsCaseStage
            {
                CaseId = caseId, DepartmentCode = "APERTURAS", Status = "PENDING"
            };
            operation.Stages.Add(stage);
            db.Cases.Add(operation);

            var link = new OperationsCaseQuoteLink
            {
                CaseId = caseId, QuoteId = quoteId,
                QuoteOptionId = operation.SourceQuoteOptionId,
                QuoteNumber = groupNumber, IsPrimary = true,
                Status = "ACCEPTED_SNAPSHOT", SnapshotVersion = 1,
                LinkedBy = actorId
            };
            db.CaseQuoteLinks.Add(link);
            if (snapshot.TryGetProperty("costs", out var costs) &&
                costs.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in costs.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var quantity = Decimal(item, "Quantity", 1m);
                    var cost = new OperationsCaseQuoteCost
                    {
                        QuoteLinkId = link.Id,
                        SourceCostId = TryGuid(item, "sourceCostId", out var costId) ? costId : null,
                        CostCode = String(item, "costCode", "OTHER")[..Math.Min(60, String(item, "costCode", "OTHER").Length)],
                        Description = String(item, "description", "Servicio")[..Math.Min(500, String(item, "description", "Servicio").Length)],
                        CurrencyCode = NormalizeCurrency(String(item, "currencyCode", "USD")),
                        CostAmount = Decimal(item, "CostAmount", 0m) * quantity,
                        SaleAmount = Decimal(item, "SaleAmount", 0m) * quantity,
                    };
                    db.CaseQuoteCosts.Add(cost);
                }
            }
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.case.created.v1",
                JsonSerializer.Serialize(new { caseId, operation.Idtra, quoteId, groupQuoteNumber = groupNumber,
                    apertureType = "AUTOMATIC", version = operation.Version })));
            logger.LogInformation("Opening {Idtra} created from accepted QUO {QuoteNumber} / {QuoteId}",
                operation.Idtra, selectedNumber, quoteId);
        }

        var receipt = new InboxMessage
        {
            ConsumerService = Receiver, SourceService = "DholePricingService",
            EventId = eventId, EventType = EventName, EventName = EventName,
            Status = InboxMessageStatus.Processed, CreatedAtUtc = DateTime.UtcNow,
            ProcessedAtUtc = DateTime.UtcNow
        };
        db.InboxMessages.Add(receipt);
        db.Entry(receipt).Property("PayloadJson").CurrentValue = payload;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static async Task<long> NextIdtraAsync(OperationsDbContext db, CancellationToken ct)
    {
        var conn = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
        await using var cmd = new Npgsql.NpgsqlCommand("""
            INSERT INTO operations.operations_case_sequences (id, series_code, last_number, updated_at_utc)
            VALUES (@id, '00000', 1, now())
            ON CONFLICT (series_code)
            DO UPDATE SET last_number = operations_case_sequences.last_number + 1, updated_at_utc = now()
            RETURNING last_number
            """, conn, (Npgsql.NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static bool TryGuid(JsonElement root, string key, out Guid result)
    {
        result = Guid.Empty;
        return root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
            Guid.TryParse(value.GetString(), out result) && result != Guid.Empty;
    }

    private static bool TryString(JsonElement root, string key, out string result)
    {
        result = String(root, key);
        return !string.IsNullOrWhiteSpace(result);
    }

    private static string String(JsonElement root, string key, string fallback = "")
        => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? fallback : fallback;

    private static decimal Decimal(JsonElement root, string key, decimal fallback)
        => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out var amount) ? amount : fallback;

    private static string NormalizeCurrency(string currency)
        => currency.Length == 3 && currency.All(c => c is >= 'A' and <= 'Z') ? currency : "USD";
}
