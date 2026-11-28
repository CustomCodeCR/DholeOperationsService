using System.Text.Json.Nodes;

namespace Dhole.Operations.Domain.Services;

/// <summary>One additive field matrix for Origin. Applicability is not a
/// workflow requirement. Profiles do not grant permissions or confirm data.</summary>
public static class OperationsOriginProfile
{
    public sealed record Field(string Code, string Group, string Kind, bool ExternalCandidate = false);
    public sealed record Profile(string Code, bool IsUnitedStates, bool IsProvisional, IReadOnlyList<Field> Fields);
    private static Field F(string code, string group, string kind, bool external = false) => new(code, group, kind, external);
    private static readonly Field[] CommonCargo = [
        F("packageCount", "CARGO", "INTEGER"), F("volume", "CARGO", "MEASUREMENT"),
        F("weight", "CARGO", "MEASUREMENT"), F("cargoDescription", "CARGO", "TEXT")
    ];
    private static readonly Field[] Parties = [
        F("shippers", "PARTIES", "PARTY_MULTI"), F("consignee", "PARTIES", "PARTY"),
        F("notify", "PARTIES", "PARTY")
    ];
    private static readonly Field[] Dates = [
        F("cargoReadyDate", "SHIPMENT", "DATE"), F("etd", "COORDINATION", "TEMPORAL", true),
        F("eta", "COORDINATION", "TEMPORAL", true), F("estimatedTransitDays", "COORDINATION", "CALCULATED"),
        F("actualDeparture", "COORDINATION", "TEMPORAL", true)
    ];
    private static readonly Field[] Maritime = [
        F("shippingOrder", "DOCUMENTS", "DATE_DOCUMENT"), F("hbl", "DOCUMENTS", "REFERENCE_DOCUMENT"),
        F("booking", "DOCUMENTS", "REFERENCE_DOCUMENT", true), F("mbl", "DOCUMENTS", "REFERENCE_DOCUMENT", true),
        F("departureVessel", "COORDINATION", "VESSEL", true), F("departureVoyage", "COORDINATION", "REFERENCE", true),
        F("imo", "CARGO", "BOOLEAN")
    ];
    private static readonly Field[] Fcl = [
        F("bookingConfirmedDate", "COORDINATION", "TEMPORAL", true),
        F("estimatedLoadingDate", "EQUIPMENT", "TEMPORAL"),
        F("equipmentLoadedDate", "EQUIPMENT", "PER_EQUIPMENT_TEMPORAL", true),
        F("containerNumber", "EQUIPMENT", "EQUIPMENT_REFERENCE", true),
        F("sealNumber", "EQUIPMENT", "PER_EQUIPMENT_REFERENCE"),
        F("overweight", "CARGO", "BOOLEAN"), F("clientPoReference", "SHIPMENT", "REFERENCE"),
        F("invoiceReference", "DOCUMENTS", "REFERENCE_DOCUMENT")
    ];
    private static readonly Field[] Lcl = [
        F("stackable", "CARGO", "BOOLEAN"), F("warehouseEntryNumber", "WAREHOUSE", "REFERENCE"),
        F("warehouseEntryDate", "WAREHOUSE", "TEMPORAL"),
        F("estimatedWarehouseEntryDate", "WAREHOUSE", "TEMPORAL"),
        F("cargoValue", "CARGO", "MONEY"), F("consolidationType", "WAREHOUSE", "CATALOG"),
        F("consolidationId", "WAREHOUSE", "CONSOLIDATION"), F("whs", "WAREHOUSE", "CATALOG")
    ];
    private static readonly Field[] Air = [
        F("hawb", "DOCUMENTS", "REFERENCE_DOCUMENT"), F("mawb", "DOCUMENTS", "REFERENCE_DOCUMENT"),
        F("airline", "COORDINATION", "CATALOG"), F("flight", "COORDINATION", "REFERENCE"),
        F("booking", "DOCUMENTS", "REFERENCE_DOCUMENT"),
        F("departureAirport", "COORDINATION", "CATALOG"), F("arrivalAirport", "COORDINATION", "CATALOG"),
        F("volumetricWeight", "CARGO", "MEASUREMENT"), F("chargeableWeight", "CARGO", "MEASUREMENT"),
        F("cargoValue", "CARGO", "MONEY"), F("dangerousGoods", "CARGO", "BOOLEAN"), F("stackable", "CARGO", "BOOLEAN")
    ];

    // The persisted modality code is the existing Operations taxonomy. Explicit
    // incompatible transport codes and multimodal cases never imply a maritime profile.
    public static Profile Resolve(string? modality, string? transport, bool usTeam, bool caldera,
        bool airPreviewEnabled = false)
    {
        var mode = modality?.Trim().ToUpperInvariant();
        var transportCode = transport?.Trim().ToUpperInvariant();
        var maritime = string.IsNullOrEmpty(transportCode) || transportCode is "MARITIME" or "SEA" or "OCEAN";
        var air = string.IsNullOrEmpty(transportCode) || transportCode is "AIR";
        if (mode is "FCL" or "LCL" && maritime)
        {
            List<Field> fields = [.. Dates, .. Maritime, .. CommonCargo, .. Parties];
            fields.AddRange(mode == "FCL" ? Fcl : Lcl);
            if (mode == "LCL" || caldera) fields.Add(F("gcf", "SHIPMENT", "UNDEFINED"));
            if (usTeam)
            {
                fields.Add(F("tracking", "COORDINATION", "REFERENCES"));
                fields.Add(F("carrier", "COORDINATION", "PROVIDER"));
            }
            return new(mode, usTeam, false, fields.ToArray());
        }
        if (mode is "AIR" or "AIRCONSOL" && air)
            return new("AIR", usTeam, true, airPreviewEnabled ? [.. Dates, .. CommonCargo, .. Parties, .. Air] : []);
        return new("UNRESOLVED", usTeam, true, []);
    }

    public static bool IsCaldera(JsonObject opening, IReadOnlySet<string> catalogIds) =>
        new[] { "polCatalogId", "poeCatalogId", "podCatalogId" }.Any(key =>
            opening[key] is JsonValue value && value.TryGetValue<string>(out var id) &&
            !string.IsNullOrWhiteSpace(id) && catalogIds.Contains(id.Trim()));
}
