using Dhole.Operations.Persistence.Audit;
using System.Data;
using System.Text.Json;
using Dhole.Operations.Application.Cases;
using Dhole.Operations.Persistence.Messaging;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Dhole.Operations.Persistence.Cases;

/// <summary>Transactional Operations writes. External CRM/Pricing/Storage identifiers are references only.</summary>
public sealed class OperationsCaseStore(OperationsDbContext db, IOperationsAssignmentEligibility? eligibility = null) : IOperationsCaseStore
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
            Status = input.SendDirectlyToOrigin ? "OPEN" : "DRAFT",
            CreatedBy = input.ActorUserId
        };
        var openingStage = new OperationsCaseStage { CaseId = operation.Id, DepartmentCode = "APERTURAS" };
        if (input.SendDirectlyToOrigin)
        {
            openingStage.Status = "SKIPPED";
            openingStage.CompletedAtUtc = DateTimeOffset.UtcNow;
        }
        operation.Stages.Add(openingStage);
        var initialStage = openingStage;
        if (input.SendDirectlyToOrigin)
        {
            initialStage = new OperationsCaseStage {
                CaseId = operation.Id, DepartmentCode = "COORDINACION_ORIGEN", Status = "PENDING"
            };
            operation.Stages.Add(initialStage);
        }
        db.Cases.Add(operation);
        using (var opening = JsonDocument.Parse(operation.OpeningDataJson))
        {
            foreach (var service in ArrayProperty(opening.RootElement, "serviceSelections"))
            {
                var id = GuidProperty(service, "id");
                var code = StringProperty(service, "code");
                var label = StringProperty(service, "label");
                if (id == Guid.Empty || code.Length is < 1 or > 60 || label.Length is < 1 or > 200)
                    throw new ArgumentException("Invalid opening service reference.");
                db.CaseServiceLines.Add(new OperationsCaseServiceLine
                {
                    CaseId = operation.Id, PricingServiceId = id, ServiceCode = code, ServiceName = label,
                    Source = "OPERATIONS_ADDED", CreatedBy = input.ActorUserId
                });
            }
        }
        OperationsAuditWriter.Case(db, operation.Id, openingStage.Id, input.ActorUserId,
            "operations.case.created", null, new {
                operation.Idtra, operation.CompanyId, operation.AccountId, operation.ModalityCode
            });
        if (input.QuoteRateId is Guid rateId)
        {
            // Reserve the explicit option in the same transaction as the manual IDTRA.
            // Pricing remains authoritative for the accepted status, account and modality.
            var siblings = await db.Cases.AsNoTracking().Where(x => x.SourceQuoteOptionId == rateId &&
                x.CompanyId == operation.CompanyId && x.AccountId == operation.AccountId)
                .Select(x => x.Idtra).Distinct().Take(30).ToArrayAsync(ct);
            var link = new OperationsCaseQuoteLink
            {
                CaseId = operation.Id, QuoteId = rateId, QuoteOptionId = rateId,
                IsPrimary = false, Status = "PENDING_LINK", LinkedBy = input.ActorUserId
            };
            db.CaseQuoteLinks.Add(link);
            OperationsAuditWriter.Case(db, operation.Id, null, input.ActorUserId, "operations.quote-link.requested", null,
                new { requestId = link.Id, RateId = rateId, operation.Idtra });
            Enqueue("operations.quote-link.requested.v1", new { requestId = link.Id, caseId = operation.Id,
                operation.AccountId, operation.CompanyId, rateId, requestedBy = input.ActorUserId,
                operation.Idtra, operation.ModalityCode, existingIdtras = siblings });
        }
        Enqueue("operations.case.created.v1", new { caseId = operation.Id, idtra, actorId = input.ActorUserId, version = operation.Version });
        if (input.SendDirectlyToOrigin)
        {
            OperationsAuditWriter.Case(db, operation.Id, initialStage.Id, input.ActorUserId,
                "operations.manual.origin.queued", null, new { initialStage.DepartmentCode });
            Enqueue("operations.manual.origin.queued.v1", new {
                caseId = operation.Id, stageId = initialStage.Id, actorId = input.ActorUserId,
                operation.CompanyId, version = operation.Version
            });
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CaseCreatedResult(operation.Id, idtra, operation.Status, initialStage.Id);
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
            (GuidProperty(accepted, "crmAccountId") != Guid.Empty && !GuidProperty(accepted, "crmAccountId").Equals(input.AccountId)) ||
            !(GuidProperty(accepted, "acceptedByUserId") == input.ActorUserId ||
                (!string.IsNullOrWhiteSpace(StringProperty(accepted, "historicalIdtra")) &&
                 GuidProperty(accepted, "historicalSourceActorId") == input.ActorUserId)) ||
            IntProperty(accepted, "commercialRevision") != input.CommercialRevision ||
            !StringProperty(accepted, "shipmentMode")
                .Equals(input.ModalityCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Accepted QUO payload does not match the CRM-verified identity.");

        var historicalIdtra = StringProperty(accepted, "historicalIdtra");
        long? historicalNumber = null;
        if (!string.IsNullOrEmpty(historicalIdtra))
        {
            if (!TryHistoricalNumber(historicalIdtra, out var number))
                throw new InvalidOperationException("Historical IDTRA requires an unambiguous numeric identifier.");
            historicalNumber = number;
        }
        var listedIdtras = ArrayProperty(accepted, "historicalIdtras");
        var explicitIds = new Dictionary<long, string>();
        foreach (var item in listedIdtras)
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } id ||
                !TryHistoricalNumber(id, out var numeric) || !explicitIds.TryAdd(numeric, id))
                throw new InvalidOperationException("Ambiguous explicit IDTRA list.");
        }
        var multi = explicitIds.Count > 1;
        if (explicitIds.Count > 0 && (!historicalNumber.HasValue ||
            !explicitIds.TryGetValue(historicalNumber.Value, out var declared) || declared != historicalIdtra))
            throw new InvalidOperationException("IDTRA is not in the accepted quote list.");

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
            Quantity = OptionalDecimalProperty(element, "commercialQuantity") ?? DecimalProperty(element, "quantity"),
            UnitCost = OptionalDecimalProperty(element, "commercialUnitCost") ?? DecimalProperty(element, "unitCost"),
            UnitSale = OptionalDecimalProperty(element, "commercialUnitSale") ?? DecimalProperty(element, "unitSale"),
            SupplierName = StringProperty(element, "supplierName")
        }).ToArray();
        // Pricing is the authority for accepted historical commercial data. A
        // malformed legacy row must not prevent materializing the operational case.
        // Preserve every row verbatim in the immutable snapshot. Only rows that
        // fit the local financial schema are projected; exceptions remain visible
        // as a reconciliation task and cannot be treated as settled costs.
        var rejectedCosts = costs.Where(x => x.RateDetailId == Guid.Empty ||
            x.Description.Length is < 1 or > 500 ||
            x.CurrencyCode.Length != 3 || !x.CurrencyCode.All(char.IsAsciiLetterUpper) ||
            x.Quantity < 0 || x.UnitCost < 0 || x.UnitSale < 0 ||
            x.CostCode.Length is < 1 or > 60).ToArray();
        var projectableCosts = costs.Except(rejectedCosts).ToArray();

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
        var manualCaseId = GuidProperty(accepted, "manualCaseId");
        var requestId = GuidProperty(accepted, "quoteLinkRequestId");
        if (manualCaseId != Guid.Empty || requestId != Guid.Empty)
        {
            if (manualCaseId == Guid.Empty || requestId == Guid.Empty || !historicalNumber.HasValue)
                throw new InvalidOperationException("Manual quote link identity incomplete.");
            var caseLock = "operations-quote-link:" + manualCaseId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({caseLock}, 0));", ct);
            var target = await db.Cases.SingleOrDefaultAsync(x => x.Id == manualCaseId && x.ApertureType == "MANUAL", ct);
            var pending = await db.CaseQuoteLinks.SingleOrDefaultAsync(x => x.Id == requestId && x.CaseId == manualCaseId, ct);
            if (target is null || pending is null || target.AccountId != input.AccountId ||
                target.CompanyId != input.CompanyId || target.Idtra != historicalIdtra ||
                !string.Equals(target.ModalityCode, input.ModalityCode, StringComparison.OrdinalIgnoreCase) ||
                pending.LinkedBy != input.ActorUserId || pending.QuoteOptionId != input.AcceptedOptionId)
                throw new InvalidOperationException("Manual quote link does not match its reserved case.");
            if (pending.Status == "ACCEPTED" && target.SourceQuoteId == input.QuoteId && target.SourceQuoteOptionId == input.AcceptedOptionId)
            {
                if (!processed) { AddPricingInboxReceipt(input.SourceEventId, "pricing.quote.linked.v1"); await db.SaveChangesAsync(ct); }
                await transaction.CommitAsync(ct);
                return new AcceptedQuoteCaseCreatedResult(target.Id, target.Idtra, true);
            }
            if (processed || pending.Status != "PENDING_LINK" || target.SourceQuoteId.HasValue)
                throw new InvalidOperationException("Manual quote link is no longer reserved.");
            target.SourceQuoteId = input.QuoteId;
            target.SourceQuoteOptionId = input.AcceptedOptionId;
            target.UpdatedAtUtc = DateTimeOffset.UtcNow;
            target.Version++;
            pending.QuoteId = input.QuoteId;
            pending.QuoteNumber = input.QuoteNumber;
            pending.IsPrimary = true;
            pending.Status = "ACCEPTED";
            pending.SnapshotVersion = input.CommercialRevision;
            db.AcceptedQuoteSnapshots.Add(new OperationsAcceptedQuoteSnapshot {
                CaseId = target.Id, EventId = input.SourceEventId, QuoteId = input.QuoteId,
                AcceptedOptionId = input.AcceptedOptionId, CommercialRevision = input.CommercialRevision,
                SnapshotJson = input.CommercialSnapshotJson, AcceptedAtUtc = input.AcceptedAtUtc
            });
            // Retain manual opening data, operational assignments, documents and
            // services. Commercial cost lines are a separate immutable snapshot.
            var uniqueCostIds = new HashSet<Guid>();
            foreach (var item in projectableCosts)
                db.CaseQuoteCosts.Add(new OperationsCaseQuoteCost {
                    QuoteLinkId = pending.Id,
                    SourceCostId = item.SourceCostId != Guid.Empty && uniqueCostIds.Add(item.SourceCostId) ? item.SourceCostId : null,
                    Description = item.Description, CostCode = item.CostCode, CurrencyCode = item.CurrencyCode,
                    SupplierName = string.IsNullOrWhiteSpace(item.SupplierName) || item.SupplierName.Length > 200 ? null : item.SupplierName,
                    CostAmount = item.UnitCost * item.Quantity, SaleAmount = item.UnitSale * item.Quantity
                });
            AddPricingInboxReceipt(input.SourceEventId, "pricing.quote.linked.v1");
            OperationsAuditWriter.Case(db, target.Id, null, input.ActorUserId, "operations.quote-link.confirmed", null,
                new { requestId, input.QuoteId, input.AcceptedOptionId, input.CommercialRevision, target.Idtra,
                    sharedQuote = multi, costsPendingReconciliation = rejectedCosts.Length });
            Enqueue("operations.case.updated.v1", new { caseId = target.Id, version = target.Version, quoteId = input.QuoteId });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new AcceptedQuoteCaseCreatedResult(target.Id, target.Idtra, true);
        }

        var siblings = await db.Cases.AsNoTracking()
            .Where(x => x.SourceQuoteId == input.QuoteId)
            .OrderBy(x => x.SequenceNumber)
            .Select(x => new { x.Id, x.Idtra, x.SequenceNumber, x.CompanyId, x.SourceQuoteOptionId })
            .ToArrayAsync(ct);
        if (siblings.Any(x => x.SourceQuoteOptionId != input.AcceptedOptionId || x.CompanyId != input.CompanyId))
            throw new InvalidOperationException("A different commercial option or company already owns this parent QUO.");
        var previous = historicalNumber.HasValue
            ? siblings.FirstOrDefault(x => x.SequenceNumber == historicalNumber.Value)
            : siblings.FirstOrDefault();
        if (historicalNumber.HasValue && previous is null && siblings.Length > 0 &&
            (!multi || siblings.Any(x => !explicitIds.ContainsKey(x.SequenceNumber))))
            throw new InvalidOperationException("Existing QUO IDTRA does not match the verified explicit list; reconciliation required.");

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

        long next;
        if (historicalNumber.HasValue)
        {
            await LockSequenceAsync(db, ct);
            next = historicalNumber.Value;
            if (await db.Cases.AsNoTracking().AnyAsync(c => c.Idtra == historicalIdtra || c.SequenceNumber == next, ct))
                throw new InvalidOperationException("Historical IDTRA is already assigned to another case; reconciliation required.");
            await RaiseSequenceFloorAsync(db, next, ct);
        }
        else next = await NextSequenceAsync("00000", ct);
        var route = ObjectProperty(accepted, "route");
        var cargo = ObjectProperty(accepted, "cargo");
        var providers = ObjectProperty(accepted, "providers");
        var description = CargoDescription(cargo);
        var opening = new {
            source = historicalNumber.HasValue ? "PRICING_HISTORICAL" : "PRICING",
            historicalIdtra = historicalNumber.HasValue ? historicalIdtra : null,
            sharedQuoteIdtras = explicitIds.Values.ToArray(),
            quoteId = input.QuoteId,
            acceptedOptionId = input.AcceptedOptionId,
            commercialRevision = input.CommercialRevision,
            commercialCostReconciliationRequired = rejectedCosts.Length > 0,
            commercialCostReconciliationCount = rejectedCosts.Length,
            shipmentType = input.ModalityCode.ToUpperInvariant(),
            incoterm = StringProperty(accepted, "incoterm"),
            incotermName = StringProperty(accepted, "incotermName"),
            provenance = StringProperty(accepted, "operationType"),
            pickupAddress = StringProperty(accepted, "pickupAddress"),
            requiresPickup = !string.IsNullOrWhiteSpace(StringProperty(accepted, "pickupAddress")),
            warehouseId = StringProperty(accepted, "warehouseId"),
            carrierRoute = StringProperty(accepted, "carrierRoute"),
            transitTime = StringProperty(accepted, "transitTime"),
            quoteReferences = StringProperty(accepted, "otherReferences"),
            executiveName = StringProperty(accepted, "commercialExecutiveName"),
            pol = StringProperty(route, "polName"),
            pod = StringProperty(route, "podName"),
            poe = StringProperty(route, "poeName"),
            cargoDescription = description,
            volumeCbm = DecimalProperty(cargo, "volumeCbm"),
            volumeValue = DecimalProperty(cargo, "volumeCbm"),
            volumeUnit = "CBM",
            quotedWeightKg = DecimalProperty(cargo, "weightKg"),
            estimatedWeightValue = DecimalProperty(cargo, "weightKg"),
            estimatedWeightUnit = "KG",
            estimatedWeightKg = DecimalProperty(cargo, "weightKg"),
            packageCount = DecimalProperty(cargo, "packages"),
            packages = DecimalProperty(cargo, "packages"),
            carrierReference = StringProperty(providers, "carrierName"),
            agentReference = StringProperty(providers, "agentName"),
            agentName = StringProperty(providers, "agentName"),
            lclGroupAgentReference = StringProperty(providers, "lclGroupAgentName"),
            merchantReference = StringProperty(providers, "merchantName"),
            palletCount = OptionalDecimalProperty(cargo, "pallets"),
            chargeableQuantity = OptionalDecimalProperty(cargo, "chargeableQuantity"),
            services = services.Select(item => new {
                serviceId = item.Id, serviceCode = item.Code,
                name = item.Name, source = "QUOTED"
            }).ToArray(),
            equipment = equipment.Select(item => new {
                equipmentType = item.Code, quantity = item.Quantity,
                equipmentId = item.Id, equipmentName = item.Name
            }).ToArray()
        };
        var operation = new OperationsCase
        {
            CompanyId = input.CompanyId, SeriesCode = "00000",
            SequenceNumber = next, Idtra = historicalNumber.HasValue ? historicalIdtra : $"IDTRA{next}",
            AccountId = input.AccountId, ExecutiveUserId = input.ExecutiveUserId,
            SourceQuoteId = input.QuoteId, SourceQuoteOptionId = input.AcceptedOptionId,
            ApertureType = "AUTOMATIC", ModalityCode = input.ModalityCode.Trim().ToUpperInvariant(),
            OriginCountryCode = OperationsOriginRouting.Country(StringProperty(route, "originCountryCode"), StringProperty(route, "polName")),
            DestinationCountryCode = OperationsOriginRouting.Country(StringProperty(route, "destinationCountryCode"), StringProperty(route, "podName")),
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
        foreach (var item in projectableCosts)
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
                SupplierName = string.IsNullOrWhiteSpace(item.SupplierName) || item.SupplierName.Length > 200 ? null : item.SupplierName,
                CostAmount = item.UnitCost * item.Quantity,
                SaleAmount = item.UnitSale * item.Quantity
            });
        }
        db.CaseQuoteLinks.Add(quoteLink);
        if (rejectedCosts.Length > 0)
        {
            db.CaseTasks.Add(new OperationsCaseTask {
                CaseId = operation.Id, CaseStageId = stage.Id,
                Title = "Conciliar rubros comerciales históricos",
                Details = $"{rejectedCosts.Length} rubros no cumplen el contrato local. Todos se conservan en el snapshot inmutable de Pricing. Revisar antes de conciliación financiera.",
                Status = "PENDING", Priority = 1
            });
        }

        if (multi)
            db.CaseTasks.Add(new OperationsCaseTask {
                CaseId = operation.Id, CaseStageId = stage.Id,
                Title = "Conciliar distribución de QUO compartida",
                Details = "La QUO corresponde a varios IDTRA. Los valores comerciales son referencias de la QUO completa, no una distribución individual. Verificar costos y carga antes de conciliación financiera.",
                Status = "PENDING", Priority = 1
            });

        AddPricingInboxReceipt(input.SourceEventId);
        OperationsAuditWriter.Case(db, operation.Id, stage.Id, input.ActorUserId,
            "operations.case.accepted-quote.created", null,
            new { operation.Idtra, input.QuoteId, input.AcceptedOptionId,
                input.CommercialRevision, input.SourceEventId,
                services = services.Length, equipment = equipment.Sum(x => x.Quantity),
                costs = costs.Length, projectedCosts = projectableCosts.Length,
                costsPendingReconciliation = rejectedCosts.Length });
        Enqueue("operations.case.created.v1", new {
            caseId = operation.Id, operation.Idtra, operation.CompanyId,
            quoteId = input.QuoteId, quoteOptionId = input.AcceptedOptionId,
            status = operation.Status, version = operation.Version
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new AcceptedQuoteCaseCreatedResult(operation.Id, operation.Idtra, false);
    }

    private void AddPricingInboxReceipt(Guid eventId, string eventName = "pricing.quote.accepted.v1")
    {
        var receipt = new CustomCodeFramework.Messaging.Inbox.InboxMessage
        {
            EventId = eventId,
            EventType = eventName,
            EventName = eventName,
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

    private static decimal? OptionalDecimalProperty(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDecimal(out var parsed) ? parsed : null;

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
                    .Select(line => {
                        var lower = StringProperty(line, "description");
                        return !string.IsNullOrWhiteSpace(lower)
                            ? lower : StringProperty(line, "Description");
                    })
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

        await db.Entry(stage).ReloadAsync(ct);
        await db.Entry(operation).ReloadAsync(ct);
        if (stage.Status is "COMPLETED" or "SKIPPED" ||
            await db.StageAssignments.AnyAsync(x => x.CaseStageId == stage.Id && x.ReleasedAtUtc == null, ct))
            return null;

        // Routing predicates always derive from persisted case data, not caller-supplied assign parameters.
        var origin = operation.OriginCountryCode;
        var destination = operation.DestinationCountryCode;
        var isOrigin = stage.DepartmentCode == "COORDINACION_ORIGEN";
        if (isOrigin && operation.ApertureType == "AUTOMATIC" && (origin == null || destination == null))
        {
            var snapshotJson = await db.AcceptedQuoteSnapshots.AsNoTracking()
                .Where(x => x.CaseId == operation.Id).Select(x => x.SnapshotJson).SingleOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(snapshotJson))
            {
                using var snapshot = JsonDocument.Parse(snapshotJson);
                var route = ObjectProperty(snapshot.RootElement, "route");
                origin ??= OperationsOriginRouting.Country(StringProperty(route, "originCountryCode"), StringProperty(route, "polName"));
                destination ??= OperationsOriginRouting.Country(StringProperty(route, "destinationCountryCode"), StringProperty(route, "podName"));
            }
        }
        var manualRouting = false;
        string? expectedTeamName = null;
        if (isOrigin && operation.ApertureType == "MANUAL")
        {
            using var opening = JsonDocument.Parse(operation.OpeningDataJson);
            var data = opening.RootElement;
            manualRouting = data.TryGetProperty("openingValidationVersion", out var revision) &&
                revision.ValueKind == JsonValueKind.Number && revision.TryGetInt32(out var version) && version >= 6;
            if (manualRouting)
            {
                origin = OperationsOriginRouting.Country(origin, StringProperty(data, "pol"));
                var lastStop = StringProperty(data, "pod");
                destination = OperationsOriginRouting.Country(destination,
                    string.IsNullOrWhiteSpace(lastStop) ? StringProperty(data, "poe") : lastStop);
                expectedTeamName = OperationsOriginRouting.ManualTeamName(operation.ModalityCode, origin,
                    destination, StringProperty(data, "operationDirection"));
            }
        }
        if (isOrigin)
        {
            operation.OriginCountryCode = origin;
            operation.DestinationCountryCode = destination;
        }
        var rules = await db.AssignmentRules.AsNoTracking()
            .Include(x => x.Team).ThenInclude(x => x.Members)
            .Where(x => x.IsActive && x.Team.IsActive && x.Team.CompanyId == operation.CompanyId &&
                x.DepartmentCode == stage.DepartmentCode &&
                (x.ModalityCode == null || x.ModalityCode == operation.ModalityCode) &&
                (x.OriginCountryCode == null || x.OriginCountryCode == origin) &&
                (x.DestinationCountryCode == null || x.DestinationCountryCode == destination))
            .ToListAsync(ct);

        if (!manualRouting && isOrigin && OperationsOriginRouting.IsSpecialized(operation.ModalityCode))
        {
            // US routes must never fall through to the international pool; unknown countries require review.
            if (origin is null) rules.Clear();
            else if (origin == "US") rules = rules.Where(r => r.OriginCountryCode == "US").ToList();
        }
        if (!manualRouting && isOrigin && rules.Count > 0)
        {
            // Geographic/modality specificity precedes load. Only compare users within the best routing tier.
            int Specificity(OperationsAssignmentRule r) =>
                (r.OriginCountryCode != null ? 4 : 0) + (r.ModalityCode != null ? 2 : 0) + (r.DestinationCountryCode != null ? 1 : 0);
            var best = rules.Max(Specificity);
            rules = rules.Where(r => Specificity(r) == best).ToList();
            var priority = rules.Max(r => r.Priority);
            rules = rules.Where(r => r.Priority == priority).ToList();
        }

        // New manual openings target the standard active team itself. Assignment rules remain
        // unchanged for automatic/historical openings and every subsequent department.
        // Never fall back to an unrelated team when the designated pool is empty.
        var pools = rules.Select(rule => (Team: rule.Team, Priority: rule.Priority)).ToList();
        if (manualRouting)
        {
            pools.Clear();
            if (expectedTeamName is not null)
            {
                var teams = await db.Teams.AsNoTracking().Include(t => t.Members)
                    .Where(t => t.CompanyId == operation.CompanyId && t.DepartmentCode == stage.DepartmentCode &&
                        t.IsActive && t.Name == expectedTeamName).ToListAsync(ct);
                pools.AddRange(teams.Select(team => (Team: team, Priority: 0)));
            }
        }

        var load = await db.StageAssignments.AsNoTracking()
            .Where(x => x.ReleasedAtUtc == null && x.CaseStage.Case.CompanyId == operation.CompanyId &&
                x.CaseStage.Status != "COMPLETED" && x.CaseStage.Status != "SKIPPED")
            .GroupBy(x => x.UserId).Select(g => new { UserId = g.Key, Points = g.Sum(x => x.AssignedPoints) })
            .ToDictionaryAsync(x => x.UserId, x => x.Points, ct);

        var candidates = pools.SelectMany(pool => pool.Team.Members
                .Where(member => member.IsAvailable && member.CapacityPoints > 0)
                .Select(member => new
                {
                    TeamId = pool.Team.Id, member.UserId, pool.Priority,
                    RelativeLoad = (load.GetValueOrDefault(member.UserId) + operation.ComplexityPoints)
                        / member.CapacityPoints
                }))
            .OrderBy(x => x.RelativeLoad).ThenByDescending(x => x.Priority)
            .ThenBy(x => x.UserId).ThenBy(x => x.TeamId).ToList();

        var selected = candidates.FirstOrDefault();
        OperationsAssignmentRecipient? recipient = null;
        if (isOrigin)
        {
            selected = null;
            if (eligibility != null)
                foreach (var candidate in candidates)
                {
                    recipient = await eligibility.GetRecipientAsync(candidate.UserId, ct);
                    if (recipient is not null) { selected = candidate; break; }
                }
        }
        if (selected is null)
        {
            if (isOrigin && !await db.CaseTasks.AnyAsync(t => t.CaseStageId == stage.Id &&
                t.Title == "Pendiente de asignación a Coordinación Origen" && t.Status == "PENDING", ct))
            {
                db.CaseTasks.Add(new OperationsCaseTask { CaseId = operation.Id, CaseStageId = stage.Id,
                    Title = "Pendiente de asignación a Coordinación Origen",
                    Details = manualRouting
                        ? expectedTeamName is null ? "Confirmar ruta y sentido de operación." : $"Revisar disponibilidad y autorización del equipo {expectedTeamName}."
                        : origin is null ? "Confirmar país de origen del tramo operativo." : "Revisar reglas, disponibilidad y autorización de los operativos.",
                    Priority = 1, Status = "PENDING" });
                OperationsAuditWriter.Case(db, operation.Id, stage.Id, input.ActorUserId,
                    "operations.origin.assignment.pending", null, new { origin, operation.ModalityCode, expectedTeamName });
                operation.Version++;
                operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
                Enqueue("operations.case.updated.v1", new { caseId = operation.Id, stageId = stage.Id, version = operation.Version });
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return null;
        }
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
            foreach (var task in await db.CaseTasks.Where(t => t.CaseStageId == stage.Id &&
                t.Title == "Pendiente de asignación a Coordinación Origen" && t.Status == "PENDING").ToListAsync(ct))
                task.Status = "COMPLETED";
            if (!await db.OriginProgress.AnyAsync(x => x.CaseStageId == stage.Id, ct))
                db.OriginProgress.Add(new OperationsOriginProgress {
                    CaseStageId = stage.Id, StatusCode = "ORIGIN_COORDINATION"
                });
            db.OutboxMessages.Add(OperationsOutboxFactory.CreateOriginAssignedNotification(
                operation.Id, operation.Idtra, assignment.Id, assignment.UserId));
            if (System.Net.Mail.MailAddress.TryCreate(recipient?.Email, out var email))
                db.OutboxMessages.Add(OperationsOutboxFactory.CreateOriginAssignedNotification(
                    operation.Id, operation.Idtra, assignment.Id, assignment.UserId, email.Address));
            else
                OperationsAuditWriter.Case(db, operation.Id, stage.Id, input.ActorUserId,
                    "operations.origin.notification.email.unavailable", null, new { assignment.UserId });
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

    public const long MaximumIdtra = 999999999999L;
    public static bool TryHistoricalNumber(string value, out long number)
    {
        number = 0;
        if (value.Length > 32) return false;
        var match = System.Text.RegularExpressions.Regex.Match(value, @"^IDTRA(?:999\.)?([0-9]+)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && long.TryParse(match.Groups[1].Value, out number)
            && number > 0 && number <= MaximumIdtra;
    }

    private static Task LockSequenceAsync(OperationsDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('operations-idtra-global', 0));", ct);

    private static Task RaiseSequenceFloorAsync(OperationsDbContext db, long number, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO operations.operations_case_sequences (id, series_code, last_number, updated_at_utc)
            VALUES ({Guid.NewGuid()}, '00000', {number}, now())
            ON CONFLICT (series_code) DO UPDATE SET
              last_number = GREATEST(operations_case_sequences.last_number, EXCLUDED.last_number), updated_at_utc = now();
            """, ct);

    public static async Task<long> ReadNextIdtraAsync(OperationsDbContext db, CancellationToken ct)
    {
        var last = await db.CaseSequences.AsNoTracking().Where(x => x.SeriesCode == "00000")
            .Select(x => (long?)x.LastNumber).SingleOrDefaultAsync(ct) ?? 0;
        var used = await db.Cases.AsNoTracking().Select(x => (long?)x.SequenceNumber).MaxAsync(ct) ?? 0;
        return checked(Math.Max(last, used) + 1);
    }

    public static async Task<long?> ConfigureNextIdtraAsync(OperationsDbContext db, long next, long expectedNext,
        Guid actor, string reason, CancellationToken ct)
    {
        if (next < 1 || next > MaximumIdtra || reason.Trim().Length is < 3 or > 500)
            throw new ArgumentException("Invalid next IDTRA or reason.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockSequenceAsync(db, ct);
        var current = await ReadNextIdtraAsync(db, ct);
        if (current != expectedNext || next < current) return null;
        await RaiseSequenceFloorAsync(db, next - 1, ct);
        var sequence = await db.CaseSequences.AsNoTracking().SingleAsync(x => x.SeriesCode == "00000", ct);
        OperationsAuditWriter.Entity(db, "OperationsIdtraSequence", sequence.Id, actor,
            "operations.idtra.sequence.configured", new { nextNumber = current }, new { nextNumber = next, reason = reason.Trim() });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return next;
    }

    private async Task<long> NextSequenceAsync(string seriesCode, CancellationToken ct)
    {
        await LockSequenceAsync(db, ct);
        var currentNext = await ReadNextIdtraAsync(db, ct);
        if (currentNext > MaximumIdtra) throw new InvalidOperationException("IDTRA sequence exhausted.");
        await RaiseSequenceFloorAsync(db, currentNext - 1, ct);
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
