using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsOriginTransitDurationTests
{
    [TestMethod]
    public void CalendarDatesAreNotConvertedToUtcOrAffectedByDaylightSaving()
    {
        var result = OperationsOriginTransitDuration.Calculate("2026-03-07", "2026-03-09");
        Assert.AreEqual("CALCULATED", result.State);
        Assert.AreEqual((decimal?)2m, result.Days);
        Assert.AreEqual("DATE", result.Precision);
        Assert.AreEqual((decimal?)0m, OperationsOriginTransitDuration.Calculate("2026-11-28", "2026-11-28").Days);
    }

    [TestMethod]
    public void ExplicitOffsetsAreComparedAsInstants()
    {
        var result = OperationsOriginTransitDuration.Calculate("2026-11-28T18:00:00+08:00", "2026-11-29T04:00:00-06:00");
        Assert.AreEqual("CALCULATED", result.State);
        Assert.AreEqual((decimal?)1m, result.Days);
        Assert.AreEqual("INSTANT", result.Precision);
        Assert.AreEqual((decimal?)0.5m, OperationsOriginTransitDuration.Calculate(
            "2026-11-28T12:00:00Z", "2026-11-29T00:00:00Z").Days);
    }

    [TestMethod]
    public void MissingAndInvalidDatesAreNotZero()
    {
        foreach (var missing in new string?[] { null, "" })
        {
            var result = OperationsOriginTransitDuration.Calculate(missing, "2026-11-29");
            Assert.AreEqual("PENDING", result.State);
            Assert.IsNull(result.Days);
        }
        foreach (var pair in new[] {
            ("2026-11-30", "2026-11-29"), ("2026-02-30", "2026-03-01"),
            ("2026-11-28", "2026-11-29T00:00:00Z"),
            ("2026-11-28T12:00:00", "2026-11-29T12:00:00"),
            ("2026-11-28T18:00:00-06:00", "2026-11-28T19:00:00+08:00")
        })
        {
            var result = OperationsOriginTransitDuration.Calculate(pair.Item1, pair.Item2);
            Assert.AreEqual("INVALID", result.State);
            Assert.IsNull(result.Days);
        }
    }

    [TestMethod]
    public void PlanningPatchPreservesOmittedValuesAndRevalidatesChangedEstimates()
    {
        Assert.IsTrue(OperationsOriginTransitDuration.TryMergePlanning("2026-11-28", "2026-12-03",
            null, "2026-12-04", out var etd, out var eta));
        Assert.AreEqual("2026-11-28", etd);
        Assert.AreEqual("2026-12-04", eta);
        Assert.AreEqual((decimal?)6m, OperationsOriginTransitDuration.Calculate(etd, eta).Days);
        Assert.IsFalse(OperationsOriginTransitDuration.TryMergePlanning("2026-11-28", "2026-12-03",
            null, "2026-11-27", out _, out _));
        Assert.IsTrue(OperationsOriginTransitDuration.TryMergePlanning("2026-11-28T12:00:00Z", "2026-12-03T12:00:00Z",
            null, null, out etd, out eta));
        Assert.AreEqual("2026-11-28T12:00:00Z", etd);
        Assert.IsFalse(OperationsOriginTransitDuration.TryMergePlanning(etd, eta, "2026-11-29", null, out _, out _));
        Assert.IsTrue(OperationsOriginTransitDuration.TryMergePlanning("2026-11-28", "2026-12-03",
            "", null, out etd, out eta));
        Assert.AreEqual("", etd);
        Assert.AreEqual("2026-12-03", eta);
    }
}
