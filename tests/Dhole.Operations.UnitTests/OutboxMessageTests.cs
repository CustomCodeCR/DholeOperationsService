using CustomCodeFramework.Messaging.Outbox;
using Dhole.Operations.Persistence.Messaging;

namespace Dhole.Operations.UnitTests;
[TestClass]
public sealed class OutboxMessageTests
{
    [TestMethod]
    public void FactoryValidatesNameAndJson()
    {
        Assert.ThrowsExactly<ArgumentException>(() => OperationsOutboxFactory.Create("", "{}"));
        Assert.Throws<System.Text.Json.JsonException>(() => OperationsOutboxFactory.Create("operations.changed", "{"));
        var message = OperationsOutboxFactory.Create("operations.changed", "{\"id\":123}");
        Assert.AreEqual("operations.changed", message.EventName);
        Assert.AreEqual("operations.changed", message.EventType);
        Assert.AreEqual("DholeOperationsService", message.SourceService);
        Assert.AreNotEqual(Guid.Empty, message.EventId);
        Assert.AreEqual(OutboxMessageStatus.Pending, message.Status);
        Assert.IsNull(message.ProcessedAtUtc);
    }
}
