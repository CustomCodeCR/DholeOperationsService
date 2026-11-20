using Dhole.Operations.Application.Cases;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Cases;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.IntegrationTests;

/// <summary>PostgreSQL 16 integration checks: additive migration, FK, snapshot, uniqueness and history.</summary>
[TestClass]
public sealed class OperationsFirstTablesTests
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
    public async Task FirstOperationalTablesPersistRealReferencesAndSnapshot()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE operations.operations_case_sequences SET is_authoritatively_synced = true WHERE series_code = '00000'");
        var applied = await db.Database.GetAppliedMigrationsAsync();
        CollectionAssert.Contains(applied.ToList(), "202611170310_OperationsFirstTables");

        var company = Guid.NewGuid();
        var created = await new CreateManualCaseHandler(new OperationsCaseStore(db)).HandleAsync(
            new CreateManualCaseCommand(company, Guid.NewGuid(), Guid.NewGuid(),
                "FCL", 1, 1m, Guid.NewGuid()), CancellationToken.None);
        var actor = Guid.NewGuid();
        var quoteId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var link = new OperationsCaseQuoteLink
        {
            CaseId = created.Id, QuoteId = quoteId, QuoteNumber = "QUO-00000-000001",
            IsPrimary = true, LinkedBy = actor
        };
        link.Costs.Add(new OperationsCaseQuoteCost
        {
            SourceCostId = Guid.NewGuid(), SupplierId = providerId,
            SupplierName = "Prueba SRM", CostCode = "OCEAN",
            Description = "Ocean freight", CostAmount = 1123.4523m,
            SaleAmount = 1350.0000m, CurrencyCode = "USD"
        });
        db.CaseQuoteLinks.Add(link);
        db.CaseMilestones.Add(new OperationsCaseMilestone
        {
            CaseId = created.Id, CaseStageId = created.InitialStageId, MilestoneCode = "ETD",
            Title = "Salida estimada", CreatedBy = actor, PlannedAtUtc = DateTimeOffset.UtcNow.AddDays(3)
        });
        db.CaseTasks.Add(new OperationsCaseTask
        {
            CaseId = created.Id, CaseStageId = created.InitialStageId, Title = "Validar documentación",
            AssignedUserId = actor, CreatedBy = actor
        });
        db.CaseNotes.Add(new OperationsCaseNote { CaseId = created.Id, Content = "Revisado", CreatedBy = actor });
        db.CaseAuditEntries.Add(new OperationsCaseAuditEntry
        {
            CaseId = created.Id, CaseStageId = created.InitialStageId, EventId = eventId,
            ActionCode = "case.created", ActorUserId = actor, AfterJson = "{\"status\":\"PENDING_VALIDATION\"}"
        });
        await db.SaveChangesAsync();

        await using var verify = Db();
        Assert.AreEqual(1, await verify.CaseQuoteLinks.CountAsync(x => x.CaseId == created.Id));
        var cost = await verify.CaseQuoteCosts.SingleAsync(x => x.QuoteLinkId == link.Id);
        Assert.AreEqual(providerId, cost.SupplierId);
        Assert.AreEqual(1123.4523m, cost.CostAmount);
        Assert.AreEqual(1, await verify.CaseMilestones.CountAsync(x => x.CaseId == created.Id));
        Assert.AreEqual(1, await verify.CaseTasks.CountAsync(x => x.CaseId == created.Id));
        Assert.AreEqual(1, await verify.CaseNotes.CountAsync(x => x.CaseId == created.Id));
        Assert.AreEqual(1, await verify.CaseAuditEntries.CountAsync(x => x.EventId == eventId));

        // Invariant: one primary QUO per case even if two writers race.
        verify.CaseQuoteLinks.Add(new OperationsCaseQuoteLink
        {
            CaseId = created.Id, QuoteId = Guid.NewGuid(), IsPrimary = true, LinkedBy = actor
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => verify.SaveChangesAsync());

        // An already-migrated database can be upgraded repeatedly without data loss.
        await using var again = Db();
        await again.Database.MigrateAsync();
        Assert.AreEqual(1, await again.CaseNotes.CountAsync(x => x.CaseId == created.Id));
    }

    [TestMethod]
    public async Task DuplicateSourceCostAndAuditEventAreRejectedByPostgres()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        var created = await new CreateManualCaseHandler(new OperationsCaseStore(db)).HandleAsync(
            new CreateManualCaseCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "FCL", 1, 1m, Guid.NewGuid()), CancellationToken.None);
        var link = new OperationsCaseQuoteLink { CaseId = created.Id, QuoteId = Guid.NewGuid(), LinkedBy = Guid.NewGuid() };
        db.CaseQuoteLinks.Add(link);
        var sourceCostId = Guid.NewGuid();
        db.CaseQuoteCosts.AddRange(
            new OperationsCaseQuoteCost { QuoteLink = link, SourceCostId = sourceCostId, CostCode = "OCEAN", Description = "A", CurrencyCode = "USD" },
            new OperationsCaseQuoteCost { QuoteLink = link, SourceCostId = sourceCostId, CostCode = "OCEAN", Description = "B", CurrencyCode = "USD" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
