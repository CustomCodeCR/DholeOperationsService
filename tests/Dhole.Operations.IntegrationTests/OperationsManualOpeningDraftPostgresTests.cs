using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.IntegrationTests;

[TestClass]
public sealed class OperationsManualOpeningDraftPostgresTests
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
    public async Task IncompleteDraftPersistsWithoutAccountTeamOrConsumingIdtra()
    {
        var company = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var id = Guid.NewGuid();
        var savedJson = """
            {"version":2,"savedAt":1795485600000,"form":{"incoterm":"FCA","equipment":[]}}
            """;

        await using (var db = Db())
        {
            await db.Database.MigrateAsync();
            var applied = await db.Database.GetAppliedMigrationsAsync();
            CollectionAssert.Contains(applied.ToList(), "202611231900_OperationsManualOpeningDrafts434");
            db.ManualOpeningDrafts.Add(new OperationsManualOpeningDraft
            {
                Id = id, CompanyId = company, CreatedBy = actor, PayloadJson = savedJson
            });
            await db.SaveChangesAsync();
        }

        await using (var db = Db())
        {
            var draft = await db.ManualOpeningDrafts.SingleAsync(x =>
                x.CompanyId == company && x.CreatedBy == actor);
            Assert.AreEqual(id, draft.Id);
            using (var payload = System.Text.Json.JsonDocument.Parse(draft.PayloadJson))
                Assert.AreEqual("FCA", payload.RootElement.GetProperty("form").GetProperty("incoterm").GetString());
            Assert.AreEqual(1L, draft.Version);
            Assert.AreEqual(0, await db.Cases.CountAsync(x => x.CompanyId == company));

            // No second "current" draft for the same company and user.
            db.ManualOpeningDrafts.Add(new OperationsManualOpeningDraft
            {
                CompanyId = company, CreatedBy = actor, PayloadJson = "{}"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [TestMethod]
    public async Task DraftsAreIsolatedAcrossUsersAndCompanies()
    {
        var actor = Guid.NewGuid();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        await using var db = Db();
        await db.Database.MigrateAsync();
        db.ManualOpeningDrafts.AddRange(
            new OperationsManualOpeningDraft
            {
                CompanyId = companyA, CreatedBy = actor, PayloadJson = "{\"step\":1}"
            },
            new OperationsManualOpeningDraft
            {
                CompanyId = companyB, CreatedBy = actor, PayloadJson = "{\"step\":2}"
            });
        await db.SaveChangesAsync();
        Assert.AreEqual(2, await db.ManualOpeningDrafts.CountAsync(x => x.CreatedBy == actor));
    }
}
