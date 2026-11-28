using Dhole.Operations.Domain.Models;

namespace Dhole.Operations.Domain.Services;

/// <summary>Conservative draft topology; never derives a local operation from a port call.</summary>
public static class OperationsItineraryPolicy
{
    public sealed record Location(Guid Id, string Label, string CountryCode, string TerminalType);
    public sealed record LegInput(Guid FromLocationId, Guid ToLocationId, string TransportCode,
        Guid TransitStageId, string DestinationKind, Guid? DestinationStageId, Guid? DestinationServiceLineId);

    public static string DestinationDepartment(string country) => country switch
    {
        "PA" => "PANAMA", "CR" => "COSTA_RICA", _ => "DESTINO_" + country
    };

    public static string? Validate(IReadOnlyList<LegInput> legs, IReadOnlyDictionary<Guid, Location> locations,
        string? originCountry, string? finalCountry)
    {
        if (legs.Count is < 1 or > 20 || !Country(originCountry) || !Country(finalCountry)) return "ITINERARY_INVALID";
        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            if (leg is null || leg.FromLocationId == Guid.Empty || leg.ToLocationId == Guid.Empty ||
                leg.FromLocationId == leg.ToLocationId || leg.TransitStageId == Guid.Empty ||
                !locations.TryGetValue(leg.FromLocationId, out var from) ||
                !locations.TryGetValue(leg.ToLocationId, out var to) ||
                !Country(from.CountryCode) || !Country(to.CountryCode)) return "LOCATION_UNVERIFIED";
            if (i == 0 && from.CountryCode != originCountry || i == legs.Count - 1 && to.CountryCode != finalCountry)
                return "ROUTE_COUNTRY_MISMATCH";
            if (i > 0 && legs[i - 1].ToLocationId != leg.FromLocationId) return "ROUTE_DISCONTINUITY";
            if (!Compatible(leg.TransportCode, from.TerminalType, to.TerminalType)) return "TRANSPORT_LOCATION_MISMATCH";
            if (leg.DestinationKind == "NONE")
            {
                if (leg.DestinationStageId.HasValue || leg.DestinationServiceLineId.HasValue) return "DESTINATION_SCOPE_INVALID";
            }
            else if (leg.DestinationKind == "FINAL" || leg.DestinationKind == "ENTRY_TRANSFER")
            {
                if (leg.DestinationStageId is null || leg.DestinationStageId == Guid.Empty ||
                    leg.DestinationStageId == leg.TransitStageId ||
                    (leg.DestinationKind == "FINAL") != (i == legs.Count - 1) ||
                    (leg.DestinationKind == "ENTRY_TRANSFER" &&
                        (leg.DestinationServiceLineId is null || leg.DestinationServiceLineId == Guid.Empty)))
                    return "DESTINATION_SCOPE_INVALID";
            }
            else return "DESTINATION_SCOPE_INVALID";
        }
        return null;
    }

    public static bool Country(string? code) => code is { Length: 2 } && code.All(c => c is >= 'A' and <= 'Z');

    private static bool Compatible(string transport, string from, string to) => transport switch
    {
        "MARITIME" => from == "CY" && to == "CY",
        "AIR" => from == "APT" && to == "APT",
        "ROAD" => (from is "CY" or "SD" or "APT") && (to is "CY" or "SD" or "APT"),
        _ => false
    };

    public static bool CanReadDestination(string country, bool action, bool countryGrant, bool allGrant, bool stageVisible) =>
        Country(country) && action && (countryGrant || allGrant) && stageVisible;

    // A derived itinerary indicator only, never the persisted IDTRA status.
    public static string Summarize(IReadOnlyCollection<OperationsItineraryLeg> legs) =>
        legs.Count == 0 ? "NOT_CONFIGURED" :
        legs.All(l => l.Status == "COMPLETED") ? "ALL_LEGS_COMPLETED" :
        legs.All(l => l.Status == "DRAFT") ? "DRAFT" : "INCOMPLETE";
}
