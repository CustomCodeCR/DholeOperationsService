using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;

namespace Dhole.Operations.IntegrationTests;

[TestClass]
public sealed class OperationsQuoteComparisonTests
{
    [TestMethod]
    public void AcceptedSnapshotIsIndependentOfOperationalChanges()
    {
        var snapshot = new OperationsAcceptedQuoteSnapshot {
            CommercialRevision = 2, SnapshotJson = """{"shipmentMode":"Lcl","incoterm":"FOB","route":{"polName":"Miami","poeName":"Moín","podName":"San José"},"cargo":{"weightKg":100,"volumeCbm":1,"packages":5}}"""
        };
        var operation = new OperationsCase {
            ModalityCode = "LCL", OpeningDataJson = """{"incoterm":"FOB","pol":"Miami","poe":"Moín","pod":"San José","weightValue":220.46226218,"weightUnit":"LB","volumeValue":1,"volumeUnit":"CBM","packageCount":7}"""
        };
        var compare = OperationsQuoteComparison.Compare(operation, snapshot);
        Assert.IsTrue(compare.HasSnapshot);
        Assert.AreEqual("MATCH", compare.Rows.Single(x => x.Field == "weightValue").Status);
        Assert.AreEqual("CHANGED", compare.Rows.Single(x => x.Field == "packageCount").Status);
        Assert.AreEqual("RED", OperationsQuoteComparison.Severity(compare, null));
        Assert.AreEqual("YELLOW", OperationsQuoteComparison.Severity(compare, "PENDING_PRICING"));
        Assert.AreEqual("GREEN", OperationsQuoteComparison.Severity(compare, "RESOLVED_NO_PRICE_CHANGE"));
        operation.OpeningDataJson = operation.OpeningDataJson.Replace("packageCount\":7", "packageCount\":8");
        Assert.AreNotEqual(compare.Fingerprint, OperationsQuoteComparison.Compare(operation, snapshot).Fingerprint);
    }

    [TestMethod]
    public void MissingSnapshotDoesNotBecomeGreen()
    {
        var result = OperationsQuoteComparison.Compare(new OperationsCase(), null);
        Assert.IsFalse(result.HasSnapshot);
        Assert.AreEqual("UNKNOWN", OperationsQuoteComparison.Severity(result, null));
    }
}
