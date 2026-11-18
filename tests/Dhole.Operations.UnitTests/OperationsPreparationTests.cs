using Dhole.Operations.Domain.Models;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsPreparationTests
{
    [TestMethod]
    public void CarrierCodesAreExplicitNotInferredFromProviderNames()
    {
        Assert.IsTrue(OperationsCarrierCodes.IsKnown("MAERSK"));
        Assert.IsTrue(OperationsCarrierCodes.IsKnown("YANG_MING"));
        Assert.IsTrue(OperationsCarrierCodes.IsKnown("CMA_CGM"));
        Assert.IsFalse(OperationsCarrierCodes.IsKnown("NOT_A_CARRIER"));
        Assert.IsFalse(OperationsCarrierCodes.IsKnown(null));
    }

    [TestMethod]
    public void UnverifiedConnectionNeverImpliesApiAccess()
    {
        var connection = new OperationsCarrierConnection
        {
            CompanyId = Guid.NewGuid(), CarrierCode = "ONE", Environment = "UAT"
        };
        Assert.AreEqual("REQUESTED", connection.Status);
        Assert.IsNull(connection.LastValidatedAtUtc);
        Assert.AreEqual(0, connection.Capabilities.Count);
        Assert.AreEqual("UNVERIFIED", new OperationsCarrierCapability().Status);
    }

    [TestMethod]
    public void OriginStartsSeparatelyFromCommercialCaseState()
    {
        Assert.AreEqual("ORIGIN_COORDINATION", new OperationsOriginProgress().StatusCode);
        Assert.IsNull(new OperationsOriginProgress().ActualDepartureAtUtc);
    }
}
