using Dhole.Operations.Api.Endpoints;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsManualOpeningValidation20261125Tests
{
    private const string Air = """
        {"transportType":"AIR","shipmentType":"AIR","operationDirection":"IMPORT",
         "cargoDescription":"Piezas","pol":"SZX","poe":"SJO","pod":"San José",
         "polTerminalType":"APT","poeTerminalType":"APT","podTerminalType":"SD",
         "incoterm":"FCA","whsName":"WHS","estimatedWeightValue":"100","estimatedWeightUnit":"KG",
         "lengthCm":"50","widthCm":"40","heightCm":"30","volumeValue":"1","volumeUnit":"CBM","equipment":[]}
        """;

    private static string[] Missing(string json, string mode = "AIR") =>
        OperationsOpeningDraftValidator.MissingFields(mode, json, manualOpening: true);

    [TestMethod]
    public void ValidAirOpeningHasNoBlockers() => CollectionAssert.AreEqual(Array.Empty<string>(), Missing(Air));

    [TestMethod]
    public void AirRejectsSeaTerminalsAndFullContainers()
    {
        var invalid = Air.Replace("APT", "CY").Replace("\"equipment\":[]", "\"equipment\":[{\"equipmentType\":\"40HC\",\"quantity\":1}]");
        var missing = Missing(invalid);
        CollectionAssert.Contains(missing, "equipment");
        CollectionAssert.Contains(missing, "polTerminalType");
        CollectionAssert.Contains(missing, "poeTerminalType");
        CollectionAssert.Contains(Missing(Air.Replace("FCA", "FOB")), "incoterm");
        CollectionAssert.Contains(Missing(Air, "FCL"), "shipmentType");
    }

    [TestMethod]
    public void NewAirOpeningRequiresEstimatedWeightVolumeAndDimensions()
    {
        var missing = Missing(Air.Replace("\"100\"", "\"0\"").Replace("\"50\"", "\"0\"").Replace("\"1\"", "\"0\""));
        CollectionAssert.Contains(missing, "estimatedWeight");
        CollectionAssert.Contains(missing, "dimensions");
        CollectionAssert.Contains(missing, "volume");
    }

    [TestMethod]
    public void VersionThreeAirDoesNotRequirePodAndFcaRequiresWarehouse()
    {
        var air = Air.Replace("\"pod\":\"San José\",", "\"openingValidationVersion\":3,")
            .Replace("\"podTerminalType\":\"SD\",", "")
            .Replace("\"whsName\":\"WHS\"", "\"whsName\":\"WHS\",\"warehouseId\":\"id\"");
        CollectionAssert.AreEqual(Array.Empty<string>(), Missing(air));
        CollectionAssert.Contains(Missing(air.Replace("\"warehouseId\":\"id\"", "\"requiresPickup\":true")), "whsName");
        CollectionAssert.Contains(Missing(air.Replace("\"openingValidationVersion\":3", "\"openingValidationVersion\":3,\"pod\":\"Invalid\"")), "pod");
        CollectionAssert.Contains(Missing(air.Replace("FCA", "EXW")), "pickupLocations");
    }

    [TestMethod]
    public void VersionThreeRoadExwCanCompleteWithoutPickupContactOrWindow()
    {
        const string opening = """
            {"openingValidationVersion":3,"transportType":"ROAD","shipmentType":"FTL",
             "operationDirection":"EXPORT","cargoDescription":"Piezas","pol":"San José",
             "poe":"Ciudad de Guatemala","polTerminalType":"SD","poeTerminalType":"SD",
             "incoterm":"EXW","requiresPickup":true,"pickupAddress":"San José",
             "pickupLocations":[{"address":"San José","cargoCondition":"NationalizedCargo"}],
             "estimatedWeightValue":"100","estimatedWeightUnit":"KG",
             "equipment":[{"equipmentType":"Furgón 48","quantity":1}]}
            """;
        CollectionAssert.AreEqual(Array.Empty<string>(), Missing(opening, "FTL"));
        var fields = OperationsOpeningDraftValidator.EvaluateFields("FTL", opening);
        Assert.IsFalse(fields.Any(f => f.FieldCode is "pickupContact" or "pickupWindow"));
        CollectionAssert.Contains(Missing(opening.Replace("\"pickupAddress\":\"San José\"", "\"pickupAddress\":\"\""), "FTL"), "pickupAddress");
    }

    [TestMethod]
    public void ExwImportCannotSkipPickupAndFcaPickupDoesNotRequireWarehouse()
    {
        var missing = Missing(Air.Replace("FCA", "EXW"));
        CollectionAssert.Contains(missing, "pickupAddress");
        CollectionAssert.DoesNotContain(missing, "pickupContact");
        CollectionAssert.DoesNotContain(missing, "pickupWindow");
        var pickup = Air.Replace("\"whsName\":\"WHS\"", "\"requiresPickup\":true,\"pickupAddress\":\"Address\",\"pickupContact\":\"Contact\",\"pickupWindow\":\"Monday\"");
        CollectionAssert.AreEqual(Array.Empty<string>(), Missing(pickup));
    }
}
