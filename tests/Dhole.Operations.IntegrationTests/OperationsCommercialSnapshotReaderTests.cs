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
}
