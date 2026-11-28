using Dhole.Operations.Domain.Services;
using static Dhole.Operations.Domain.Services.OperationsCostReconciliation;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsCostReconciliationTests
{
    [TestMethod]
    [DataRow(1800, 1950, 150)]
    [DataRow(150, 120, -30)]
    public void ChangedUsesSignedLineTotalDifference(int quoted, int actual, int expected)
    {
        Assert.IsTrue(TryEvaluate(new(quoted, "USD", "PerShipment", 1m),
            new(Changed, actual, "USD", "PerShipment", 1m, "Confirmed by agent"),
            out var result, out var error));
        Assert.IsNull(error);
        Assert.IsNotNull(result);
        Assert.AreEqual((decimal?)expected, result.Difference);
        Assert.AreEqual(TariffChanged, result.RequestType);
        Assert.IsNull(result.AdditionalAmount);
    }

    [TestMethod]
    public void AdditionalIsIndependentOfDifference()
    {
        Assert.IsTrue(TryEvaluate(null, new(Additional, 85m, "USD", "PerShipment", 1m, "New charge"),
            out var result, out _));
        Assert.IsNotNull(result);
        Assert.AreEqual((decimal?)85m, result.AdditionalAmount);
        Assert.IsNull(result.QuotedAmount);
        Assert.IsNull(result.Difference);
        Assert.AreEqual(AdditionalCost, result.RequestType);
    }

    [TestMethod]
    public void PendingDoesNotBecomeZeroAndMatchDoesNotRequestPricing()
    {
        var baseline = new Baseline(0m, "USD", "PerContainer", 2m);
        Assert.IsTrue(TryEvaluate(baseline, new(Pending, null, null, null, null, null), out var pending, out _));
        Assert.IsNotNull(pending);
        Assert.IsNull(pending.Difference);
        Assert.IsNull(pending.ActualAmount);
        Assert.IsTrue(TryEvaluate(baseline, new(Match, 0m, "USD", "PerContainer", 2m, null), out var match, out _));
        Assert.IsNotNull(match);
        Assert.AreEqual((decimal?)0m, match.Difference);
        Assert.IsNull(match.RequestType);
    }

    [TestMethod]
    public void RejectsCurrencyBasisQuantityContradictionsAndUnknownValues()
    {
        var baseline = new Baseline(1800m, "USD", "PerContainer", 2m);
        Input[] invalid = [
            new(Changed, 1950m, "CRC", "PerContainer", 2m, "Reason"),
            new(Changed, 1950m, "USD", "PerShipment", 2m, "Reason"),
            new(Changed, 1950m, "USD", "PerContainer", 1m, "Reason"),
            new(Changed, 1950m, "USD", "PerContainer", 2m, null),
            new(Changed, 1800m, "USD", "PerContainer", 2m, "Reason"),
            new(Match, 1950m, "USD", "PerContainer", 2m, null),
            new(Changed, null, "USD", "PerContainer", 2m, "Reason"),
            new(Changed, -1m, "USD", "PerContainer", 2m, "Reason"),
            new(Pending, 0m, "USD", "PerContainer", 2m, null),
            new(Additional, 85m, "USD", "PerContainer", 2m, "Already quoted")
        ];
        foreach (var input in invalid)
        {
            Assert.IsFalse(TryEvaluate(baseline, input, out var result, out var error));
            Assert.IsNull(result);
            Assert.IsNotNull(error);
        }
        Assert.IsFalse(TryEvaluate(baseline with { Amount = null }, new(Match, 0m, "USD", "PerContainer", 2m, null), out _, out _));
        Assert.IsFalse(TryEvaluate(null, new(Additional, 0m, "USD", "PerShipment", 1m, "Reason"), out _, out _));
    }
}
