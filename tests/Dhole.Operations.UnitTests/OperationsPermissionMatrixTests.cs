using Dhole.Operations.Api.Endpoints;
using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsPermissionMatrixTests
{
    [TestMethod]
    public void OriginAndAperturasProfilesAreLeastPrivilege()
    {
        var opening = OperationsTeamCatalogEndpoints.OperatorScopes("APERTURAS");
        var origin = OperationsTeamCatalogEndpoints.OperatorScopes("COORDINACION_ORIGEN");
        CollectionAssert.Contains(opening, "operations.aperture.validation.review");
        CollectionAssert.Contains(origin, "operations.origin.update");
        CollectionAssert.DoesNotContain(opening, "operations.origin.update");
        CollectionAssert.DoesNotContain(origin, "operations.aperture.validation.review");
        foreach (var department in new[] { "APERTURAS", "COORDINACION_ORIGEN" })
        {
            var supervisor = OperationsTeamCatalogEndpoints.SupervisorScopes(department);
            CollectionAssert.Contains(supervisor, "operations.audit.read");
            CollectionAssert.DoesNotContain(supervisor, "operations.case.read-all");
            CollectionAssert.DoesNotContain(supervisor, "operations.finance.manage");
            CollectionAssert.DoesNotContain(supervisor, "operations.team.manage");
        }
        Assert.AreEqual(0, OperationsTeamCatalogEndpoints.OperatorScopes("TRANSITO").Length);
    }

    [TestMethod]
    public void StatesRespectModalityAndPickupAndDoNotSimulateDeparture()
    {
        var lcl = OperationsOriginStatusPolicy.Allowed("LCL", "{\"requiresPickup\":true}");
        CollectionAssert.Contains(lcl, "ORIGIN_WAREHOUSE");
        CollectionAssert.Contains(lcl, "CARGO_PICKUP");
        CollectionAssert.DoesNotContain(lcl, "IN_TRANSIT");
        CollectionAssert.DoesNotContain(OperationsOriginStatusPolicy.Allowed("FCL", "{}"), "ORIGIN_WAREHOUSE");
        CollectionAssert.DoesNotContain(OperationsOriginStatusPolicy.Allowed("FTL", "{\"requiresPickup\":false}"), "CARGO_PICKUP");
        CollectionAssert.Contains(OperationsOriginStatusPolicy.Allowed("AIR", "{}"), "BOOKING_READY");
        CollectionAssert.DoesNotContain(OperationsOriginStatusPolicy.Allowed("LCL", "bad json"), "CARGO_PICKUP");
    }
}
