using System.Data;
using System.Text.Json;
using CustomCodeFramework.Messaging.Inbox;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Persistence.Cases;

public sealed record AcceptedQuoteIntegrationEvent(
    int SchemaVersion,
    Guid QuoteId,
    Guid? QuoteGroupId,
    Guid? SelectedOptionId,
    string QuoteNumber,
    int RevisionNumber,
    Guid? ClientAccountId,
    string? ClientName,
    Guid? ExecutiveUserId,
    Guid? AcceptedBy,
    DateTime? AcceptedAtUtc,
    string? AcceptanceObservations,
    JsonElement CommercialSnapshot);

/// <summary>
/// PostgreSQL transaction: Inbox receipt, unique QUO/option, IDTRA, immutable
/// snapshot/costs and Operations realtime Outbox succeed or fail together.
/// Acknowledgement of the Redis entry happens only AFTER this transaction commits.
/// </summary>
public sealed class AcceptedQuoteCaseMaterializer(OperationsDbContext db)
{
    public const string ConsumerService = "operations-accepted-quote";

    public async Task<string> ApplyAsync(Guid eventId, string payloadJson, CancellationToken ct)
    {
        if (eventId == Guid.Empty) throw new InvalidOperationException("Pricing event ID required.");
        var item = JsonSerializer.Deserialize<AcceptedQuoteIntegrationEvent>(
            payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new JsonException("Pricing accepted QUO payload missing.");
        if (item.SchemaVersion != 1 || item.QuoteId == Guid.Empty ||
            item.RevisionNumber < 1 || string.IsNullOrWhiteSpace(item.QuoteNumber) ||
            item.CommercialSnapshot.ValueKind != JsonValueKind.Object ||
            !item.CommercialSnapshot.TryGetProperty("Details", out var details) ||
            details.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Unrecognized or incomplete accepted QUO snapshot.");
        if (!item.ClientAccountId.HasValue || item.ClientAccountId.Value == Guid.Empty ||
            !item.ExecutiveUserId.HasValue || item.ExecutiveUserId.Value == Guid.Empty ||
            !item.AcceptedBy.HasValue || item.AcceptedBy.Value == Guid.Empty)
            throw new InvalidOperationException("CRM account, commercial executive and acceptance actor must be resolved.");
        if (item.QuoteGroupId.HasValue &&
            (item.QuoteGroupId == Guid.Empty || item.SelectedOptionId != item.QuoteId))
            throw new InvalidOperationException("Accepted subQUO must match the selected A/B option.");

        await using var transaction =
            await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var key = (item.QuoteGroupId ?? item.QuoteId).ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"operations:accepted:" + key}, 0));", ct);

        if (await db.InboxMessages.AnyAsync(x =>
            x.ConsumerService == ConsumerService && x.EventId == eventId, ct))
        {
            await transaction.CommitAsync(ct);
            return "ALREADY_PROCESSED";
        }
        var existing = await db.Cases.AsNoTracking().FirstOrDefaultAsync(
            x => x.ApertureType == "AUTOMATIC" &&
                (x.SourceQuoteId == item.QuoteId ||
                    (item.QuoteGroupId.HasValue &&
                    x.SourceQuoteGroupId == item.QuoteGroupId)), ct);

        if (existing is not null)
        {
            AddReceipt(eventId, payloadJson);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return existing.Idtra;
        }

        // Pricing does not own an Operations company. Only a uniquely existing
        // Operations tenant is safe; ambiguous/missing tenant remains pending.
        var known = await db.Teams.AsNoTracking()
            .Select(x => x.CompanyId).Distinct().Take(2).ToListAsync(ct);
        var fromCases = await db.Cases.AsNoTracking()
            .Select(x => x.CompanyId).Distinct().Take(2).ToListAsync(ct);
        var companies = known.Concat(fromCases)
            .Where(x => x != Guid.Empty).Distinct().Take(2).ToArray();
        if (companies.Length != 1)
            throw new InvalidOperationException("Operations tenant cannot be resolved uniquely; event remains pending.");

        var snapshot = item.CommercialSnapshot;
        var modality = Shipment(Get(snapshot, "ShipmentMode"));
        if (modality is not ("FCL" or "LCL" or "FTL" or "LTL" or "AIR"))
            throw new InvalidOperationException("The accepted quotation does not identify a supported shipment mode.");

