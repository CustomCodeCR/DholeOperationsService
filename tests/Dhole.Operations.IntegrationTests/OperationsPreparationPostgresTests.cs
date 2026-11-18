using Dhole.Operations.Application.Cases;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Cases;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.IntegrationTests;

[TestClass]
public sealed class OperationsPreparationPostgresTests
{
    private static OperationsDbContext Db()
    {
        var connection = Environment.GetEnvironmentVariable("DHOLE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Inconclusive("DHOLE_TEST_POSTGRES is required.");
        return new OperationsDbContext(new DbContextOptionsBuilder<OperationsDbContext>()
            .UseNpgsql(connection).Options);
    }

    [TestMethod]
    public async Task PreparationTablesAreAdditiveAndPersistExternalReferences()
    {
        Guid caseId, stageId, providerId, connectionId;
        var eventId = Guid.NewGuid();
        await using (var db = Db())
        {
            await db.Database.MigrateAsync();
            var applied = await db.Database.GetAppliedMigrationsAsync();
            CollectionAssert.Contains(applied.ToList(), "202611172230_OperationsPreparationV12");

            var created = await new CreateManualCaseHandler(new OperationsCaseStore(db)).HandleAsync(
                new CreateManualCaseCommand(Guid.NewGuid(), "00000", Guid.NewGuid(),
                    Guid.NewGuid(), "FCL", 1, 1m, Guid.NewGuid()), CancellationToken.None);
            caseId = created.Id;
            var stage = new OperationsCaseStage
            {
                CaseId = caseId, DepartmentCode = "COORDINACION_ORIGEN", Status = "IN_PROGRESS"
            };
            stageId = stage.Id;
            db.CaseStages.Add(stage);
            db.OriginProgress.Add(new OperationsOriginProgress
            {
                CaseStageId = stageId, StatusCode = "IN_TRANSIT",
                DepartureEventId = eventId, ActualDepartureAtUtc = DateTimeOffset.UtcNow,
                EvidenceStorageFileId = Guid.NewGuid(), DepartureSource = "MANUAL"
            });
            db.InternalNotes.Add(new OperationsInternalNote
            {
                CaseId = caseId, CaseStageId = stageId, CreatedBy = Guid.NewGuid(),
                Body = "Información confidencial"
            });
            providerId = Guid.NewGuid();
            var connection = new OperationsCarrierConnection
            {
                CompanyId = Guid.NewGuid(), CarrierCode = "MAERSK",
                SrmProviderId = providerId, Environment = "UAT",
                Capabilities = [new OperationsCarrierCapability
                {
                    ProductCode = "TRACKING", CapabilityCode = "TRACKING_READ",
                    Status = "UNVERIFIED"
                }]
            };
            connectionId = connection.Id;
            db.CarrierConnections.Add(connection);
            await db.SaveChangesAsync();
        }

        await using (var verify = Db())
        {
            Assert.AreEqual(1, await verify.OriginProgress.CountAsync(x =>
                x.CaseStageId == stageId && x.DepartureEventId == eventId));
            Assert.AreEqual(1, await verify.InternalNotes.CountAsync(x => x.CaseId == caseId));
            var carrier = await verify.CarrierConnections.Include(x => x.Capabilities)
                .SingleAsync(x => x.Id == connectionId);
            Assert.AreEqual(providerId, carrier.SrmProviderId);
            Assert.AreEqual("REQUESTED", carrier.Status);
            Assert.AreEqual("UNVERIFIED", carrier.Capabilities.Single().Status);

            // A second event with the same source ID cannot duplicate a departure.
            var otherStage = new OperationsCaseStage
            {
                CaseId = caseId, DepartmentCode = "TRANSITO", Status = "READY"
            };
            verify.CaseStages.Add(otherStage);
            verify.OriginProgress.Add(new OperationsOriginProgress
            {
                CaseStage = otherStage, StatusCode = "IN_TRANSIT",
                DepartureEventId = eventId
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => verify.SaveChangesAsync());
        }
    }
}
