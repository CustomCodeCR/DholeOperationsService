using Dhole.Operations.Api.Endpoints;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsOpeningShipmentParityTests
{
    private const string BaseOpening = """
        {
          "cargoDescription": "Repuestos",
          "pol": "Shanghai, China",
          "poe": "Puerto Caldera, Costa Rica",
          "pod": "San José, Costa Rica",
          "incoterm": "FOB"
        }
        """;

    [TestMethod]
    public void FullTruckNeedsEquipmentBeforeValidation()
    {
        var missing = OperationsOpeningDraftValidator.MissingFields("FTL", BaseOpening);
        CollectionAssert.Contains(missing, "equipment");
    }

    [TestMethod]
    public void MultimodalFclNeedsEquipmentButLclDoesNot()
    {
        var fcl = BaseOpening.Replace("\"incoterm\"", "\"shipmentType\": \"FCL\", \"incoterm\"");
        var lcl = BaseOpening.Replace("\"incoterm\"", "\"shipmentType\": \"LCL\", \"incoterm\"");
        CollectionAssert.Contains(OperationsOpeningDraftValidator.MissingFields("MULTIMODAL", fcl), "equipment");
        CollectionAssert.DoesNotContain(OperationsOpeningDraftValidator.MissingFields("MULTIMODAL", lcl), "equipment");
    }

    [TestMethod]
    public void ValidMultiEquipmentConfigurationIsAccepted()
    {
        var fcl = BaseOpening.Replace("\"incoterm\"",
            "\"shipmentType\": \"FCL\", \"equipment\": [" +
            "{\"equipmentType\": \"40HC\", \"quantity\": 2}," +
            "{\"equipmentType\": \"20GP\", \"quantity\": 1}], \"incoterm\"");
        CollectionAssert.DoesNotContain(OperationsOpeningDraftValidator.MissingFields("MULTIMODAL", fcl), "equipment");
    }
    [TestMethod]
    public void DeliveryRequiresContractedServiceEvenForDdp()
    {
        var noDelivery = BaseOpening.Replace("FOB", "DDP");
        var yesDelivery = noDelivery.Replace("\"incoterm\"", "\"services\": [{\"serviceName\": \"Entrega\"}], \"incoterm\"");
        Assert.AreEqual("NO_APLICA", OperationsOpeningDraftValidator.EvaluateFields("FCL", noDelivery).Single(x => x.FieldCode == "deliveryLocations").CompletionStatus);
        Assert.AreEqual("PENDIENTE_PERMITIDO", OperationsOpeningDraftValidator.EvaluateFields("FCL", yesDelivery).Single(x => x.FieldCode == "deliveryLocations").CompletionStatus);
    }
}