        var now = DateTimeOffset.UtcNow;
        var next = await OperationsIdtraAllocator.NextAsync(db, ct);
        var caseId = Guid.NewGuid();
        var incoterm = Text(snapshot, "IncotermCode");
        var pickupAddress = Text(snapshot, "PickupAddress");
        var units = Array.Empty<object>();
        if (snapshot.TryGetProperty("Containers", out var containers) &&
            containers.ValueKind == JsonValueKind.Array)
            units = containers.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x => (object)new
                {
                    equipmentType = Text(x, "ContainerTypeCode") ?? Text(x, "ContainerTypeName") ?? "",
                    equipmentCatalogId = Text(x, "ContainerTypeId") ?? "",
                    quantity = Integer(x, "Quantity")
                }).ToArray();

        var services = new List<string>();
        if (snapshot.TryGetProperty("Services", out var quotedServices) &&
            quotedServices.ValueKind == JsonValueKind.Array)
            foreach (var service in quotedServices.EnumerateArray())
                if (service.ValueKind == JsonValueKind.Object &&
                    Text(service, "ServiceName") is { Length: > 0 } name)
                    services.Add(name);

        // OpeningData is operational working data. The original Pricing JSON
        // remains immutable on OperationsCaseQuoteLink.SnapshotJson.
        var draft = JsonSerializer.Serialize(new
        {
            quoteId = item.QuoteId, quoteOptionId = item.SelectedOptionId,
            quoteGroupId = item.QuoteGroupId, quoteNumber = item.QuoteNumber,
            quoteRevision = item.RevisionNumber, clientName = item.ClientName,
            executiveName = Text(snapshot, "ExecutiveName"),
            shipmentType = modality, transportType = modality switch {
                "AIR" => "AIR", "FTL" or "LTL" => "LAND", _ => "SEA" },
            pol = Text(snapshot, "PolName") ?? "",
            poe = Text(snapshot, "PoeName") ?? "",
            pod = Text(snapshot, "PodName") ?? Text(snapshot, "PoeName") ?? "",
            incoterm = incoterm ?? "",
            cargoDescription = "", // Never invent a merchandise description.
            equipment = units,
            estimatedWeightKg = Decimal(snapshot, "TotalWeightKg"),
            volumeCbm = Decimal(snapshot, "TotalVolumeCbm"),
            totalPackages = Integer(snapshot, "TotalPackages"),
            carrierReference = Text(snapshot, "CarrierName"),
            agentName = Text(snapshot, "AgentName"),
            requiresPickup = string.Equals(incoterm, "EXW", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(pickupAddress),
            pickupAddress = pickupAddress ?? "",
            pickupContact = "",
            pickupWindow = "",
            whsName = "",
            services,
            acceptanceObservations = item.AcceptanceObservations
        });

        var company = companies[0];
        var openingTeamIds = await db.Teams.AsNoTracking()
            .Where(t => t.CompanyId == company && t.IsActive && t.DepartmentCode == "APERTURAS")
            .Select(t => t.Id).Take(2).ToArrayAsync(ct);
        var stage = new OperationsCaseStage {
            CaseId = caseId, DepartmentCode = "APERTURAS", Status = "PENDING",
            TeamId = openingTeamIds.Length == 1 ? openingTeamIds[0] : null
        };
        var operation = new OperationsCase
        {
            Id = caseId, CompanyId = company, Idtra = $"IDTRA{next}",
            SeriesCode = "00000", SequenceNumber = next,
            AccountId = item.ClientAccountId!.Value,
            ExecutiveUserId = item.ExecutiveUserId!.Value,
            SourceQuoteId = item.QuoteId,
            SourceQuoteGroupId = item.QuoteGroupId,
            SourceQuoteOptionId = item.SelectedOptionId,
            ApertureType = "AUTOMATIC", ModalityCode = modality,
            OriginCountryCode = Country(Text(snapshot, "PolCode")),
            DestinationCountryCode = Country(Text(snapshot, "PodCode")),
            Status = "PENDING_VALIDATION", CreatedBy = item.AcceptedBy!.Value,
            OpeningDataJson = draft,
        };
        operation.Stages.Add(stage);
        db.Cases.Add(operation);

        var quoteLink = new OperationsCaseQuoteLink
        {
            CaseId = caseId, QuoteId = item.QuoteId,
            QuoteOptionId = item.SelectedOptionId,
            QuoteNumber = item.QuoteNumber, SnapshotJson = snapshot.GetRawText(),
            SnapshotVersion = item.RevisionNumber, IsPrimary = true,
            LinkedBy = item.AcceptedBy.Value, LinkedAtUtc = now,
            Status = "ACCEPTED_SNAPSHOT"
        };
        foreach (var detail in details.EnumerateArray())
        {
            if (detail.ValueKind != JsonValueKind.Object) continue;
            var detailId = Guid.TryParse(Text(detail, "Id"), out var parsed)
                ? parsed : (Guid?)null;
            var cost = new OperationsCaseQuoteCost
            {
                SourceCostId = detailId,
                CostCode = Text(detail, "CostType") ?? "QUOTED",
                Description = (Text(detail, "Name") ?? "Línea comercial").Trim(),
                CurrencyCode = Currency(detail),
                CostAmount = Decimal(detail, "CostAmount"),
                SaleAmount = Decimal(detail, "SaleAmount"),
                CapturedAtUtc = now
            };
            quoteLink.Costs.Add(cost);
        }
        db.CaseQuoteLinks.Add(quoteLink);
        db.CaseTasks.Add(new OperationsCaseTask
        {
            CaseId = caseId, CaseStageId = stage.Id, CreatedBy = item.AcceptedBy.Value,
            Title = "Revisar y validar la apertura automática",
            Details = "Complete los campos pendientes y confirme los documentos antes de transferir a Coordinación Origen.",
            Priority = 2
        });

        OperationsAuditWriter.Case(db, caseId, stage.Id, item.AcceptedBy.Value,
            "operations.aperture.auto-created", null, new {
                operation.Idtra, item.QuoteId, item.QuoteGroupId,
                item.SelectedOptionId, item.RevisionNumber, item.AcceptedAtUtc
            });
        db.OutboxMessages.Add(OperationsOutboxFactory.Create(
            "operations.case.created.v1", JsonSerializer.Serialize(new {
                caseId, idtra = operation.Idtra, sourceQuoteId = item.QuoteId,
                apertureType = "AUTOMATIC", version = operation.Version
            })));
        AddReceipt(eventId, payloadJson);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return operation.Idtra;
    }

    private void AddReceipt(Guid id, string payloadJson)
    {
        var receipt = new InboxMessage {
            ConsumerService = ConsumerService, SourceService = "DholePricingService",
            EventId = id, EventType = "pricing.quote.accepted.v1",
            EventName = "pricing.quote.accepted.v1",
            Status = InboxMessageStatus.Processed,
            CreatedAtUtc = DateTime.UtcNow, ProcessedAtUtc = DateTime.UtcNow
        };
        db.InboxMessages.Add(receipt);
        db.Entry(receipt).Property("PayloadJson").CurrentValue = payloadJson;
    }

    public static string Shipment(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n))
            return n switch { 1 => "FCL", 2 => "LCL", 3 => "FTL", 4 => "LTL",
                5 or 6 => "AIR", _ => "" };
        var text = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return text?.Trim().ToUpperInvariant() switch {
            "FCL" => "FCL", "LCL" => "LCL", "FTL" => "FTL", "LTL" => "LTL",
            "AIR" or "AIRCONSOL" => "AIR", _ => ""
        };
    }

    private static JsonElement Get(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var x)
            ? x : default;
    private static string? Text(JsonElement o, string key)
    {
        var x = Get(o, key);
        return x.ValueKind == JsonValueKind.String ? x.GetString()?.Trim() :
            x.ValueKind == JsonValueKind.Number ? x.GetRawText() : null;
    }
    private static int Integer(JsonElement o, string key) =>
        Get(o, key) is { ValueKind: JsonValueKind.Number } x && x.TryGetInt32(out var n) ? n : 0;
    private static decimal Decimal(JsonElement o, string key) =>
        Get(o, key) is { ValueKind: JsonValueKind.Number } x && x.TryGetDecimal(out var n) ? n : 0m;
    private static string? Country(string? code) =>
        code is { Length: >= 5 } && char.IsLetter(code[0]) && char.IsLetter(code[1])
            ? code[..2].ToUpperInvariant() : null;
    private static string Currency(JsonElement detail)
    {
        var code = Text(detail, "CurrencyCode")?.ToUpperInvariant();
        if (code is { Length: 3 } && code.All(char.IsLetter)) return code;
        var name = Text(detail, "CurrencyName")?.ToUpperInvariant();
        if (name?.Contains("CRC") == true || name?.Contains("COLÓN") == true ||
            name?.Contains("COLON") == true) return "CRC";
        if (name?.Contains("USD") == true || name?.Contains("DOLLAR") == true ||
            name?.Contains("DÓLAR") == true) return "USD";
        throw new InvalidOperationException("Commercial currency could not be resolved without guessing.");
    }
}
