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
    public void NeverCombinesOneUserFromTwoTeams()
    {
        var members = new[] {
            new AperturePoolMember(Guid.NewGuid(), Guid.NewGuid(), 10m, 0m, 0),
            new AperturePoolMember(Guid.NewGuid(), Guid.NewGuid(), 10m, 0m, 0)
        };
        Assert.IsNull(OperationsDualPoolScoring.Select(members, 1m));
    }
}
