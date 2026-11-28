using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsOriginProfileTests
{
    private static string[] Codes(OperationsOriginProfile.Profile profile) => profile.Fields.Select(f => f.Code).ToArray();

    [TestMethod]
    public void FclUsesEquipmentAndOnlyShowsGcfForCaldera()
    {
        var profile = OperationsOriginProfile.Resolve("FCL", "MARITIME", false, false);
        CollectionAssert.AreEquivalent(new[] {
            "cargoReadyDate", "etd", "eta", "estimatedTransitDays", "actualDeparture", "shippingOrder",
            "hbl", "booking", "mbl", "departureVessel", "departureVoyage", "imo", "packageCount",
            "volume", "weight", "cargoDescription", "shippers", "consignee", "notify",
            "bookingConfirmedDate", "estimatedLoadingDate", "equipmentLoadedDate", "containerNumber",
            "sealNumber", "overweight", "clientPoReference", "invoiceReference"
        }, Codes(profile));
        CollectionAssert.Contains(Codes(OperationsOriginProfile.Resolve("FCL", "MARITIME", false, true)), "gcf");
    }

    [TestMethod]
    public void LclUsesWarehouseConsolidationAndStackableNotEquipment()
    {
        var profile = OperationsOriginProfile.Resolve("LCL", null, false, false);
        CollectionAssert.AreEquivalent(new[] {
            "cargoReadyDate", "etd", "eta", "estimatedTransitDays", "actualDeparture", "shippingOrder",
            "hbl", "booking", "mbl", "departureVessel", "departureVoyage", "imo", "packageCount",
            "volume", "weight", "cargoDescription", "shippers", "consignee", "notify", "stackable",
            "warehouseEntryNumber", "warehouseEntryDate", "estimatedWarehouseEntryDate", "cargoValue",
            "consolidationType", "consolidationId", "whs", "gcf"
        }, Codes(profile));
    }

    [TestMethod]
    [DataRow("FCL")]
    [DataRow("LCL")]
    public void UnitedStatesExtendsBaseProfileWithoutDuplicatingFields(string modality)
    {
        var normal = Codes(OperationsOriginProfile.Resolve(modality, "MARITIME", false, false));
        var us = Codes(OperationsOriginProfile.Resolve(modality, "MARITIME", true, false));
        CollectionAssert.AreEquivalent(normal.Concat(new[] { "tracking", "carrier" }).ToArray(), us);
        Assert.AreEqual(us.Length, us.Distinct().Count());
    }

    [TestMethod]
    public void AirIsProvisionalAndDoesNotInheritMaritimeOrUsFields()
    {
        var disabled = OperationsOriginProfile.Resolve("AIR", "AIR", true, false);
        Assert.IsTrue(disabled.IsProvisional);
        Assert.HasCount(0, disabled.Fields);
        var preview = OperationsOriginProfile.Resolve("AIR", "AIR", true, false, true);
        var codes = Codes(preview);
        foreach (var field in new[] { "hawb", "mawb", "flight", "dangerousGoods", "volumetricWeight", "stackable" })
            CollectionAssert.Contains(codes, field);
        foreach (var field in new[] { "hbl", "mbl", "departureVessel", "containerNumber", "imo", "tracking", "carrier" })
            CollectionAssert.DoesNotContain(codes, field);
        Assert.AreEqual("UNRESOLVED", OperationsOriginProfile.Resolve("MULTIMODAL", "MARITIME", false, true).Code);
        Assert.AreEqual("UNRESOLVED", OperationsOriginProfile.Resolve("FCL", "AIR", false, true).Code);
    }

    [TestMethod]
    public void CalderaRequiresKnownCatalogIdentityNotDisplayName()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PORT-42" };
        var textOnly = OperationsCargoMeasurements.Parse("""{"pol":"Caldera, Costa Rica"}""");
        Assert.IsFalse(OperationsOriginProfile.IsCaldera(textOnly, ids));
        var catalog = OperationsCargoMeasurements.Parse("""{"poeCatalogId":"port-42"}""");
        Assert.IsTrue(OperationsOriginProfile.IsCaldera(catalog, ids));
        Assert.IsFalse(OperationsOriginProfile.IsCaldera(catalog, new HashSet<string>()));
    }
}
