using CustomCodeFramework.Messaging.Outbox;
using Dhole.Operations.Persistence.Messaging;

namespace Dhole.Operations.UnitTests;
[TestClass]
public sealed class OutboxMessageTests
{
    [TestMethod]
    public void OpeningNotificationTargetsOnlyAssignedUserAndHasStableBusinessKey()
    {
        var caseId = Guid.NewGuid();
        var assignment = Guid.NewGuid();
        var user = Guid.NewGuid();
        var first = OperationsOutboxFactory.CreateOpeningAssignedNotification(caseId, "IDTRA123", assignment, user);
        var second = OperationsOutboxFactory.CreateOpeningAssignedNotification(caseId, "IDTRA123", assignment, user);
        Assert.AreEqual("notifications.notification.requested", first.EventName);
        using var a = System.Text.Json.JsonDocument.Parse(first.PayloadJson);
        using var b = System.Text.Json.JsonDocument.Parse(second.PayloadJson);
        var payload = a.RootElement;
        Assert.AreEqual("System", payload.GetProperty("channel").GetString());
        Assert.AreEqual(1, payload.GetProperty("recipients").GetArrayLength());
        Assert.AreEqual(user.ToString("D"), payload.GetProperty("recipients")[0].GetProperty("userId").GetString());
        Assert.AreEqual(payload.GetProperty("idempotencyKey").GetString(), b.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.AreEqual($"/operations/cases/{caseId:D}", payload.GetProperty("payload").GetProperty("route").GetString());
    }

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
