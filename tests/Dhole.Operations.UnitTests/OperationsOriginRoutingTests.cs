using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsOriginRoutingTests
{
    [TestMethod]
    [DataRow("Miami, Estados Unidos", "US")]
    [DataRow("Shanghai, China", "CN")]
    [DataRow("Hamburgo, Alemania", "DE")]
    [DataRow("Miami", null)]
    public void UsesExplicitOriginCountryNotCityGuess(string label, string? expected)
    {
        Assert.AreEqual(expected, OperationsOriginRouting.Country(null, label));
    }
}
