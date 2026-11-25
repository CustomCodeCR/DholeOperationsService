using Dhole.Operations.Api.Endpoints;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsFieldValidation20261125Tests
{
    private const string Fcl = """
        {"cargoDescription":"Piezas","incoterm":"FOB","pol":"Shanghai","poe":"Moín","pod":"San José","equipment":[{"equipmentType":"40HC","quantity":1}]}
        """;

    [TestMethod]
    public void MaritimeReferencesAreDeferredAndAirlineDoesNotApply()
    {
        var fields = OperationsOpeningDraftValidator.EvaluateCase("FCL", Fcl, Guid.NewGuid(), false)
            .ToDictionary(f => f.FieldCode, f => f.CompletionStatus);
        Assert.AreEqual("CONFIRMADO", fields["accountId"]);
        Assert.AreEqual("CONFIRMADO", fields["equipment"]);
        Assert.AreEqual("PENDIENTE_PERMITIDO", fields["bookingNumber"]);
        Assert.AreEqual("PENDIENTE_PERMITIDO", fields["containerNumber"]);
        Assert.AreEqual("PENDIENTE_PERMITIDO", fields["actualWeight"]);
        Assert.AreEqual("NO_APLICA", fields["airline"]);
        Assert.IsFalse(fields.Values.Contains("PENDIENTE_BLOQUEANTE"));
    }

    [TestMethod]
    public void MissingEquipmentBlocksButCompletingItAllowsAdvance()
    {
        var empty = Fcl.Replace("40HC", "");
        Assert.IsTrue(OperationsOpeningDraftValidator.MissingFields("FCL", empty).Contains("equipment"));
        Assert.IsEmpty(OperationsOpeningDraftValidator.MissingFields("FCL", Fcl));
        var filled = Fcl.Replace("\"cargoDescription\"", "\"bookingNumber\":\"B123\",\"cargoDescription\"");
        Assert.AreEqual("CONFIRMADO", OperationsOpeningDraftValidator.EvaluateFields("FCL", filled)
            .Single(f => f.FieldCode == "bookingNumber").CompletionStatus);
    }
    [TestMethod]
    public void NewManualOpeningRequiresServiceSelection()
    {
        var noServices = Fcl.Replace("\"cargoDescription\"", "\"openingValidationVersion\":4,\"cargoDescription\"");
        Assert.IsTrue(OperationsOpeningDraftValidator.MissingFields("FCL", noServices, true).Contains("services"));
        var withServices = noServices.Replace("\"cargoDescription\"", "\"services\":[\"service\"],\"serviceSelections\":[{\"id\":\"service\"}],\"cargoDescription\"");
        Assert.IsFalse(OperationsOpeningDraftValidator.MissingFields("FCL", withServices, true).Contains("services"));
    }
}
