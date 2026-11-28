using Dhole.Operations.Domain.Models;
using static Dhole.Operations.Domain.Services.OperationsItineraryPolicy;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsItineraryPolicyTests
{
    private static readonly Guid Shanghai = Guid.NewGuid(), Balboa = Guid.NewGuid(), SanJose = Guid.NewGuid();
    private static readonly Guid Transit = Guid.NewGuid(), Panama = Guid.NewGuid(), CostaRica = Guid.NewGuid(), Service = Guid.NewGuid();
    private static readonly Dictionary<Guid, Location> Locations = new()
    {
        [Shanghai] = new(Shanghai, "Shanghai", "CN", "CY"),
        [Balboa] = new(Balboa, "Balboa", "PA", "CY"),
        [SanJose] = new(SanJose, "San José", "CR", "SD")
    };
    private static LegInput[] Multimodal() => [
        new(Shanghai, Balboa, "MARITIME", Transit, "ENTRY_TRANSFER", Panama, Service),
        new(Balboa, SanJose, "ROAD", Transit, "FINAL", CostaRica, null)
    ];

    [TestMethod]
    public void MultimodalUsesExplicitEntryAndFinalResponsibilities()
    {
        Assert.IsNull(Validate(Multimodal(), Locations, "CN", "CR"));
        Assert.AreEqual("PANAMA", DestinationDepartment("PA"));
        Assert.AreEqual("COSTA_RICA", DestinationDepartment("CR"));
        Assert.AreEqual("DESTINO_US", DestinationDepartment("US"));
        var legs = Multimodal();
        legs[0] = legs[0] with { DestinationKind = "NONE", DestinationStageId = null, DestinationServiceLineId = null };
        Assert.IsNull(Validate(legs, Locations, "CN", "CR"), "A transshipment does not imply local Destination service.");
    }

    [TestMethod]
    public void DirectPanamaIsFinalNotEntry()
    {
        var direct = new LegInput(Shanghai, Balboa, "MARITIME", Transit, "FINAL", Panama, null);
        Assert.IsNull(Validate([direct], Locations, "CN", "PA"));
        Assert.AreEqual("DESTINATION_SCOPE_INVALID", Validate([direct with {
            DestinationKind = "ENTRY_TRANSFER", DestinationServiceLineId = Service }], Locations, "CN", "PA"));
    }

    [TestMethod]
    public void MissingServiceWrongCountryAndDiscontinuityAreRejected()
    {
        var legs = Multimodal();
        legs[0] = legs[0] with { DestinationServiceLineId = null };
        Assert.AreEqual("DESTINATION_SCOPE_INVALID", Validate(legs, Locations, "CN", "CR"));
        Assert.AreEqual("ROUTE_COUNTRY_MISMATCH", Validate(Multimodal(), Locations, "CN", "PA"));
        legs = Multimodal(); legs[1] = legs[1] with { FromLocationId = Shanghai };
        Assert.AreEqual("ROUTE_DISCONTINUITY", Validate(legs, Locations, "CN", "CR"));
        legs = Multimodal(); legs[0] = legs[0] with { TransportCode = "AIR" };
        Assert.AreEqual("TRANSPORT_LOCATION_MISMATCH", Validate(legs, Locations, "CN", "CR"));
        legs = Multimodal(); legs[0] = legs[0] with { FromLocationId = Guid.NewGuid() };
        Assert.AreEqual("LOCATION_UNVERIFIED", Validate(legs, Locations, "CN", "CR"));
    }

    [TestMethod]
    public void DestinationPermissionRequiresActionCountryAndStageVisibility()
    {
        Assert.IsTrue(CanReadDestination("PA", true, true, false, true));
        Assert.IsFalse(CanReadDestination("CR", true, false, false, true));
        Assert.IsFalse(CanReadDestination("PA", false, true, false, true));
        Assert.IsFalse(CanReadDestination("PA", true, true, false, false));
        Assert.IsTrue(CanReadDestination("GT", true, false, true, true));
        Assert.IsFalse(CanReadDestination("GT", true, false, true, false));
    }

    [TestMethod]
    public void OneCompletedLegDoesNotCompleteTheItineraryOrCase()
    {
        var operation = new OperationsCase { Status = "ACTIVE", ModalityCode = "MULTIMODAL" };
        var legs = new OperationsItineraryLeg[] {
            new() { CaseId = operation.Id, Sequence = 1, Status = "COMPLETED" },
            new() { CaseId = operation.Id, Sequence = 2, Status = "DRAFT" }
        };
        Assert.AreEqual("INCOMPLETE", Summarize(legs));
        legs[1].Status = "COMPLETED";
        Assert.AreEqual("ALL_LEGS_COMPLETED", Summarize(legs));
        Assert.AreEqual("ACTIVE", operation.Status);
        Assert.AreEqual("MULTIMODAL", operation.ModalityCode);
        Assert.AreNotEqual(legs[0].Id, legs[1].Id);
    }
}
