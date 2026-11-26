using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsManualOpeningBasicsV5Tests
{
    private const string Lcl = """
        {"openingValidationVersion":5,"transportType":"MARITIME","shipmentType":"LCL",
         "operationDirection":"IMPORT","pol":"Shanghai","poe":"Moín",
         "polTerminalType":"CY","poeTerminalType":"CY","incoterm":"EXW",
         "estimatedWeightValue":"100","estimatedWeightUnit":"KG","volumeValue":"2","volumeUnit":"CBM",
         "packageCount":"4","warehouseAddress":"Shanghai warehouse","whsName":"Shanghai warehouse",
         "services":["service-id"],"serviceSelections":[{"id":"service-id"}],"equipment":[]}
        """;
    private const string Fcl = """
        {"openingValidationVersion":5,"transportType":"MARITIME","shipmentType":"FCL",
         "operationDirection":"IMPORT","pol":"Shanghai","poe":"Moín",
         "polTerminalType":"CY","poeTerminalType":"CY","incoterm":"FCA",
         "estimatedWeightValue":"100","estimatedWeightUnit":"KG","volumeValue":"2","volumeUnit":"CBM",
         "packageCount":"4","carrierReference":"Carrier",
         "services":["service-id"],"serviceSelections":[{"id":"service-id"}],
         "equipment":[{"equipmentType":"40HC","quantity":1}]}
        """;

    [TestMethod]
    public void ConsolidatedOpeningRequiresWarehouseButNotOriginPlanning()
    {
        CollectionAssert.AreEqual(Array.Empty<string>(), OperationsOpeningDraftPolicy.MissingFields("LCL", Lcl, true));
        CollectionAssert.Contains(OperationsOpeningDraftPolicy.MissingFields("LCL", Lcl.Replace("\"whsName\":\"Shanghai warehouse\"", "\"whsName\":\"\""), true), "whsName");
        CollectionAssert.Contains(OperationsOpeningDraftPolicy.MissingFields("LCL", Lcl.Replace("\"packageCount\":\"4\"", "\"packageCount\":\"1.5\""), true), "packageCount");
    }

    [TestMethod]
    public void FullContainerRequiresCarrierNotWarehouseAndKeepsMeasurements()
    {
        CollectionAssert.AreEqual(Array.Empty<string>(), OperationsOpeningDraftPolicy.MissingFields("FCL", Fcl, true));
        CollectionAssert.Contains(OperationsOpeningDraftPolicy.MissingFields("FCL", Fcl.Replace("\"carrierReference\":\"Carrier\"", "\"carrierReference\":\"\""), true), "carrierReference");
        CollectionAssert.Contains(OperationsOpeningDraftPolicy.MissingFields("FCL", Fcl.Replace("\"volumeValue\":\"2\"", "\"volumeValue\":\"\""), true), "volume");
    }

    [TestMethod]
    public void V6FcaFullContainerRequiresWarehouseWithoutChangingV5()
    {
        var v6 = Fcl.Replace("\"openingValidationVersion\":5", "\"openingValidationVersion\":6");
        CollectionAssert.Contains(OperationsOpeningDraftPolicy.MissingFields("FCL", v6, true), "whsName");
        var complete = v6.Replace("\"carrierReference\":\"Carrier\"", "\"carrierReference\":\"Carrier\",\"warehouseAddress\":\"Origin warehouse\",\"whsName\":\"Origin warehouse\"");
        CollectionAssert.AreEqual(Array.Empty<string>(), OperationsOpeningDraftPolicy.MissingFields("FCL", complete, true));
        var cpt = v6.Replace("\"incoterm\":\"FCA\"", "\"incoterm\":\"CPT\"");
        CollectionAssert.AreEqual(Array.Empty<string>(), OperationsOpeningDraftPolicy.MissingFields("FCL", cpt, true));
    }

    [DataRow("LCL", "EXW", true)]
    [DataRow("LTL", "CPT", true)]
    [DataRow("FCL", "FCA", true)]
    [DataRow("FTL", "FCA", true)]
    [DataRow("FCL", "FOB", false)]
    [DataRow("FTL", "CPT", false)]
    [DataRow("AIR", "FCA", false)]
    [TestMethod]
    public void V6WarehouseMatrix(string shipment, string term, bool required)
        => Assert.AreEqual(required, OperationsOpeningDraftPolicy.RequiresManualWarehouse(shipment, term, 6));

    [TestMethod]
    public void V7RequiresWarehouseAndAgentCatalogIdsWhileV6RemainsCompatible()
    {
        var v7 = Lcl.Replace("\"openingValidationVersion\":5", "\"openingValidationVersion\":7");
        CollectionAssert.Contains(OperationsOpeningDraftPolicy.MissingFields("LCL", v7, true), "whsName");
        CollectionAssert.Contains(OperationsOpeningDraftPolicy.MissingFields("LCL", v7, true), "agentReference");
        var complete = v7.Replace("\"warehouseAddress\":\"Shanghai warehouse\"",
            "\"warehouseAddress\":\"Shanghai warehouse\",\"warehouseId\":\"11111111-1111-4111-8111-111111111111\",\"agentId\":\"22222222-2222-4222-8222-222222222222\",\"agentReference\":\"Agent A\"");
        CollectionAssert.AreEqual(Array.Empty<string>(), OperationsOpeningDraftPolicy.MissingFields("LCL", complete, true));
        var fields = OperationsOpeningDraftPolicy.EvaluateFields("LCL", complete, true);
        Assert.AreEqual(1, fields.Count(field => field.FieldCode == "agentReference"));
        CollectionAssert.AreEqual(Array.Empty<string>(), OperationsOpeningDraftPolicy.MissingFields("LCL",
            Lcl.Replace("\"openingValidationVersion\":5", "\"openingValidationVersion\":6"), true));
    }

    [DataRow("{\"agentId\":\"22222222-2222-4222-8222-222222222222\"}", true)]
    [DataRow("{\"agentId\":\"33333333-3333-4333-8333-333333333333\"}", false)]
    [DataRow("{\"agentName\":\"Same name\"}", false)]
    [DataRow("{}", false)]
    [DataRow("invalid", false)]
    [TestMethod]
    public void WarehouseOwnershipUsesExplicitConfigGuid(string metadata, bool expected)
        => Assert.AreEqual(expected, Dhole.Operations.Api.Integrations.OperationsOpeningCatalogValidator.WarehouseBelongsToAgent(
            metadata, "22222222-2222-4222-8222-222222222222"));
}
