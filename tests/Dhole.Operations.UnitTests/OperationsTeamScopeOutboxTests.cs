using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsTeamScopeOutboxTests
{
    [DataRow("operations.team.member.added")]
    [DataRow("operations.team.member.updated")]
    [DataRow("operations.team.member.removed")]
    [DataRow("operations.team.active.changed")]
    [TestMethod]
    public void MembershipMutationsQueueDurablePermissionInvalidation(string action)
    {
        using var db = new OperationsDbContext(new DbContextOptionsBuilder<OperationsDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var team = Guid.NewGuid();
        OperationsAuditWriter.Team(db, team, Guid.NewGuid(), action, null, null);
        var events = db.OutboxMessages.Local.ToArray();
        Assert.HasCount(2, events);
        var invalidation = events.Single(e => e.EventName == "operations.team.permissions.changed.v1");
        using var payload = System.Text.Json.JsonDocument.Parse(invalidation.PayloadJson);
        Assert.AreEqual(team, payload.RootElement.GetProperty("teamId").GetGuid());
        Assert.IsFalse(payload.RootElement.TryGetProperty("scopes", out _));
    }
}
