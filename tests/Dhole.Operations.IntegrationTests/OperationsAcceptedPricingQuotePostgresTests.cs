using System.Text.Json;
using Dhole.Operations.Application.Cases;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Cases;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.IntegrationTests;

[TestClass]
public sealed class OperationsAcceptedPricingQuotePostgresTests
{
    private static OperationsDbContext Db()
    {
        var connection = Environment.GetEnvironmentVariable("DHOLE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Inconclusive("DHOLE_TEST_POSTGRES is required.");
        return new OperationsDbContext(new DbContextOptionsBuilder<OperationsDbContext>()
            .UseNpgsql(connection).Options);
    }

    private static CreateAcceptedQuoteCaseCommand Quote(Guid eventId,
        Guid quoteId, Guid optionId, Guid accountId, Guid actor, Guid company,
        Guid executive, Guid[] serviceIds)
    {
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            rateId = optionId,
            quoteId,
            acceptedOptionId = optionId,
            acceptedByUserId = actor,
            quoteNumber = "QUO-00000-000672",
            commercialRevision = 7,
            acceptedAtUtc = DateTimeOffset.UtcNow,
            crmAccountId = accountId,
            shipmentMode = "Fcl",
            operationType = "Import",
            incoterm = "FOB",
            route = new { polId = Guid.NewGuid(), polName = "Shanghai",
                poeId = Guid.NewGuid(), poeName = "Moín",
                podId = Guid.NewGuid(), podName = "San José" },
            cargo = new { weightKg = 20000m, volumeCbm = 23.456m,
                packages = 18m, cargoLinesJson = "[{\"description\":\"Carga industrial\"}]" },
            providers = new { carrierId = Guid.NewGuid(), carrierName = "Naviera verificada",
                agentId = Guid.NewGuid(), agentName = "Agente verificado" },
            services = serviceIds.Select((id, i) => new {
                serviceId = id, serviceCode = i == 0 ? "FREIGHT" : "INLAND",
                serviceName = i == 0 ? "Flete" : "Transporte interior"
            }),
            equipment = new[] {
                new {equipmentId=Guid.NewGuid(), equipmentCode="40HC",
                    equipmentName="40 High Cube",quantity=2,unitWeightsKg=new[]{10000m,9000m}},
                new {equipmentId=Guid.NewGuid(), equipmentCode="20GP",
                    equipmentName="20 General Purpose",quantity=1,unitWeightsKg=new[]{1000m}}
            },
            costLines = new[] {
                new {rateDetailId=Guid.NewGuid(),sourceCostId=Guid.NewGuid(),
                    description="Ocean Freight",costType="Freight",currencyCode="USD",
                    quantity=2m,unitCost=100m,unitSale=150m},
                new {rateDetailId=Guid.NewGuid(),sourceCostId=Guid.NewGuid(),
                    description="Inland",costType="Inland",currencyCode="USD",
                    quantity=1m,unitCost=50m,unitSale=80m}
            }
        });
        return new CreateAcceptedQuoteCaseCommand(company,accountId,executive,actor,
            quoteId,optionId,"QUO-00000-000672",7,DateTimeOffset.UtcNow,eventId,payload,"Fcl");
    }

    [TestMethod]
    public async Task ExactlyOneIdtraForRepeatedEventAndSameParentQuote()
    {
        var company=Guid.NewGuid(); var quoteId=Guid.NewGuid();var option=Guid.NewGuid();
        var account=Guid.NewGuid();var actor=Guid.NewGuid();var executive=Guid.NewGuid();
        var eventId=Guid.NewGuid(); var retryEventId=Guid.NewGuid();
        var input=Quote(eventId,quoteId,option,account,actor,company,executive,
            [Guid.NewGuid(),Guid.NewGuid()]);
        AcceptedQuoteCaseCreatedResult created;
        await using(var db=Db())
        {
            await db.Database.MigrateAsync();
            var migrations=await db.Database.GetAppliedMigrationsAsync();
            CollectionAssert.Contains(migrations.ToArray(),"202611232025_OperationsAcceptedQuoteSnapshot435");
            var writer=new OperationsCaseStore(db);
            created=await writer.CreateAcceptedQuoteAsync(input,CancellationToken.None);
            Assert.IsFalse(created.Existing);
            Assert.StartsWith("IDTRA", created.Idtra);
            Assert.DoesNotContain("-", created.Idtra);
            var retry=await writer.CreateAcceptedQuoteAsync(input,CancellationToken.None);
            Assert.AreEqual(created.Idtra,retry.Idtra);
            Assert.IsTrue(retry.Existing);
        }

        await using(var db=Db())
        {
            var writer=new OperationsCaseStore(db);
            var differentEvent=Quote(retryEventId,quoteId,option,account,
                actor,company,executive,[Guid.NewGuid(),Guid.NewGuid()]);
            var retry=await writer.CreateAcceptedQuoteAsync(differentEvent,CancellationToken.None);
            Assert.AreEqual(created.Idtra,retry.Idtra);
            Assert.IsTrue(retry.Existing);
        }

        await using(var db=Db())
        {
            Assert.AreEqual(1,await db.Cases.CountAsync(x=>x.SourceQuoteId==quoteId &&
                x.ApertureType=="AUTOMATIC"));
            Assert.AreEqual(1,await db.AcceptedQuoteSnapshots.CountAsync(x=>x.CaseId==created.Id));
            Assert.AreEqual(2,await db.CaseServiceLines.CountAsync(x=>x.CaseId==created.Id &&
                x.Source=="QUOTED"));
            var lines=await db.CaseEquipmentLines.Where(x=>x.CaseId==created.Id)
                .OrderBy(x=>x.EquipmentCode).ToArrayAsync();
            Assert.HasCount(2,lines);
            Assert.AreEqual(3,lines.Sum(x=>x.Quantity));
            Assert.AreEqual(2,await db.CaseQuoteCosts.CountAsync(x=>x.QuoteLink.CaseId==created.Id));
            Assert.AreEqual(2,await db.InboxMessages.CountAsync(x=>
                x.ConsumerService=="operations-pricing-quote" && x.EventName=="pricing.quote.accepted.v1" &&
                (x.EventId==eventId || x.EventId==retryEventId)));
            Assert.AreEqual("PENDING_VALIDATION",await db.Cases.Where(x=>x.Id==created.Id)
                .Select(x=>x.Status).SingleAsync());
        }
    }

    [TestMethod]
    public async Task AnotherOptionForAcceptedParentIsRejectedBeforeAllocatingNewIdtra()
    {
        var company=Guid.NewGuid();var quoteId=Guid.NewGuid();var account=Guid.NewGuid();
        var actor=Guid.NewGuid();var executive=Guid.NewGuid();
        await using var db=Db();
        await db.Database.MigrateAsync();
        var store=new OperationsCaseStore(db);
        var original=await store.CreateAcceptedQuoteAsync(Quote(Guid.NewGuid(),quoteId,
            Guid.NewGuid(),account,actor,company,executive,[]),CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CreateAcceptedQuoteAsync(Quote(Guid.NewGuid(),quoteId,
                Guid.NewGuid(),account,actor,company,executive,[]),CancellationToken.None));
        Assert.AreEqual(1,await db.Cases.CountAsync(x=>x.SourceQuoteId==quoteId));
        Assert.AreEqual(original.Idtra,await db.Cases.Where(x=>x.SourceQuoteId==quoteId)
            .Select(x=>x.Idtra).SingleAsync());
    }

    [TestMethod]
    public async Task InvalidCrmAccountOrUnknownOptionCannotCreateCase()
    {
        await using var db=Db();
        await db.Database.MigrateAsync();
        var writer=new OperationsCaseStore(db);
        var invalid=Quote(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.Empty,
            Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),[]);
        await Assert.ThrowsAsync<InvalidOperationException>(async ()=>
            await writer.CreateAcceptedQuoteAsync(invalid,CancellationToken.None));
    }

    [TestMethod]
    public async Task HistoricalIdtraPreservesExactValueAndNextNumberIsConfigurable()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        var ct = CancellationToken.None;
        var originalNext = await OperationsCaseStore.ReadNextIdtraAsync(db, ct);
        var historicNumber = checked(originalNext + 20);
        var historicIdtra = $"IDTRA999.{historicNumber}";
        var quoteId = Guid.NewGuid(); var optionId = Guid.NewGuid();
        var actor = Guid.NewGuid(); var account = Guid.NewGuid();
        var company = Guid.NewGuid(); var executive = Guid.NewGuid();
        var input = Quote(Guid.NewGuid(), quoteId, optionId, account, actor, company, executive, []);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(input.CommercialSnapshotJson)!.AsObject();
        payload["historicalIdtra"] = historicIdtra;
        input = input with { CommercialSnapshotJson = payload.ToJsonString() };
        var store = new OperationsCaseStore(db);
        var historical = await store.CreateAcceptedQuoteAsync(input, ct);
        Assert.AreEqual(historicIdtra, historical.Idtra);
        var repeated = await store.CreateAcceptedQuoteAsync(input, ct);
        Assert.AreEqual(historical.Id, repeated.Id);
        Assert.IsTrue(repeated.Existing);
        Assert.AreEqual(historicNumber + 1, await OperationsCaseStore.ReadNextIdtraAsync(db, ct));
        var desired = historicNumber + 10;
        Assert.AreEqual((long?)desired, await OperationsCaseStore.ConfigureNextIdtraAsync(
            db, desired, historicNumber + 1, actor, "Migration cutoff", ct));
        Assert.IsNull(await OperationsCaseStore.ConfigureNextIdtraAsync(
            db, desired - 1, desired, actor, "Unsafe rollback", ct));
        Assert.IsNull(await OperationsCaseStore.ConfigureNextIdtraAsync(
            db, desired + 1, historicNumber + 1, actor, "Stale editor", ct));
        var first = await store.CreateAcceptedQuoteAsync(Quote(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), account, actor, company, executive, []), ct);
        var second = await store.CreateAcceptedQuoteAsync(Quote(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), account, actor, company, executive, []), ct);
        Assert.AreEqual($"IDTRA{desired}", first.Idtra);
        Assert.AreEqual($"IDTRA{desired + 1}", second.Idtra);
    }
}
