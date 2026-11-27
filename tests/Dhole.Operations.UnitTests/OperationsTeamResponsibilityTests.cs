using Dhole.Operations.Domain.Services;
using Dhole.Operations.Api.Endpoints;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsTeamResponsibilityTests
{
    [TestMethod]
    public void LegacyMembershipRemainsTeamScopedAndLeadershipDoesNotEnterPool()
    {
        Assert.AreEqual("TEAM_SUPERVISOR", OperationsTeamResponsibility.Resolve(null, true));
        Assert.AreEqual("OPERATIONS_HEAD", OperationsTeamResponsibility.Resolve(null, false, "OPERATIONS_HEAD"));
        Assert.IsFalse(OperationsTeamResponsibility.Available("OPERATIONS_HEAD", true));
        Assert.IsTrue(OperationsTeamResponsibility.Available("OPERATOR", true));
        Assert.IsFalse(OperationsTeamResponsibility.IsValid("ORIGIN_SUPERVISOR", "APERTURAS"));
        Assert.IsTrue(OperationsTeamResponsibility.IsValid("ORIGIN_SUPERVISOR", "COORDINACION_ORIGEN"));
        Assert.IsFalse(OperationsTeamResponsibility.IsValid("UNRECOGNIZED", "APERTURAS"));
    }

    [TestMethod]
    public void ProfilesSeparateGlobalAndLocalScopeGrants()
    {
        var profiles = OperationsTeamCatalogEndpoints.RoleProfiles("COORDINACION_ORIGEN");
        CollectionAssert.Contains(profiles["ORIGIN_SUPERVISOR"], "operations.origin.read-all");
        CollectionAssert.DoesNotContain(profiles["ORIGIN_SUPERVISOR"], "operations.case.read-all");
        CollectionAssert.Contains(profiles["OPERATIONS_HEAD"], "operations.case.read-all");
        CollectionAssert.DoesNotContain(profiles["OPERATOR"], "operations.case.read-all");
        Assert.IsFalse(OperationsTeamCatalogEndpoints.RoleProfiles("APERTURAS").ContainsKey("ORIGIN_SUPERVISOR"));
    }
}
