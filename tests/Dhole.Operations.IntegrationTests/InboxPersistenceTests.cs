using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.IntegrationTests;
[TestClass]
public sealed class InboxPersistenceTests
{
    [TestMethod]
    public async Task InboxCompositeIdempotencyKeyPersists()
    {
        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var eventId = Guid.NewGuid();
        await using (var db = new OperationsDbContext(options))
        {
            db.InboxReceipts.Add(new InboxReceipt
            {
                ConsumerName = "operations-realtime", EventId = eventId,
                EventName = "operations.changed", PayloadJson = "{}"
            });
            await db.SaveChangesAsync();
        }
        await using (var db = new OperationsDbContext(options))
        {
            Assert.IsTrue(await db.InboxReceipts.AnyAsync(x =>
                x.ConsumerName == "operations-realtime" && x.EventId == eventId));
        }
    }
}
