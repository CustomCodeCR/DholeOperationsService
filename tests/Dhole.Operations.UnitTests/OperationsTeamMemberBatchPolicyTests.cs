using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsTeamMemberBatchPolicyTests
{
    [TestMethod]
    public void ValidUniqueBatchIsAccepted()
    {
        Assert.IsNull(OperationsTeamMemberBatchPolicy.Validate(
            [Guid.NewGuid(), Guid.NewGuid()], 10m));
    }

    [TestMethod]
    public void EmptyDuplicateAndInvalidMembersAreRejected()
    {
        var userId = Guid.NewGuid();
        Assert.AreEqual("TEAM_BATCH_SIZE_INVALID",
            OperationsTeamMemberBatchPolicy.Validate([], 10m));
        Assert.AreEqual("TEAM_BATCH_SIZE_INVALID",
            OperationsTeamMemberBatchPolicy.Validate(
                Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray(), 10m));
        Assert.AreEqual("TEAM_BATCH_DUPLICATE_USER",
            OperationsTeamMemberBatchPolicy.Validate([userId, userId], 10m));
        Assert.AreEqual("TEAM_BATCH_USER_INVALID",
            OperationsTeamMemberBatchPolicy.Validate([Guid.Empty], 10m));
    }

    [TestMethod]
    public void InvalidCapacityIsRejected()
    {
        Assert.AreEqual("TEAM_BATCH_CAPACITY_INVALID",
            OperationsTeamMemberBatchPolicy.Validate([Guid.NewGuid()], 0m));
        Assert.AreEqual("TEAM_BATCH_CAPACITY_INVALID",
            OperationsTeamMemberBatchPolicy.Validate([Guid.NewGuid()], 10000.01m));
    }
}
