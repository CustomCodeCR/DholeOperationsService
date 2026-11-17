using CustomCodeFramework.Messaging.Inbox;
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
            var receipt = new InboxMessage
            {
                ConsumerService = "operations-realtime", SourceService = "DholeOperationsService",
                EventId = eventId, EventType = "operations.changed",
                EventName = "operations.changed",
                Status = InboxMessageStatus.Processed,
                ProcessedAtUtc = DateTime.UtcNow
            };
            db.InboxMessages.Add(receipt);
            db.Entry(receipt).Property("PayloadJson").CurrentValue = "{}";
            await db.SaveChangesAsync();
        }
        await using (var db = new OperationsDbContext(options))
        {
            Assert.IsTrue(await db.InboxMessages.AnyAsync(x =>
                x.ConsumerService == "operations-realtime" && x.EventId == eventId));
        }
    }
}
