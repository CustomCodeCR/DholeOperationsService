using System.Text.Json;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Cases;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.IntegrationTests;

[TestClass]
public sealed class AcceptedQuoteAperture417Tests
{
    private static string? Connection => Environment.GetEnvironmentVariable("DHOLE_TEST_POSTGRES");
    private static OperationsDbContext NewDb() => new(
        new DbContextOptionsBuilder<OperationsDbContext>().UseNpgsql(Connection!).Options);

    private static string Payload(Guid quote, Guid group, Guid account, Guid executive, Guid actor)
    {
        var detailId = Guid.NewGuid();
        return JsonSerializer.Serialize(new {
            schemaVersion = 1, quoteId = quote, quoteGroupId = group,
            selectedOptionId = quote,
            quoteNumber = "QUO-00000-000672", revisionNumber = 3,
            clientAccountId = account, clientName = "Cliente QA",
            executiveUserId = executive, acceptedBy = actor,
            acceptedAtUtc = DateTime.UtcNow, acceptanceObservations = "Aceptación de alternativa B",
            commercialSnapshot = new {
                ShipmentMode = 1, IncotermCode = "FCA", PolName = "Shanghai",
                PoeName = "Moín", PodName = "San José", TotalWeightKg = 1500m,
                TotalVolumeCbm = 12m, TotalPackages = 10,
                Containers = new object[] {
                    new { ContainerTypeCode = "40HC", ContainerTypeId = Guid.NewGuid(), Quantity = 2 },
                    new { ContainerTypeCode = "20GP", ContainerTypeId = Guid.NewGuid(), Quantity = 1 }
                },
                Services = new[] { new { ServiceName = "Flete internacional" } },
                Details = new[] { new {
                    Id = detailId, CostType = "Freight", Name = "Flete internacional",
                    CurrencyCode = "USD", CostAmount = 150m, SaleAmount = 200m
                }}
            }
        });
    }

    [TestMethod]
    public async Task AcceptedSelectedBGeneratesOneIdtraAndImmutableSnapshot()
    {
        if (string.IsNullOrWhiteSpace(Connection))
            Assert.Inconclusive("DHOLE_TEST_POSTGRES required");
        var company = Guid.NewGuid();
        var group = Guid.NewGuid();
        var selected = Guid.NewGuid();
        var account = Guid.NewGuid();
        var executive = Guid.NewGuid();
        var actor = Guid.NewGuid();
        await using (var db = NewDb()) {
            await db.Database.MigrateAsync();
            db.Teams.Add(new OperationsTeam {
                CompanyId = company, Name = "QA-Aperturas-" + Guid.NewGuid().ToString("N"),
                DepartmentCode = "APERTURAS"
            });
            await db.SaveChangesAsync();
        }
        var payload = Payload(selected, group, account, executive, actor);
        string first;
        await using (var db = NewDb()) {
            first = await new AcceptedQuoteCaseMaterializer(db)
                .ApplyAsync(Guid.NewGuid(), payload, CancellationToken.None);
        }
        await using (var db = NewDb()) {
            var repeatedSameBusiness = await new AcceptedQuoteCaseMaterializer(db)
                .ApplyAsync(Guid.NewGuid(), payload, CancellationToken.None);
            Assert.AreEqual(first, repeatedSameBusiness);
        }
        await using (var db = NewDb()) {
            var @case = await db.Cases.SingleAsync(x => x.SourceQuoteId == selected);
            Assert.AreEqual(first, @case.Idtra);
            Assert.AreEqual(group, @case.SourceQuoteGroupId);
            Assert.AreEqual(selected, @case.SourceQuoteOptionId);
            Assert.AreEqual(account, @case.AccountId);
            Assert.AreEqual(executive, @case.ExecutiveUserId);
            Assert.AreEqual("AUTOMATIC", @case.ApertureType);
            var link = await db.CaseQuoteLinks.Include(x => x.Costs)
                .SingleAsync(x => x.CaseId == @case.Id);
            Assert.AreEqual(3, link.SnapshotVersion);
            Assert.HasCount(1, link.Costs);
            Assert.AreEqual(150m, link.Costs[0].CostAmount);
            using var captured = JsonDocument.Parse(link.SnapshotJson);
            Assert.AreEqual(2, captured.RootElement.GetProperty("Containers")[0]
                .GetProperty("Quantity").GetInt32());
            Assert.AreEqual(1, await db.Cases.CountAsync(x =>
                x.SourceQuoteGroupId == group && x.ApertureType == "AUTOMATIC"));
        }
    }

    [TestMethod]
    public async Task MissingCrmIdentityNeverAllocatesIdtraOrInboxReceipt()
    {
        if (string.IsNullOrWhiteSpace(Connection))
            Assert.Inconclusive("DHOLE_TEST_POSTGRES required");
        await using var db = NewDb();
        await db.Database.MigrateAsync();
        var quote = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var payload = Payload(quote, Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), Guid.NewGuid());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new AcceptedQuoteCaseMaterializer(db).ApplyAsync(eventId, payload, CancellationToken.None));
        Assert.IsFalse(await db.Cases.AnyAsync(x => x.SourceQuoteId == quote));
        Assert.IsFalse(await db.InboxMessages.AnyAsync(x => x.EventId == eventId &&
            x.ConsumerService == AcceptedQuoteCaseMaterializer.ConsumerService));
    }
}
