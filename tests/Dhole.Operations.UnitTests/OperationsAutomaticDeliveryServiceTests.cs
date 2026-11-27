using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsAutomaticDeliveryServiceTests
{
    [TestMethod]
    public void IncotermAndLegacyFlagDoNotCreateDeliveryService()
    {
        var fields = OperationsOpeningDraftPolicy.EvaluateFields("FCL", """{"incoterm":"DDP","requiresDelivery":true,"services":[{"serviceName":"Transporte Internacional"}]}""");
        Assert.AreEqual("NO_APLICA", fields.Single(x => x.FieldCode == "deliveryLocations").CompletionStatus);
    }

    [TestMethod]
    public void QuotedDeliveryIsConfirmedWithValidPoint()
    {
        var fields = OperationsOpeningDraftPolicy.EvaluateFields("FCL", """{"incoterm":"FOB","services":[{"serviceCode":"DELIVERY","serviceName":"Entrega"}],"deliveryLocations":[{"address":"San Jose","cargoCondition":"FiscalCargo"}]}""");
        Assert.AreEqual("CONFIRMADO", fields.Single(x => x.FieldCode == "deliveryLocations").CompletionStatus);
    }
}
