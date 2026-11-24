using Dhole.Operations.Persistence.Audit;
using System.Data;
using System.Text.Json;
using Dhole.Operations.Application.Cases;
using Dhole.Operations.Persistence.Messaging;
using Dhole.Operations.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Dhole.Operations.Persistence.Cases;

/// <summary>Transactional Operations writes. External CRM/Pricing/Storage identifiers are references only.</summary>
public sealed class OperationsCaseStore(OperationsDbContext db) : IOperationsCaseStore
{
    public async Task<CaseCreatedResult> CreateManualAsync(CreateManualCaseCommand input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var next = await NextSequenceAsync("00000", ct);
        // One global, server-owned counter. No forced zeros or series in the visible IDTRA.
        var idtra = $"IDTRA{next}";
        var operation = new OperationsCase
        {
            CompanyId = input.CompanyId,
            SeriesCode = "00000",
            OpeningDataJson = input.OpeningDataJson ?? "{}",
            SequenceNumber = next,
            Idtra = idtra,
            AccountId = input.AccountId,
            ExecutiveUserId = input.ExecutiveUserId,
            ModalityCode = input.ModalityCode.Trim().ToUpperInvariant(),
            OriginCountryCode = input.OriginCountryCode?.ToUpperInvariant(),
            DestinationCountryCode = input.DestinationCountryCode?.ToUpperInvariant(),
            Priority = input.Priority,
            ComplexityPoints = input.ComplexityPoints,
            Status = "DRAFT",
            CreatedBy = input.ActorUserId
        };
        var openingStage = new OperationsCaseStage { CaseId = operation.Id, DepartmentCode = "APERTURAS" };
        operation.Stages.Add(openingStage);
        db.Cases.Add(operation);
        OperationsAuditWriter.Case(db, operation.Id, openingStage.Id, input.ActorUserId,
            "operations.case.created", null, new {
                operation.Idtra, operation.CompanyId, operation.AccountId, operation.ModalityCode
            });
        Enqueue("operations.case.created.v1", new { caseId = operation.Id, idtra, actorId = input.ActorUserId, version = operation.Version });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CaseCreatedResult(operation.Id, idtra, operation.Status, openingStage.Id);
    }

