using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.IntegrationTests;

[TestClass]
public sealed class ManualOpeningDraft431Tests
{
    [TestMethod]
    public async Task IncompleteDraftPersistsWithoutAccountTeamOrIdtra()
    {
        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var id = Guid.NewGuid();
        var owner = Guid.NewGuid();
        await using (var db = new OperationsDbContext(options))
        {
            db.ManualOpeningDrafts.Add(new OperationsManualOpeningDraft
            {
                Id = id, UserId = owner,
                PayloadJson = """{"form":{"incoterm":"FCA"},"step":1}"""
            });
            await db.SaveChangesAsync();
        }
        await using (var db = new OperationsDbContext(options))
        {
            var saved = await db.ManualOpeningDrafts.SingleAsync(x => x.Id == id);
            Assert.AreEqual(owner, saved.UserId);
            StringAssert.Contains(saved.PayloadJson, "FCA");
            Assert.IsFalse(await db.Cases.AnyAsync());
            Assert.IsFalse(await db.CaseSequences.AnyAsync());
        }
    }
}
