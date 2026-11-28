using System.Security.Cryptography;
using System.Text.Json;
using Dhole.Operations.Domain.Models;

namespace Dhole.Operations.Domain.Services;

/// <summary>Canonical read-only carrier observations. All identity fields must
/// come from an authenticated, homologated adapter and a persisted leg binding.
/// This is not an ingress API and cannot itself confirm or transfer a case.</summary>
public static class OperationsCarrierObservation
{
    public sealed record Binding(Guid CompanyId, Guid ConnectionId, Guid CaseId, Guid LegId,
        Guid SrmProviderId, string CarrierCode, string Shipment, string Transport,
        string BookingReference, string TransportCallId, string LocationCode,
        string[] EquipmentReferences);
    public sealed record Observation(Guid CompanyId, Guid ConnectionId, Guid SrmProviderId,
        string CarrierCode, string Product, string ApiVersion, string EventId,
        string BookingReference, string TransportCallId, string LocationCode,
        string? EquipmentReference, string EventType, string EventCode, string Classifier,
        string TransportMode, DateTimeOffset OccurredAt, DateTimeOffset CreatedAt);
    public sealed record Candidate(Guid CaseId, Guid LegId, string Field, string Value,
        string EventKey, string Fingerprint, DateTimeOffset OccurredAt, DateTimeOffset CreatedAt);
    public sealed record Correlation(string State, Candidate? Value);
    public sealed record StoredObservation(string EventKey, string Fingerprint, DateTimeOffset CreatedAt);
    public sealed record FieldState(string? Value, bool ManuallyConfirmed, DateTimeOffset? SourceCreatedAt);

    public static bool SupportsShipment(string carrier, string shipment, string transport) =>
        OperationsCarrierCodes.IsKnown(carrier) && transport == "MARITIME" &&
        (shipment == "FCL" || (shipment == "LCL" && carrier == "MAERSK"));

    public static bool IsHomologated(OperationsCarrierConnection connection,
        OperationsCarrierCapability capability, string shipment, string transport) =>
        connection.Id != Guid.Empty && connection.CompanyId != Guid.Empty &&
        connection.SrmProviderId.HasValue && connection.SrmProviderId != Guid.Empty &&
        connection.Status == "ACTIVE" && connection.LastValidatedAtUtc.HasValue &&
        capability.ConnectionId == connection.Id && capability.Status == "VERIFIED" &&
        capability.VerifiedAtUtc.HasValue && !string.IsNullOrWhiteSpace(capability.ApiVersion) &&
        !string.IsNullOrWhiteSpace(capability.EvidenceRef) &&
        SupportsShipment(connection.CarrierCode, shipment, transport) &&
        (shipment == "FCL" || capability.ProductCode == "LCL_BOOKING");

    public static Correlation Correlate(Observation e, IEnumerable<Binding> bindings, DateTimeOffset now)
    {
        if (e.CompanyId == Guid.Empty || e.ConnectionId == Guid.Empty || e.SrmProviderId == Guid.Empty ||
            !OperationsCarrierCodes.IsKnown(e.CarrierCode) || !Text(e.Product) || !Text(e.ApiVersion) ||
            !Text(e.EventId) || !Text(e.BookingReference) || !Text(e.TransportCallId) || !Text(e.LocationCode) ||
            e.Classifier is not ("ACT" or "EST" or "PLN") || e.OccurredAt == default || e.CreatedAt == default ||
            e.CreatedAt > now.AddMinutes(5) || (e.Classifier == "ACT" && e.OccurredAt > now.AddMinutes(5)))
            return new("INVALID_OBSERVATION", null);

        var matches = bindings.Where(b => b.CaseId != Guid.Empty && b.LegId != Guid.Empty &&
            b.CompanyId == e.CompanyId && b.ConnectionId == e.ConnectionId && b.SrmProviderId == e.SrmProviderId &&
            b.CarrierCode == e.CarrierCode && SupportsShipment(b.CarrierCode, b.Shipment, b.Transport) &&
            (b.Shipment == "FCL" || e.Product == "LCL_BOOKING") &&
            b.BookingReference == e.BookingReference && b.TransportCallId == e.TransportCallId &&
            b.LocationCode == e.LocationCode &&
            (e.EquipmentReference is null || b.EquipmentReferences.Contains(e.EquipmentReference, StringComparer.Ordinal))).ToArray();
        // Multiple authorized IDTRA may share a booking. Do not guess the target,
        // nor broadcast a transport-call event to all matching legs.
        if (matches.Length != 1) return new(matches.Length == 0 ? "NO_MATCH" : "AMBIGUOUS_BINDING", null);
        var binding = matches[0];
        // The initial mapping is deliberately limited to vessel transport events.
        // Equipment LOAD/STUF and shipment confirmations have different semantics.
        if (e.EventType != "TRANSPORT" || e.TransportMode != "VESSEL" ||
            e.EventCode is not ("DEPA" or "ARRI"))
            return new("UNSUPPORTED_EVENT", null);
        var field = (e.EventCode, e.Classifier) switch
        {
            ("DEPA", "PLN") => "PLANNED_DEPARTURE",
            ("DEPA", "EST") => "ESTIMATED_DEPARTURE",
            ("DEPA", "ACT") => "ACTUAL_DEPARTURE",
            ("ARRI", "PLN") => "PLANNED_ARRIVAL",
            ("ARRI", "EST") => "ESTIMATED_ARRIVAL",
            ("ARRI", "ACT") => "ACTUAL_ARRIVAL",
            _ => throw new InvalidOperationException()
        };
        var key = Hash(new { e.CompanyId, e.ConnectionId, e.Product, e.ApiVersion, e.EventId });
        var fingerprint = Hash(e with { OccurredAt = e.OccurredAt.ToUniversalTime(), CreatedAt = e.CreatedAt.ToUniversalTime() });
        return new("CANDIDATE", new(binding.CaseId, binding.LegId, field,
            e.OccurredAt.ToUniversalTime().ToString("O"), key, fingerprint, e.OccurredAt.ToUniversalTime(), e.CreatedAt.ToUniversalTime()));
    }

    public static string Reconcile(Candidate candidate, StoredObservation? previousEvent, FieldState field)
    {
        if (previousEvent is not null && previousEvent.EventKey == candidate.EventKey)
            return previousEvent.Fingerprint == candidate.Fingerprint ? "DUPLICATE" : "EVENT_ID_CONFLICT";
        if (string.IsNullOrWhiteSpace(candidate.Value)) return "IGNORE_EMPTY";
        if (field.Value == candidate.Value) return "UNCHANGED";
        if (field.ManuallyConfirmed) return "REVIEW_REQUIRED";
        if (field.SourceCreatedAt.HasValue && candidate.CreatedAt <= field.SourceCreatedAt.Value)
            return "HISTORICAL_ONLY";
        // A valid ACT event is only evidence for the correct leg. Never
        // call the legacy departure endpoint, close a stage or mutate QUO.
        return "PROPOSE_UPDATE";
    }

    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200;
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
