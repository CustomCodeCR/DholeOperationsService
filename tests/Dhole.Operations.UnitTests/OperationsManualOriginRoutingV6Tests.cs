using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsManualOriginRoutingV6Tests
{
    [DataRow("FCL", "CN", "CR", "IMPORT", OperationsOriginRouting.FclTeam)]
    [DataRow("LCL", "CN", "CR", "IMPORT", OperationsOriginRouting.LclAirTeam)]
    [DataRow("AIR", "DE", "CR", "IMPORT", OperationsOriginRouting.LclAirTeam)]
    [DataRow("AIR", "US", "CR", "IMPORT", OperationsOriginRouting.UnitedStatesTeam)]
    [DataRow("FCL", "CR", "US", "EXPORT", OperationsOriginRouting.UnitedStatesTeam)]
    [DataRow("FTL", "PA", "CR", "IMPORT", OperationsOriginRouting.CentralAmericaTeam)]
    [DataRow("LTL", "GT", "CR", "IMPORT", OperationsOriginRouting.CentralAmericaTeam)]
    [DataRow("FCL", "CN", "PA", "OTHER", OperationsOriginRouting.TransitTeam)]
    [DataRow("LCL", "US", "PA", "OTHER", OperationsOriginRouting.TransitTeam)]
    [DataRow("FTL", "MX", "CR", "IMPORT", OperationsOriginRouting.FtlTeam)]
    [DataRow("LTL", "MX", "CR", "IMPORT", OperationsOriginRouting.LtlTeam)]
    [TestMethod]
    public void RouteSelectsExpectedPool(string modality, string origin, string destination, string direction, string team)
        => Assert.AreEqual(team, OperationsOriginRouting.ManualTeamName(modality, origin, destination, direction));

    [TestMethod]
    public void UnknownRouteDoesNotFallBack()
    {
        Assert.IsNull(OperationsOriginRouting.ManualTeamName("FCL", null, "CR", "IMPORT"));
        Assert.IsNull(OperationsOriginRouting.ManualTeamName("FCL", "CN", null, "IMPORT"));
        Assert.IsNull(OperationsOriginRouting.ManualTeamName("FCL", "CN", "CR", null));
        Assert.IsNull(OperationsOriginRouting.ManualTeamName("UNKNOWN", "US", "CR", "IMPORT"));
    }
}
