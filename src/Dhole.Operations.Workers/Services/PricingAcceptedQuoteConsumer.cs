using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using CustomCodeFramework.Messaging.Inbox;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using StackExchange.Redis;

namespace Dhole.Operations.Workers.Services;

/// <summary>
/// Accepted QUO -> exactly one automatic opening. The Outbox is owned by Pricing,
/// while the complete case, immutable snapshot and Inbox are committed by Operations.
/// The stream acknowledgement happens only AFTER the PostgreSQL commit.
/// </summary>
public sealed class PricingAcceptedQuoteConsumer(
    IServiceScopeFactory scopeFactory,
    IConnectionMultiplexer redis,
    IConfiguration config,
    ILogger<PricingAcceptedQuoteConsumer> logger) : BackgroundService
{
    private const string StreamName = "dhole.operations.pricing-accepted.v1";
    private const string GroupName = "dhole-operations-accepted-v1";
    private const string ConsumerName = "operations-accepted-worker";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var database = redis.GetDatabase();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                try { await database.StreamCreateConsumerGroupAsync(StreamName, GroupName, "0-0", createStream: true); }
                catch (RedisServerException e) when (e.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase)) { }

                // A stable name replays the worker's own unacknowledged entries after restart.
                var entries = await database.StreamReadGroupAsync(StreamName, GroupName, ConsumerName, "0-0", count: 25);
                if (entries.Length == 0)
                    entries = await database.StreamReadGroupAsync(StreamName, GroupName, ConsumerName, ">", count: 25);

                foreach (var entry in entries)
                {
                    var fields = entry.Values.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString(),
                        StringComparer.OrdinalIgnoreCase);
                    var type = Field(fields, "message_type", "type");
                    if (type != "pricing.quote.accepted.v1")
                    {
                        logger.LogWarning("Unknown Pricing event {EventType} at {RedisId}; not acknowledged", type, entry.Id);
                        continue;
                    }
                    var json = Field(fields, "payload_json", "data");
                    if (string.IsNullOrWhiteSpace(json))
                        throw new InvalidOperationException("Accepted quote event has no JSON payload");
                    var eventIdText = Field(fields, "header:event_id", "header:message_id", "id");
                    if (!Guid.TryParse(eventIdText, out var eventId))
                        throw new InvalidOperationException("Accepted quote event has no stable UUID");
                    var payload = JsonSerializer.Deserialize<AcceptedQuote>(json, JsonOptions)
                        ?? throw new InvalidOperationException("Accepted quote cannot be deserialized");
                    await PersistAsync(eventId, payload, json, stoppingToken);
                    await database.StreamAcknowledgeAsync(StreamName, GroupName, entry.Id);
                }
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Accepted QUO processing failed; event remains pending for retry");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task PersistAsync(Guid eventId, AcceptedQuote accepted, string json, CancellationToken ct)
    {
        if (accepted.QuoteId == Guid.Empty || accepted.SelectedQuoteOptionId != accepted.QuoteId ||
            accepted.AccountId is null or { } && accepted.AccountId == Guid.Empty ||
            accepted.ExecutiveUserId is null or { } && accepted.ExecutiveUserId == Guid.Empty ||
            accepted.AcceptedBy is null or { } && accepted.AcceptedBy == Guid.Empty ||
            string.IsNullOrWhiteSpace(accepted.QuoteNumber) ||
            string.IsNullOrWhiteSpace(accepted.ModalityCode))
            throw new InvalidOperationException("Accepted QUO has incomplete CRM identity or selection; keep event pending");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // A lock over the GROUP key prevents two A/B alternatives or replayed messages
        // from obtaining separate IDTRA numbers concurrently.
        var baseQuote = BaseQuote(accepted.QuoteNumber);
        var lockKey = "operations-accepted-quo:" + baseQuote;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);

        var received = await db.InboxMessages.AnyAsync(x =>
            x.ConsumerService == "operations-pricing-accepted" && x.EventId == eventId, ct);
        if (received)
        {
            await transaction.CommitAsync(ct);
            return;
        }

        var alreadyCreated = await db.CaseQuoteLinks.AsNoTracking()
            .Where(x => x.IsPrimary && x.QuoteNumber == baseQuote && x.Status == "ACCEPTED")
            .Select(x => new { x.CaseId, x.QuoteId })
            .FirstOrDefaultAsync(ct);
        if (alreadyCreated is not null && alreadyCreated.QuoteId != accepted.QuoteId)
            throw new InvalidOperationException("Different alternative already created an opening for this QUO");

        var existingById = await db.Cases.AsNoTracking().AnyAsync(x =>
            x.SourceQuoteId == accepted.QuoteId && x.ApertureType == "AUTOMATIC", ct);

        if (alreadyCreated is null && !existingById)
        {
            // The Auth JWT currently has no company claim. Reuse exactly the same
            // single-tenant bootstrap rule as OperationsCaseAccess; never guess.
            var companyIds = await db.Teams.AsNoTracking().Select(x => x.CompanyId)
                .Union(db.Cases.AsNoTracking().Select(x => x.CompanyId))
                .Distinct().Take(2).ToArrayAsync(ct);
            if (companyIds.Length != 1 || companyIds[0] == Guid.Empty)
                throw new InvalidOperationException("Operations company identity is ambiguous or missing");

            var next = await NextIdtraAsync(db, ct);
            var idtra = "IDTRA" + next;
            var operation = new OperationsCase
            {
                CompanyId = companyIds[0],
                AccountId = accepted.AccountId!.Value,
                ExecutiveUserId = accepted.ExecutiveUserId!.Value,
                SourceQuoteId = accepted.QuoteId,
                SourceQuoteOptionId = accepted.SelectedQuoteOptionId,
                ApertureType = "AUTOMATIC",
                SeriesCode = "00000",
                SequenceNumber = next,
                Idtra = idtra,
                Status = "PENDING_VALIDATION",
                ModalityCode = accepted.ModalityCode!.Trim().ToUpperInvariant(),
                CreatedBy = accepted.AcceptedBy!.Value,
                OpeningDataJson = OpeningData(accepted, json)
            };
            var stage = new OperationsCaseStage
                { CaseId = operation.Id, DepartmentCode = "APERTURAS", Status = "PENDING" };
            operation.Stages.Add(stage);
            db.Cases.Add(operation);

            var quote = new OperationsCaseQuoteLink
            {
                CaseId = operation.Id,
                QuoteId = accepted.QuoteId,
                QuoteOptionId = accepted.SelectedQuoteOptionId,
                QuoteNumber = baseQuote,
                IsPrimary = true,
                Status = "ACCEPTED",
                SnapshotVersion = accepted.SchemaVersion,
                LinkedBy = accepted.AcceptedBy.Value
            };
            db.CaseQuoteLinks.Add(quote);
            foreach (var item in accepted.Costs ?? [])
            {
                // The accepted costs are a financial snapshot, never editable
                // references back into the Pricing tables.
                db.CaseQuoteCosts.Add(new OperationsCaseQuoteCost
                {
                    QuoteLinkId = quote.Id,
                    SourceCostId = item.SourceCostId == Guid.Empty ? null : item.SourceCostId,
                    CostCode = Limit(item.CostCode ?? "UNCLASSIFIED", 60),
                    Description = Limit(item.Description ?? "Sin descripcion", 500),
                    SupplierName = Limit(item.SupplierName, 200),
                    CurrencyCode = IsoCurrency(item.CurrencyCode),
                    CostAmount = item.CostAmount,
                    SaleAmount = item.SaleAmount
                });
            }
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.case.created.v1",
                JsonSerializer.Serialize(new { caseId = operation.Id, operation.Idtra,
                    sourceQuoteId = accepted.QuoteId, quoteOptionId = accepted.SelectedQuoteOptionId,
                    automated = true, version = operation.Version })));
            logger.LogInformation("Automatic opening created {Idtra} for accepted QUO {QuoteId}",
                idtra, accepted.QuoteId);
        }

        var receipt = new InboxMessage
        {
            EventId = eventId,
            ConsumerService = "operations-pricing-accepted",
            SourceService = "DholePricingService",
            EventType = "pricing.quote.accepted.v1",
            EventName = "pricing.quote.accepted.v1",
            Status = InboxMessageStatus.Processed,
            ProcessedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.InboxMessages.Add(receipt);
        db.Entry(receipt).Property("PayloadJson").CurrentValue = json;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task<long> NextIdtraAsync(OperationsDbContext db, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var command = new NpgsqlCommand("""
            INSERT INTO operations.operations_case_sequences (id, series_code, last_number, updated_at_utc)
            VALUES (@id, '00000', 1, now())
            ON CONFLICT (series_code)
            DO UPDATE SET last_number = operations_case_sequences.last_number + 1,
                updated_at_utc = now()
            RETURNING last_number;
            """, connection,
            (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static string OpeningData(AcceptedQuote source, string immutableQuoteJson)
    {
        // The opening may be reviewed without altering the accepted commercial snapshot.
        // Quoted weight is displayed as reference only; ACTUAL weight is entered in Origin.
        var root = new JsonObject
        {
            ["shipmentType"] = source.ModalityCode,
            ["pol"] = source.PolName,
            ["pod"] = source.PodName ?? source.PoeName,
            ["incoterm"] = source.IncotermCode,
            ["requiresPickup"] = !string.IsNullOrWhiteSpace(source.PickupAddress),
            ["pickupAddress"] = source.PickupAddress,
            ["cargoDescription"] = source.CargoLinesJson,
            ["quotedWeightKg"] = source.QuotedWeightKg,
            ["quotedVolumeCbm"] = source.QuotedVolumeCbm,
            ["acceptedCommercialSnapshot"] = JsonNode.Parse(immutableQuoteJson)
        };
        var equipment = new JsonArray();
        foreach (var item in source.Equipment ?? [])
            equipment.Add(new JsonObject
            {
                ["equipmentType"] = item.ContainerTypeName,
                ["quantity"] = item.Quantity
            });
        root["equipment"] = equipment;
        return root.ToJsonString();
    }

    private static string? Field(IReadOnlyDictionary<string, string> map, params string[] keys)
    {
        foreach (var key in keys)
            if (map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        return null;
    }

    private static string BaseQuote(string quote)
    {
        var match = System.Text.RegularExpressions.Regex.Match(quote,
            @"^QUO-\d{5}-\d{6}(?=-[A-Z]+$|$)");
        return match.Success ? match.Value : quote.Trim();
    }
    private static string Limit(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty :
        value.Length <= max ? value : value[..max];
    private static string IsoCurrency(string? code) =>
        code?.Trim().ToUpperInvariant() is { Length: 3 } iso &&
        iso.All(x => x is >= 'A' and <= 'Z') ? iso : "USD";

    public sealed record AcceptedQuote(
        int SchemaVersion, Guid QuoteId, Guid SelectedQuoteOptionId, string? QuoteNumber,
        Guid? AccountId, Guid? ExecutiveUserId, Guid? AcceptedBy, string? ModalityCode,
        string? PolName, string? PoeName, string? PodName, string? IncotermCode,
        string? PickupAddress, string? CargoLinesJson, decimal QuotedWeightKg,
        decimal QuotedVolumeCbm, IReadOnlyList<AcceptedEquipment>? Equipment,
        IReadOnlyList<AcceptedCost>? Costs);
    public sealed record AcceptedEquipment(string? ContainerTypeName, int Quantity);
    public sealed record AcceptedCost(Guid SourceCostId, string? CostCode,
        string? Description, string? SupplierName, string? CurrencyCode,
        decimal CostAmount, decimal SaleAmount);
}
