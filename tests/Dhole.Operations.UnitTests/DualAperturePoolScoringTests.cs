using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class DualAperturePoolScoringTests
{
    [TestMethod]
    public void RequiresTwoDistinctUsersInTheSameTeam()
    {
        var team = Guid.NewGuid();
        var only = Guid.NewGuid();
        var members = new[] {
            new AperturePoolMember(team, only, 10m, 0m, 0),
            new AperturePoolMember(team, only, 10m, 0m, 0)
        };
        Assert.IsNull(OperationsDualPoolScoring.Select(members, 2m));
    }

    [TestMethod]
    public void ChoosesTwoDifferentAvailableUsersWithLowestWeightedLoad()
    {
        var team = Guid.NewGuid();
        var busy = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var pair = OperationsDualPoolScoring.Select(new[] {
            new AperturePoolMember(team, busy, 2m, 20m, 0),
            new AperturePoolMember(team, first, 8m, 0m, 0),
            new AperturePoolMember(team, second, 10m, 0m, 0)
        }, 4m);
        Assert.IsNotNull(pair);
        Assert.AreNotEqual(pair.FirstUserId, pair.SecondUserId);
        CollectionAssert.AreEquivalent(new[] { first, second },
            new[] { pair.FirstUserId, pair.SecondUserId });
    }

    [TestMethod]
    public void CountingOnlyActiveFirstReviewsBalancesTwoOperators()
    {
        var team = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var loads = new Dictionary<Guid, decimal> { [first] = 0, [second] = 0 };
        for (var i = 0; i < 10; i++)
        {
            var pair = OperationsDualPoolScoring.Select(loads.Select(x =>
                new AperturePoolMember(team, x.Key, 10m, x.Value, 0)).ToArray(), 2m);
            Assert.IsNotNull(pair);
            loads[pair.FirstUserId]++;
        }
        Assert.AreEqual(5m, loads[first]);
        Assert.AreEqual(5m, loads[second]);
    }

    [TestMethod]
    public void NeverCombinesOneUserFromTwoTeams()
    {
        var members = new[] {
            new AperturePoolMember(Guid.NewGuid(), Guid.NewGuid(), 10m, 0m, 0),
            new AperturePoolMember(Guid.NewGuid(), Guid.NewGuid(), 10m, 0m, 0)
        };
        Assert.IsNull(OperationsDualPoolScoring.Select(members, 1m));
    }
}