    /// <summary>
    /// Atomically materialize exactly one Operations case for a definitively
    /// accepted parent QUO. PostgreSQL is the final authority for event Inbox,
    /// business idempotency, IDTRA sequence and historical Pricing snapshot.
    /// CRM actor/company must be resolved by the ingestion worker first.
    /// </summary>
    public async Task<AcceptedQuoteCaseCreatedResult> CreateAcceptedQuoteAsync(
        CreateAcceptedQuoteCaseCommand input, CancellationToken ct)
    {
        if (new[] { input.CompanyId, input.AccountId, input.ExecutiveUserId,
                input.ActorUserId, input.QuoteId, input.AcceptedOptionId, input.SourceEventId }
                .Any(x => x == Guid.Empty) ||
            string.IsNullOrWhiteSpace(input.QuoteNumber) ||
            input.QuoteNumber.Length > 60 ||
            input.CommercialRevision <= 0 ||
            string.IsNullOrWhiteSpace(input.ModalityCode) ||
            input.ModalityCode.Length > 30 ||
            input.AcceptedAtUtc.Year < 2000 ||
            input.CommercialSnapshotJson.Length > 1_000_000)
            throw new InvalidOperationException("Verified Pricing acceptance metadata is incomplete.");

        using var snapshot = JsonDocument.Parse(input.CommercialSnapshotJson);
        var accepted = snapshot.RootElement;
        if (accepted.ValueKind != JsonValueKind.Object ||
            !GuidProperty(accepted, "quoteId").Equals(input.QuoteId) ||
            !GuidProperty(accepted, "acceptedOptionId").Equals(input.AcceptedOptionId) ||
            !GuidProperty(accepted, "crmAccountId").Equals(input.AccountId) ||
            !GuidProperty(accepted, "acceptedByUserId").Equals(input.ActorUserId) ||
            IntProperty(accepted, "commercialRevision") != input.CommercialRevision ||
            !StringProperty(accepted, "shipmentMode")
                .Equals(input.ModalityCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Accepted QUO payload does not match the CRM-verified identity.");

        var services = ArrayProperty(accepted, "services").Select(element => new
        {
            Id = GuidProperty(element, "serviceId"),
            Code = StringProperty(element, "serviceCode"),
            Name = StringProperty(element, "serviceName")
        }).ToArray();
        if (services.Any(x => x.Id == Guid.Empty || x.Code.Length is < 1 or > 60 ||
            x.Name.Length is < 1 or > 200))
            throw new InvalidOperationException("Accepted quotation has incomplete service lines.");

        var equipment = ArrayProperty(accepted, "equipment").Select(element => new
        {
            Id = GuidProperty(element, "equipmentId"),
            Code = StringProperty(element, "equipmentCode"),
            Name = StringProperty(element, "equipmentName"),
            Quantity = IntProperty(element, "quantity"),
            WeightJson = element.TryGetProperty("unitWeightsKg", out var weights)
                && weights.ValueKind == JsonValueKind.Array ? weights.GetRawText() : "[]"
        }).ToArray();
        if (equipment.Any(x => x.Id == Guid.Empty || x.Code.Length is < 1 or > 50 ||
            x.Name.Length is < 1 or > 200 || x.Quantity <= 0))
            throw new InvalidOperationException("Accepted quotation has an invalid equipment combination.");

        var costs = ArrayProperty(accepted, "costLines").Select(element => new
        {
            RateDetailId = GuidProperty(element, "rateDetailId"),
            SourceCostId = GuidProperty(element, "sourceCostId"),
            Description = StringProperty(element, "description"),
            CostCode = StringProperty(element, "costType"),
            CurrencyCode = StringProperty(element, "currencyCode").ToUpperInvariant(),
            Quantity = DecimalProperty(element, "quantity"),
            UnitCost = DecimalProperty(element, "unitCost"),
            UnitSale = DecimalProperty(element, "unitSale")
        }).ToArray();
        if (costs.Any(x => x.RateDetailId == Guid.Empty ||
            x.Description.Length is < 1 or > 500 ||
            x.CurrencyCode.Length != 3 || !x.CurrencyCode.All(char.IsAsciiLetterUpper) ||
            x.Quantity <= 0 || x.UnitCost < 0 || x.UnitSale < 0 ||
            x.CostCode.Length is < 1 or > 60))
            throw new InvalidOperationException("Accepted quotation cost lines are invalid.");

        // Serialize under a shared business key, not just EventId: two distinct
        // retries of the same accepted A/B quotation cannot generate two IDTRA.
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        var lockKey = "accepted-quote:" + input.QuoteId.ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);

        var processed = await db.InboxMessages.AsNoTracking().AnyAsync(x =>
            x.ConsumerService == "operations-pricing-quote" &&
            x.EventId == input.SourceEventId, ct);
        var previous = await db.Cases.AsNoTracking()
            .Where(x => x.ApertureType == "AUTOMATIC" &&
                x.SourceQuoteId == input.QuoteId)
            .Select(x => new { x.Id, x.Idtra, x.CompanyId, x.SourceQuoteOptionId })
            .SingleOrDefaultAsync(ct);
        if (previous is not null &&
            (previous.SourceQuoteOptionId != input.AcceptedOptionId ||
             previous.CompanyId != input.CompanyId))
            throw new InvalidOperationException(
                "A different commercial option or company already owns this parent QUO.");

        if (processed || previous is not null)
        {
            if (previous is null)
                throw new InvalidOperationException(
                    "Inbox event exists without an accepted quote Operations case.");
            if (!processed)
            {
                AddPricingInboxReceipt(input.SourceEventId);
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return new AcceptedQuoteCaseCreatedResult(previous.Id, previous.Idtra, true);
        }

        var next = await NextSequenceAsync("00000", ct);
        var route = ObjectProperty(accepted, "route");
        var cargo = ObjectProperty(accepted, "cargo");
        var providers = ObjectProperty(accepted, "providers");
        var description = CargoDescription(cargo);
        var opening = new {
            source = "PRICING",
            quoteId = input.QuoteId,
            acceptedOptionId = input.AcceptedOptionId,
            commercialRevision = input.CommercialRevision,
            shipmentType = input.ModalityCode.ToUpperInvariant(),
            incoterm = StringProperty(accepted, "incoterm"),
            pol = StringProperty(route, "polName"),
            pod = StringProperty(route, "podName"),
            poe = StringProperty(route, "poeName"),
            cargoDescription = description,
            volumeCbm = DecimalProperty(cargo, "volumeCbm"),
            quotedWeightKg = DecimalProperty(cargo, "weightKg"),
            packages = DecimalProperty(cargo, "packages"),
            carrierReference = StringProperty(providers, "carrierName"),
            agentName = StringProperty(providers, "agentName"),
            equipment = equipment.Select(item => new {
                equipmentType = item.Code, item.Quantity, quantity = item.Quantity,
                equipmentId = item.Id, equipmentName = item.Name
            }).ToArray()
        };
        var operation = new OperationsCase
        {
            CompanyId = input.CompanyId, SeriesCode = "00000",
            SequenceNumber = next, Idtra = $"IDTRA{next}",
            AccountId = input.AccountId, ExecutiveUserId = input.ExecutiveUserId,
            SourceQuoteId = input.QuoteId, SourceQuoteOptionId = input.AcceptedOptionId,
            ApertureType = "AUTOMATIC", ModalityCode = input.ModalityCode.Trim().ToUpperInvariant(),
            Status = "PENDING_VALIDATION", Priority = 3, ComplexityPoints = 1m,
            OpeningDataJson = JsonSerializer.Serialize(opening),
            CreatedBy = input.ActorUserId
        };
        var stage = new OperationsCaseStage {
            CaseId = operation.Id, DepartmentCode = "APERTURAS",
            Status = "PENDING"
        };
        operation.Stages.Add(stage);
        db.Cases.Add(operation);
        db.AcceptedQuoteSnapshots.Add(new OperationsAcceptedQuoteSnapshot {
            CaseId = operation.Id, EventId = input.SourceEventId,
            QuoteId = input.QuoteId, AcceptedOptionId = input.AcceptedOptionId,
            CommercialRevision = input.CommercialRevision,
            SnapshotJson = input.CommercialSnapshotJson,
            AcceptedAtUtc = input.AcceptedAtUtc
        });
        db.CaseServiceLines.AddRange(services.Select(item => new OperationsCaseServiceLine
        {
            CaseId = operation.Id, PricingServiceId = item.Id,
            ServiceCode = item.Code, ServiceName = item.Name,
            Source = "QUOTED", CreatedBy = input.ActorUserId
        }));
        db.CaseEquipmentLines.AddRange(equipment.Select(item => new OperationsCaseEquipmentLine
        {
            CaseId = operation.Id, EquipmentTypeId = item.Id,
            EquipmentCode = item.Code, EquipmentName = item.Name,
            Quantity = item.Quantity, UnitWeightsKgJson = item.WeightJson
        }));
        var quoteLink = new OperationsCaseQuoteLink
        {
            CaseId = operation.Id, QuoteId = input.QuoteId,
            QuoteOptionId = input.AcceptedOptionId,
            QuoteNumber = input.QuoteNumber,
            IsPrimary = true, Status = "ACCEPTED",
            SnapshotVersion = input.CommercialRevision, LinkedBy = input.ActorUserId
        };
        // Old source_cost_id index is unique per link. Preserve every repeated
        // cost row; when several details refer to the same catalog cost, keep
        // the unique catalog reference only once and all other details in the
        // immutable JSON snapshot. No Pricing row is mutated.
        var seenCostIds = new HashSet<Guid>();
        foreach (var item in costs)
        {
            var catalogCostId = item.SourceCostId != Guid.Empty &&
                seenCostIds.Add(item.SourceCostId)
                    ? item.SourceCostId : (Guid?)null;
            quoteLink.Costs.Add(new OperationsCaseQuoteCost
            {
                QuoteLinkId = quoteLink.Id,
                SourceCostId = catalogCostId,
                Description = item.Description,
                CostCode = item.CostCode, CurrencyCode = item.CurrencyCode,
                CostAmount = item.UnitCost * item.Quantity,
                SaleAmount = item.UnitSale * item.Quantity
            });
        }
        db.CaseQuoteLinks.Add(quoteLink);

        AddPricingInboxReceipt(input.SourceEventId);
        OperationsAuditWriter.Case(db, operation.Id, stage.Id, input.ActorUserId,
            "operations.case.accepted-quote.created", null,
            new { operation.Idtra, input.QuoteId, input.AcceptedOptionId,
                input.CommercialRevision, input.SourceEventId,
                services = services.Length, equipment = equipment.Sum(x => x.Quantity),
                costs = costs.Length });
        Enqueue("operations.case.created.v1", new {
            caseId = operation.Id, operation.Idtra, operation.CompanyId,
            quoteId = input.QuoteId, quoteOptionId = input.AcceptedOptionId,
            status = operation.Status, version = operation.Version
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new AcceptedQuoteCaseCreatedResult(operation.Id, operation.Idtra, false);
    }

    private void AddPricingInboxReceipt(Guid eventId)
    {
        var receipt = new CustomCodeFramework.Messaging.Inbox.InboxMessage
        {
            EventId = eventId,
            EventType = "pricing.quote.accepted.v1",
            EventName = "pricing.quote.accepted.v1",
            SourceService = "DholePricingService",
            ConsumerService = "operations-pricing-quote",
            Status = CustomCodeFramework.Messaging.Inbox.InboxMessageStatus.Processed,
            CreatedAtUtc = DateTime.UtcNow,
            ProcessedAtUtc = DateTime.UtcNow
        };
        db.InboxMessages.Add(receipt);
        db.Entry(receipt).Property("PayloadJson").CurrentValue = "{}";
    }

    private static JsonElement ObjectProperty(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : default;

    private static JsonElement[] ArrayProperty(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray() : [];

    private static Guid GuidProperty(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        Guid.TryParse(value.GetString(), out var parsed)
            ? parsed : Guid.Empty;

    private static string StringProperty(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? "" : "";

    private static int IntProperty(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed : 0;

    private static decimal DecimalProperty(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDecimal(out var parsed)
            ? parsed : 0m;

    private static string CargoDescription(JsonElement cargo)
    {
        var json = StringProperty(cargo, "cargoLinesJson");
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                return document.RootElement.EnumerateArray()
                    .Select(line => StringProperty(line, "description"))
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
        }
        catch (JsonException) { }
        return "";
    }

    public async Task<StageAssignedResult?> AssignStageAsync(AssignStageCommand input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var stage = await db.CaseStages.SingleOrDefaultAsync(
            x => x.Id == input.StageId && x.CaseId == input.CaseId, ct);
        var operation = await db.Cases.SingleOrDefaultAsync(x => x.Id == input.CaseId, ct);
        if (stage is null || operation is null || stage.Status is "COMPLETED" or "SKIPPED")
            return null;

        // Serialize assignments for this department: no simultaneous stale workload calculations.
        var lockKey = operation.CompanyId.ToString("N") + ":" + stage.DepartmentCode;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);

        if (await db.StageAssignments.AnyAsync(x => x.CaseStageId == stage.Id && x.ReleasedAtUtc == null, ct))
            return null;

        // Routing predicates always derive from persisted case data, not caller-supplied assign parameters.
        var origin = operation.OriginCountryCode;
        var destination = operation.DestinationCountryCode;
        var rules = await db.AssignmentRules.AsNoTracking()
            .Include(x => x.Team).ThenInclude(x => x.Members)
            .Where(x => x.IsActive && x.Team.IsActive && x.Team.CompanyId == operation.CompanyId &&
                x.DepartmentCode == stage.DepartmentCode &&
                (x.ModalityCode == null || x.ModalityCode == operation.ModalityCode) &&
                (x.OriginCountryCode == null || x.OriginCountryCode == origin) &&
                (x.DestinationCountryCode == null || x.DestinationCountryCode == destination))
            .ToListAsync(ct);

        var load = await db.StageAssignments.AsNoTracking()
            .Where(x => x.ReleasedAtUtc == null)
            .GroupBy(x => x.UserId).Select(g => new { UserId = g.Key, Points = g.Sum(x => x.AssignedPoints) })
            .ToDictionaryAsync(x => x.UserId, x => x.Points, ct);

        var candidates = rules.SelectMany(rule => rule.Team.Members
                .Where(member => member.IsAvailable && member.CapacityPoints > 0)
                .Select(member => new
                {
                    rule.TeamId, member.UserId, rule.Priority,
                    RelativeLoad = (load.GetValueOrDefault(member.UserId) + operation.ComplexityPoints)
                        / member.CapacityPoints
                }))
            .OrderBy(x => x.RelativeLoad).ThenByDescending(x => x.Priority)
            .ThenBy(x => x.UserId).ThenBy(x => x.TeamId).ToList();

        if (candidates.Count == 0) return null;
        var selected = candidates[0];
        var assignment = new OperationsStageAssignment
        {
            CaseStageId = stage.Id, TeamId = selected.TeamId, UserId = selected.UserId,
            AssignedPoints = operation.ComplexityPoints, AssignedBy = input.ActorUserId
        };
        stage.TeamId = selected.TeamId;
        stage.Status = "READY";
        stage.Version++;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        operation.Version++;
        db.StageAssignments.Add(assignment);
        if (stage.DepartmentCode == "COORDINACION_ORIGEN")
        {
            if (!await db.OriginProgress.AnyAsync(x => x.CaseStageId == stage.Id, ct))
                db.OriginProgress.Add(new OperationsOriginProgress {
                    CaseStageId = stage.Id, StatusCode = "ORIGIN_COORDINATION"
                });
            Enqueue("operations.origin.assigned.v1", new {
                caseId = operation.Id, stageId = stage.Id,
                version = operation.Version, teamId = assignment.TeamId,
                assignedUserId = assignment.UserId
            });
        }
        OperationsAuditWriter.Case(db, operation.Id, stage.Id, input.ActorUserId,
            "operations.stage.assigned", null, new {
                assignment.TeamId, assignment.UserId, stage.DepartmentCode
            });
        Enqueue("operations.case.assigned.v1",
            new { caseId = operation.Id, stageId = stage.Id, actorId = input.ActorUserId, version = operation.Version });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new StageAssignedResult(assignment.Id, selected.TeamId, selected.UserId, selected.RelativeLoad);
    }

    public async Task<DocumentRegisteredResult> RegisterDocumentAsync(RegisterDocumentCommand input, CancellationToken ct)
    {
        var operationExists = await db.Cases.AnyAsync(x => x.Id == input.CaseId, ct);
        if (!operationExists) throw new KeyNotFoundException("Operations case not found.");
        var document = new OperationsDocument
        {
            CaseId = input.CaseId, DocumentTypeCode = input.DocumentTypeCode.Trim().ToUpperInvariant()
        };
        var version = new OperationsDocumentVersion
        {
            DocumentId = document.Id, VersionNumber = 1, StorageFileId = input.StorageFileId,
            FileName = input.FileName, MimeType = input.MimeType,
            Sha256 = input.Sha256, UploadedBy = input.ActorUserId
        };
        document.Versions.Add(version);
        db.Documents.Add(document);
        OperationsAuditWriter.Case(db, input.CaseId, null, input.ActorUserId,
            "operations.document.registered", null,
            new { documentId = document.Id, versionId = version.Id, input.DocumentTypeCode });
        Enqueue("operations.document.registered.v1", new { caseId = input.CaseId, documentId = document.Id, versionId = version.Id });
        await db.SaveChangesAsync(ct);
        return new DocumentRegisteredResult(document.Id, version.Id);
    }

    public async Task<DocumentExtractionQueuedResult> QueueDocumentExtractionAsync(QueueDocumentExtractionCommand input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var doc = await db.DocumentVersions.Where(x => x.Id == input.DocumentVersionId)
            .Select(x => new { x.Id, x.StorageFileId, x.Document.CaseId }).SingleOrDefaultAsync(ct);
        if (doc is null || doc.CaseId != input.CaseId)
            throw new KeyNotFoundException("Document version not found in case.");

        var lockKey = "operations-extraction:" + doc.Id.ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);
        var existing = await db.ExtractionJobs.AsNoTracking()
            .Where(x => x.DocumentVersionId == doc.Id && (x.Status == "QUEUED" || x.Status == "PROCESSING" || x.Status == "REVIEW_REQUIRED"))
            .OrderByDescending(x => x.RequestedAtUtc).FirstOrDefaultAsync(ct);
        if (existing is not null)
            return new DocumentExtractionQueuedResult(existing.Id, existing.Status);

        var job = new OperationsDocumentExtractionJob
        {
            DocumentVersionId = doc.Id, RequestedBy = input.ActorUserId
        };
        db.ExtractionJobs.Add(job);
        OperationsAuditWriter.Case(db, input.CaseId, null, input.ActorUserId,
            "operations.document.extraction.queued", null,
            new { jobId = job.Id, documentVersionId = doc.Id });
        // DataExtraction/AI consumer contract is a separate integration: never invent a successful analysis.
        Enqueue("operations.document.extraction.requested.v1",
            new { caseId = input.CaseId, documentVersionId = doc.Id, storageFileId = doc.StorageFileId, jobId = job.Id });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new DocumentExtractionQueuedResult(job.Id, job.Status);
    }

    private async Task<long> NextSequenceAsync(string seriesCode, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO operations.operations_case_sequences (id, series_code, last_number, updated_at_utc)
            VALUES (@id, @series, 1, now())
            ON CONFLICT (series_code)
            DO UPDATE SET last_number = operations_case_sequences.last_number + 1, updated_at_utc = now()
            RETURNING last_number;
            """, connection, (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("series", seriesCode);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private void Enqueue(string name, object payload) =>
        db.OutboxMessages.Add(OperationsOutboxFactory.Create(name, JsonSerializer.Serialize(payload)));
}
