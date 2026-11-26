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
}
