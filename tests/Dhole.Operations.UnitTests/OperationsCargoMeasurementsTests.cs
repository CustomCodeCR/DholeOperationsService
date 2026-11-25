using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsCargoMeasurementsTests
{
    [TestMethod]
    public void NormalizesOriginalMeasurementsWithoutReplacingThem()
    {
        var data = OperationsCargoMeasurements.Parse("""{"estimatedWeightValue":"100","estimatedWeightUnit":"LB","volumeValue":"35.315","volumeUnit":"CFT","lengthValue":"40","lengthUnit":"IN","widthValue":1,"widthUnit":"M","heightValue":50,"heightUnit":"IN"}""");
        OperationsCargoMeasurements.NormalizeOpening(data);
        Assert.AreEqual("100", data["estimatedWeightValue"]!.GetValue<string>());
        Assert.AreEqual(45.359237m, data["estimatedWeightKg"]!.GetValue<decimal>());
        Assert.AreEqual(101.6m, data["lengthCm"]!.GetValue<decimal>());
        Assert.AreEqual(100m, data["widthCm"]!.GetValue<decimal>());
        Assert.AreEqual(127m, data["heightCm"]!.GetValue<decimal>());
        Assert.AreEqual(35.315m * 0.028316846592m, data["volumeCbm"]!.GetValue<decimal>());
    }

    [TestMethod]
    public void ConfirmedOriginWeightDoesNotReplaceEstimate()
    {
        var data = OperationsCargoMeasurements.Parse("""{"quotedWeightKg":1000,"weightValue":1000,"weightUnit":"KG"}""");
        OperationsCargoMeasurements.ConfirmWeight(data, 1150, "KG");
        Assert.AreEqual(1000m, data["estimatedWeightKg"]!.GetValue<decimal>());
        Assert.AreEqual(1150m, data["actualWeightKg"]!.GetValue<decimal>());
        Assert.AreEqual(1000m, data["quotedWeightKg"]!.GetValue<decimal>());
        Assert.ThrowsExactly<ArgumentException>(() => OperationsCargoMeasurements.RejectOriginOnCreate(data));
    }

    [TestMethod]
    public void AperturasCannotReplaceOrEraseConfirmedWeight()
    {
        var old = OperationsCargoMeasurements.Parse("""{"estimatedWeightValue":1000,"actualWeightValue":1150,"actualWeightUnit":"KG","actualWeightKg":1150}""");
        var edit = OperationsCargoMeasurements.Parse("""{"estimatedWeightValue":900}""");
        OperationsCargoMeasurements.PreserveOriginFields(edit, old);
        Assert.AreEqual(1150m, edit["actualWeightKg"]!.GetValue<decimal>());
        var malicious = OperationsCargoMeasurements.Parse("""{"actualWeightValue":10}""");
        Assert.ThrowsExactly<ArgumentException>(() => OperationsCargoMeasurements.PreserveOriginFields(malicious, old));
    }
}
