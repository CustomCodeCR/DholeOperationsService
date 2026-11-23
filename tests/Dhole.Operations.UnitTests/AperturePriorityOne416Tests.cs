using Dhole.Operations.Api.Endpoints;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class AperturePriorityOne416Tests
{
    private const string Base = """
        {"cargoDescription":"Piezas", "pol":"Shanghai",
         "pod":"San José", "incoterm":"FOB"}
        """;

    [TestMethod]
    public void FcaAlwaysRequiresWarehouseEvenForFullContainer()
    {
        var opening = Base.Replace("FOB", "FCA");
        CollectionAssert.Contains(
            OperationsOpeningDraftValidator.MissingFields("FCL", opening),
            "whsName");
        CollectionAssert.DoesNotContain(
            OperationsOpeningDraftValidator.MissingFields("FCL",
                opening.Replace("\"incoterm\"", "\"whsName\":\"WHS Qingdao\", \"incoterm\"")
                    .Replace("FOB", "FCA")),
            "whsName");
    }

    [TestMethod]
    public void IncompleteDraftHasBlockingPermittedAndNotApplicableStates()
    {
        var fields = OperationsOpeningDraftValidator.EvaluateFields("FCL", Base);
        Assert.AreEqual("PENDIENTE_BLOQUEANTE",
            fields.Single(x => x.FieldCode == "equipment").CompletionStatus);
        Assert.AreEqual("PENDIENTE_PERMITIDO",
            fields.Single(x => x.FieldCode == "actualWeight").CompletionStatus);
        Assert.AreEqual("NO_APLICA",
            fields.Single(x => x.FieldCode == "stackable").CompletionStatus);
        Assert.AreEqual("CONFIRMADO",
            fields.Single(x => x.FieldCode == "cargoDescription").CompletionStatus);
    }

    [TestMethod]
    public void PickupNeedsAddressContactAndWindow()
    {
        var data = Base.Replace("\"incoterm\"", "\"requiresPickup\":true, \"incoterm\"");
        var missing = OperationsOpeningDraftValidator.MissingFields("LCL", data);
        CollectionAssert.Contains(missing, "pickupAddress");
        CollectionAssert.Contains(missing, "pickupContact");
        CollectionAssert.Contains(missing, "pickupWindow");
    }

    [TestMethod]
    public void RejectsAnyInvalidEquipmentLineInsteadOfAcceptingOneValidLine()
    {
        var data = Base.Replace("\"incoterm\"",
            "\"equipment\":[{\"equipmentType\":\"40HC\",\"quantity\":2}," +
            "{\"equipmentType\":\"20GP\",\"quantity\":0}],\"incoterm\"");
        CollectionAssert.Contains(
            OperationsOpeningDraftValidator.MissingFields("FCL", data), "equipment");
    }

    [TestMethod]
    public void AirDoesNotRequestMaritimeCarrier()
    {
        var data = Base.Replace("\"incoterm\"", "\"transportType\":\"AIR\",\"incoterm\"");
        var fields = OperationsOpeningDraftValidator.EvaluateFields("AIR", data);
        Assert.AreEqual("NO_APLICA",
            fields.Single(x => x.FieldCode == "carrierReference").CompletionStatus);
    }

    [TestMethod]
    public void CorruptedJsonIsBlockingAndDoesNotThrow()
    {
        CollectionAssert.Contains(
            OperationsOpeningDraftValidator.MissingFields("LCL", "{"),
            "openingData");
    }
}
