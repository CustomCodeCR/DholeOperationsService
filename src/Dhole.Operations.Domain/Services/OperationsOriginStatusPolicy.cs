using System.Text.Json;

namespace Dhole.Operations.Domain.Services;

/// <summary>Applicable operational states; departure is deliberately excluded.</summary>
public static class OperationsOriginStatusPolicy
{
    public static string[] Allowed(string modality, string openingDataJson)
    {
        string[] states = modality switch
        {
            "FCL" => ["ORIGIN_COORDINATION", "BOOKING_READY", "PORT_OF_LOADING"],
            "LCL" => ["ORIGIN_COORDINATION", "ORIGIN_WAREHOUSE", "BOOKING_READY", "PORT_OF_LOADING"],
            "AIR" => ["ORIGIN_COORDINATION", "ORIGIN_WAREHOUSE", "BOOKING_READY", "DEPARTURE_AIRPORT"],
            "FTL" or "MULTIMODAL" => ["ORIGIN_COORDINATION", "PREPARING_DISPATCH"],
            "LTL" => ["ORIGIN_COORDINATION", "ORIGIN_WAREHOUSE", "PREPARING_DISPATCH"],
            _ => ["ORIGIN_COORDINATION"]
        };
        if (!RequiresPickup(openingDataJson)) return states;
        return [states[0], "CARGO_PICKUP", .. states.Skip(1)];
    }

    private static bool RequiresPickup(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("requiresPickup", out var value) &&
                value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }
}
