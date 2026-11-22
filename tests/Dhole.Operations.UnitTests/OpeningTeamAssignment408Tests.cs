using Dhole.Operations.Api.Endpoints;
using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OpeningTeamAssignment408Tests
{
    [TestMethod]
    public void FclOpeningWithoutWeightCanProceedToApertureValidation()
    {
        const string opening = """
        {
          "transportType": "MARITIME", "shipmentType": "FCL",
          "cargoDescription": "Llantas", "pol": "Shanghai, China",
          "poe": "Puerto Caldera, Costa Rica", "pod": "San José, Costa Rica",
          "incoterm": "FOB", "stackable": null,
          "equipment": [{"equipmentType": "40HC", "quantity": 1}]
        }
        """;
        CollectionAssert.AreEqual(Array.Empty<string>(),
            OperationsOpeningDraftValidator.MissingFields("FCL", opening));
    }

    [TestMethod]
    public void FclOpeningMustHaveRealEquipment()
    {
        const string opening = """
        {"cargoDescription":"Carga","pol":"Shanghai","pod":"San José","incoterm":"FOB","equipment":[]}
        """;
        CollectionAssert.Contains(
            OperationsOpeningDraftValidator.MissingFields("FCL", opening),
            "equipment");
    }

    [TestMethod]
    public void PoolRequiresTwoDistinctUsersFromSameTeam()
    {
        var team = Guid.NewGuid();
        var person = Guid.NewGuid();
        var candidate = new AperturePoolMember(team, person, 10m, 0m, 0);
        Assert.IsNull(OperationsDualPoolScoring.Select(
            new[] { candidate, candidate }, 1m));
    }
}
