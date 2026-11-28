using Dhole.Operations.Domain.Models;
using static Dhole.Operations.Domain.Services.OperationsCarrierObservation;
using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsCarrierObservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
    private static (OperationsCarrierConnection Connection, OperationsCarrierCapability Capability, Binding Binding, Observation Event) Fixture()
    {
        var company = Guid.NewGuid();
        var provider = Guid.NewGuid();
        var connection = new OperationsCarrierConnection {
            CompanyId = company, SrmProviderId = provider, CarrierCode = "MAERSK", Environment = "UAT",
            Status = "ACTIVE", LastValidatedAtUtc = Now.AddDays(-1)
        };
        var capability = new OperationsCarrierCapability {
            ConnectionId = connection.Id, ProductCode = "OCEAN_TEST_FIXTURE", CapabilityCode = "TRACK_AND_TRACE",
            ApiVersion = "2.2", Status = "VERIFIED", VerifiedAtUtc = Now.AddDays(-1), EvidenceRef = "synthetic-test"
        };
        var binding = new Binding(company, connection.Id, Guid.NewGuid(), Guid.NewGuid(), provider, "MAERSK",
            "FCL", "MARITIME", "BOOKING-TEST", "CALL-A", "CRCAL", ["TEST-EQUIPMENT"]);
        var e = new Observation(company, connection.Id, provider, "MAERSK", capability.ProductCode, "2.2",
            "EVENT-A", binding.BookingReference, binding.TransportCallId, binding.LocationCode, "TEST-EQUIPMENT",
            "TRANSPORT", "DEPA", "ACT", "VESSEL", Now.AddHours(-1), Now.AddMinutes(-10));
        return (connection, capability, binding, e);
    }

    [TestMethod]
    public void RegistrationDoesNotAuthorizeCorrelation()
    {
        var f = Fixture();
        f.Connection.Status = "REQUESTED";
        Assert.AreEqual("NOT_HOMOLOGATED", Correlate(f.Event, f.Connection, f.Capability, [f.Binding], Now).State);
        f.Connection.Status = "ACTIVE"; f.Capability.Status = "UNVERIFIED";
        Assert.AreEqual("NOT_HOMOLOGATED", Correlate(f.Event, f.Connection, f.Capability, [f.Binding], Now).State);
    }

    [TestMethod]
    public void FclCandidatesAndOnlyMaerskLclScope()
    {
        foreach (var carrier in OperationsCarrierCodes.Candidates)
        {
            Assert.IsTrue(SupportsShipment(carrier, "FCL", "MARITIME"));
            Assert.AreEqual(carrier == "MAERSK", SupportsShipment(carrier, "LCL", "MARITIME"));
            Assert.IsFalse(SupportsShipment(carrier, "FCL", "AIR"));
        }
        Assert.IsTrue(OperationsCarrierCodes.IsKnown("HMM"));
        var f = Fixture();
        Assert.AreEqual("NO_MATCH", Correlate(f.Event, f.Connection, f.Capability, [f.Binding with { Shipment = "LCL" }], Now).State);
    }

    [TestMethod]
    public void SameHistoricalContainerOrBookingIsInsufficient()
    {
        var f = Fixture();
        foreach (var other in new[] {
            f.Binding with { CompanyId = Guid.NewGuid() }, f.Binding with { ConnectionId = Guid.NewGuid() },
            f.Binding with { SrmProviderId = Guid.NewGuid() }, f.Binding with { BookingReference = "OTHER" },
            f.Binding with { TransportCallId = "CALL-B" }, f.Binding with { LocationCode = "PABLB" },
            f.Binding with { EquipmentReferences = ["OTHER"] }
        }) Assert.AreEqual("NO_MATCH", Correlate(f.Event, f.Connection, f.Capability, [other], Now).State);
        Assert.AreEqual("AMBIGUOUS_BINDING", Correlate(f.Event, f.Connection, f.Capability,
            [f.Binding, f.Binding with { CaseId = Guid.NewGuid(), LegId = Guid.NewGuid() }], Now).State);
        Assert.AreEqual(f.Binding.LegId, Correlate(f.Event, f.Connection, f.Capability, [f.Binding], Now).Value!.LegId);
    }

    [TestMethod]
    public void PlannedEstimatedActualAndArrivalRemainDifferentFields()
    {
        var f = Fixture();
        foreach (var pair in new[] { ("PLN", "PLANNED_DEPARTURE"), ("EST", "ESTIMATED_DEPARTURE"), ("ACT", "ACTUAL_DEPARTURE") })
            Assert.AreEqual(pair.Item2, Correlate(f.Event with { Classifier = pair.Item1 },
                f.Connection, f.Capability, [f.Binding], Now).Value!.Field);
        Assert.AreEqual("ACTUAL_ARRIVAL", Correlate(f.Event with { EventCode = "ARRI" },
            f.Connection, f.Capability, [f.Binding], Now).Value!.Field);
        Assert.AreEqual("INVALID_OBSERVATION", Correlate(f.Event with { OccurredAt = Now.AddDays(1) },
            f.Connection, f.Capability, [f.Binding], Now).State);
        Assert.AreEqual("UNSUPPORTED_EVENT", Correlate(f.Event with { EventType = "EQUIPMENT", EventCode = "LOAD" },
            f.Connection, f.Capability, [f.Binding], Now).State);
    }

    [TestMethod]
    public void ReconciliationDoesNotOverwriteManualOrNewerEvidence()
    {
        var f = Fixture();
        var c = Correlate(f.Event, f.Connection, f.Capability, [f.Binding], Now).Value!;
        Assert.AreEqual("DUPLICATE", Reconcile(c, new(c.EventKey, c.Fingerprint, c.CreatedAt), new(null, false, null)));
        Assert.AreEqual("EVENT_ID_CONFLICT", Reconcile(c, new(c.EventKey, "other", c.CreatedAt), new(null, false, null)));
        Assert.AreEqual("REVIEW_REQUIRED", Reconcile(c, null, new("manual", true, null)));
        Assert.AreEqual("HISTORICAL_ONLY", Reconcile(c, null, new("newer", false, Now)));
        Assert.AreEqual("UNCHANGED", Reconcile(c, null, new(c.Value, true, Now)));
        Assert.AreEqual("IGNORE_EMPTY", Reconcile(c with { Value = "" }, null, new("manual", true, null)));
        Assert.AreEqual("PROPOSE_UPDATE", Reconcile(c, null, new(null, false, null)));
    }

    [TestMethod]
    public void RetryHonorsAuthorizationAndRetryAfterWithoutInfiniteRetry()
    {
        foreach (var status in new[] { 401, 403 })
            Assert.IsNull(OperationsCarrierRetryPolicy.AfterReadFailure(status, false, 1, Now, null, 0).NextAttemptAt);
        var after = Now.AddHours(1);
        var throttled = OperationsCarrierRetryPolicy.AfterReadFailure(429, false, 1, Now, after, 0);
        Assert.AreEqual("RATE_LIMITED", throttled.State);
        Assert.AreEqual(after, throttled.NextAttemptAt);
        Assert.AreEqual(Now.AddSeconds(60), OperationsCarrierRetryPolicy.AfterReadFailure(null, true, 2, Now, null, 0).NextAttemptAt);
        Assert.IsNull(OperationsCarrierRetryPolicy.AfterReadFailure(503, false, 5, Now, null, 0).NextAttemptAt);
    }
}
