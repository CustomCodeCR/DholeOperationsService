using Dhole.Operations.Domain.Models;

namespace Dhole.Operations.UnitTests;
[TestClass]
public sealed class OutboxMessageTests
{
    [TestMethod]
    public void FactoryValidatesNameAndJson()
    {
        Assert.ThrowsExactly<ArgumentException>(() => OutboxMessage.Create("", "{}"));
        Assert.Throws<System.Text.Json.JsonException>(() => OutboxMessage.Create("operations.changed", "{"));
        var message = OutboxMessage.Create("operations.changed", "{\"id\":123}");
        Assert.AreEqual("operations.changed", message.EventName);
        Assert.IsNull(message.PublishedAtUtc);
    }
}
