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
    [TestMethod]
    public void CanonicalAmountsAndMetadataRespectFinancialScope()
    {
        const string json = """
            {"includes":"Flete","costLines":[{"rateDetailId":"original-row","quantity":0.118,
              "unitCost":8,"unitSale":10,"commercialQuantity":1,"commercialUnitCost":8,
              "commercialUnitSale":10,"commercialCostSubtotal":8,"commercialSaleSubtotal":10,
              "commercialTaxAmount":1.3,"commercialSaleTotal":11.3,"chargeBasis":"PerCbm",
              "costDetailType":"OriginCharge","supplierName":"Proveedor registrado"}]}
            """;
        var limited = OperationsCommercialSnapshotReader.Costs(json, false).Single();
        Assert.AreEqual("original-row", limited.Id);
        Assert.AreEqual((decimal?)1m, limited.Quantity);
        Assert.AreEqual((decimal?)8m, limited.CostAmount);
        Assert.AreEqual("PerCbm", limited.ChargeBasis);
        Assert.AreEqual("Proveedor registrado", limited.SupplierName);
        Assert.IsNull(limited.SaleAmount);
        Assert.IsNull(limited.TaxAmount);
        Assert.IsNull(limited.SaleTotal);
        var authorized = OperationsCommercialSnapshotReader.Costs(json, true).Single();
        Assert.AreEqual((decimal?)10m, authorized.SaleAmount);
        Assert.AreEqual((decimal?)11.3m, authorized.SaleTotal);
    }

    [TestMethod]
    public void SnapshotExposesOnlyDistinctValidStorageReferences()
    {
        var id = Guid.NewGuid();
        var snapshot = JsonSerializer.Serialize(new { documentStorageIds = new[] { id.ToString(), id.ToString(), "invalid", Guid.Empty.ToString() } });
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(OperationsCommercialSnapshotReader.Summary(snapshot)));
        var documents = result.RootElement.GetProperty("supportDocuments");
        Assert.AreEqual(1, documents.GetArrayLength());
        Assert.AreEqual(id, documents[0].GetProperty("storageId").GetGuid());
    }

    [TestMethod]
    public void OriginProjectionOnlyReturnsExplicitStableOwnedRowsAndNoFinancialColumns()
    {
        var origin = Guid.NewGuid();
        var freight = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var unowned = Guid.NewGuid();
        var duplicate = Guid.NewGuid();
        var incomplete = Guid.NewGuid();
        object Line(Guid id, string type, decimal? amount = 1800m) => new {
            rateDetailId = id, description = "Concept", costDetailType = type,
            currencyCode = "USD", chargeBasis = "PerShipment", commercialQuantity = 1m,
            commercialCostSubtotal = amount, commercialSaleSubtotal = 2000m,
            commercialTaxAmount = 260m, commercialSaleTotal = 2260m
        };
        var json = JsonSerializer.Serialize(new { costLines = new object[] {
            Line(origin, "OriginCharge"), Line(freight, "Freight"), Line(agent, "AgentCharge"),
            Line(destination, "DestinationCharge"), Line(unowned, "Freight"),
            Line(duplicate, "OriginCharge"), Line(duplicate, "DestinationCharge"),
            Line(incomplete, "OriginCharge", null),
            new { description = "Missing stable ID", costDetailType = "OriginCharge",
                currencyCode = "USD", chargeBasis = "PerShipment", commercialQuantity = 1m, commercialCostSubtotal = 9m }
        } });
        var allowed = new HashSet<Guid> { origin, freight, agent, destination, duplicate, incomplete };
        var rows = OperationsCommercialSnapshotReader.OriginCosts(json, allowed);
        CollectionAssert.AreEquivalent(new[] { origin, freight, agent }, rows.Select(x => x.Id).ToArray());
        Assert.IsTrue(rows.All(x => x.Status == "PENDING" && x.QuotedAmount == 1800m));
        var serialized = JsonSerializer.Serialize(rows);
        foreach (var key in new[] { "Sale", "Tax", "Margin", "Utility", "Subtotal", "Total" })
            Assert.IsFalse(serialized.Contains(key, StringComparison.OrdinalIgnoreCase), key);
        Assert.HasCount(0, OperationsCommercialSnapshotReader.OriginCosts(json, new HashSet<Guid>()));
    }

    [TestMethod]
    public void HistoricalQuoteAllocationFailsClosedWhenIdtraIsAmbiguous()
    {
        const string idtra = "IDTRA123";
        Assert.IsTrue(OperationsCommercialSnapshotReader.HasUnambiguousHistoricalIdtra("{}", idtra));
        Assert.IsTrue(OperationsCommercialSnapshotReader.HasUnambiguousHistoricalIdtra(
            """{"historicalIdtras":["IDTRA123"],"historicalIdtra":"IDTRA123"}""", idtra));
        Assert.IsFalse(OperationsCommercialSnapshotReader.HasUnambiguousHistoricalIdtra(
            """{"historicalIdtras":["IDTRA123","IDTRA456"]}""", idtra));
        Assert.IsFalse(OperationsCommercialSnapshotReader.HasUnambiguousHistoricalIdtra(
            """{"historicalIdtra":"IDTRA456"}""", idtra));
        Assert.IsFalse(OperationsCommercialSnapshotReader.HasUnambiguousHistoricalIdtra(
            """{"historicalIdtras":"IDTRA123"}""", idtra));
        Assert.IsFalse(OperationsCommercialSnapshotReader.HasUnambiguousHistoricalIdtra(
            """{"historicalIdtras":[]}""", idtra));
        Assert.IsFalse(OperationsCommercialSnapshotReader.HasUnambiguousHistoricalIdtra("{", idtra));
    }
}
