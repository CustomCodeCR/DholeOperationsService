using System.Text.Json;
using Dhole.Operations.Persistence;

namespace Dhole.Operations.IntegrationTests;

[TestClass]
public sealed class OperationsCommercialSnapshotReaderTests
{
    [TestMethod]
    public void SnapshotRetainsAllRowsAndFinancialScope()
    {
        const string json = """
        {"incoterm":"INC-2026-004","incotermName":"FOB","operationType":"Import","route":{"polName":"Miami, Estados Unidos"},"costLines":[{"description":"Flete","currencyCode":"CUR-2026-001","quantity":2,"unitCost":10,"unitSale":30},{"description":"Otro","quantity":1,"unitCost":5}]}
        """;
        var summary = JsonSerializer.Serialize(OperationsCommercialSnapshotReader.Summary(json));
        StringAssert.Contains(summary, "FOB");
        Assert.DoesNotContain("unitCost", summary);
        var costs = OperationsCommercialSnapshotReader.Costs(json, false);
        Assert.HasCount(2, costs);
        Assert.AreEqual((decimal?)20m, costs[0].CostAmount);
        Assert.IsNull(costs[0].SaleAmount);
        Assert.AreEqual((decimal?)60m, OperationsCommercialSnapshotReader.Costs(json, true)[0].SaleAmount);
    }
}
