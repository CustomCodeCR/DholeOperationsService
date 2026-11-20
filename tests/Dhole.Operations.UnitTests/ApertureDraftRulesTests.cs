using Dhole.Operations.Application.Cases;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class ApertureDraftRulesTests
{
    [TestMethod]
    public void DraftAcceptsMissingFieldsButValidationReportsThem()
    {
        var details = ApertureDraftRules.Normalize(new ApertureDraft());
        Assert.IsEmpty(ApertureDraftRules.ValidateDraft(details));
        var missing = ApertureDraftRules.BlockingFields("FCL", details);
        CollectionAssert.Contains(missing.ToArray(), "cargoDescription");
        CollectionAssert.Contains(missing.ToArray(), "equipment");
        CollectionAssert.Contains(missing.ToArray(), "serviceCodes");
    }

    [TestMethod]
    public void PreservesMultipleEquipmentAndExactUnitConversions()
    {
        var data = ApertureDraftRules.Normalize(new ApertureDraft
        {
            WeightValue = 100m, WeightUnit = "LB",
            VolumeValue = 35.315m, VolumeUnit = "CFT",
            Equipment = [
                new() { EquipmentCode = "40HC", Quantity = 2 },
                new() { EquipmentCode = "20GP", Quantity = 1 }
            ]
        });
        Assert.AreEqual(45.359m, data.WeightKg);
        Assert.AreEqual(1.000000m,
            decimal.Round(data.VolumeCbm!.Value, 6));
        Assert.AreEqual(3, data.Equipment.Sum(x => x.Quantity));
        Assert.AreEqual(2, data.Equipment.Count);
    }

    [TestMethod]
    public void FCA_WarehouseAndIMO_RequiredOnlyDuringSubmission()
    {
        var data = ApertureDraftRules.Normalize(new ApertureDraft
        {
            CargoDescription = "Mercancía", TransportTypeCode = "GROUND",
            ShipmentTypeCode = "IMPORT", WeightValue = 100m, WeightUnit = "KG",
            PackageCount = 1, IncotermCode = "FCA",
            ServiceCodes = ["CUSTOMS_CR"], IsImo = true
        });
        var missing = ApertureDraftRules.BlockingFields("LCL", data);
        CollectionAssert.Contains(missing.ToArray(), "warehouseName");
        CollectionAssert.Contains(missing.ToArray(), "imoClassAndUnNumber");
    }

    [TestMethod]
    public void AirDoesNotRequireOceanCarrierAndEmptyContainerRowsAreNotPersisted()
    {
        var data = ApertureDraftRules.Normalize(new ApertureDraft
        {
            CargoDescription = "Piezas", TransportTypeCode = "AIR",
            ShipmentTypeCode = "IMPORT", WeightValue = 10m, WeightUnit = "KG",
            PackageCount = 1, ServiceCodes = ["AIR_FREIGHT"],
            Equipment = [new() { EquipmentCode = "", Quantity = 1 }]
        });
        Assert.IsEmpty(data.Equipment);
        Assert.DoesNotContain("carrierCode", ApertureDraftRules.BlockingFields("AIR", data));
    }
}
