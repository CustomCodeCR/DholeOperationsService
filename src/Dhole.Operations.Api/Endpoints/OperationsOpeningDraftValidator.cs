using System.Text.Json;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>
/// Minimal gate before two-person Aperturas validation. Saving an incomplete
/// manual opening is supported; routing it to Origen is not.
/// The pricing acceptance snapshot is separately validated by its own contract.
/// </summary>
public static class OperationsOpeningDraftValidator
{
    public static string[] MissingFields(string modality, string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return ["cargoDescription", "route"];

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return ["openingData"];

        var data = doc.RootElement;
        var missing = new List<string>();
        string? Text(string name)
        {
            if (!data.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        bool Flag(string name) =>
            data.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.True;

        if (string.IsNullOrWhiteSpace(Text("cargoDescription")))
            missing.Add("cargoDescription");
        if (string.IsNullOrWhiteSpace(Text("pol")))
            missing.Add("pol");
        if (string.IsNullOrWhiteSpace(Text("pod")))
            missing.Add("pod");
        if (string.IsNullOrWhiteSpace(Text("incoterm")))
            missing.Add("incoterm");

        if (modality == "FCL")
        {
            if (!data.TryGetProperty("equipment", out var equipment) ||
                equipment.ValueKind != JsonValueKind.Array ||
                !equipment.EnumerateArray().Any(x =>
                    x.ValueKind == JsonValueKind.Object &&
                    x.TryGetProperty("quantity", out var count) &&
                    count.TryGetInt32(out var quantity) && quantity > 0 &&
                    x.TryGetProperty("equipmentType", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(type.GetString())))
                missing.Add("equipment");
        }

        if (Text("incoterm") == "FCA" && (modality is "LCL" or "LTL") &&
            string.IsNullOrWhiteSpace(Text("whsName")))
            missing.Add("whsName");
        if (Flag("isImo") && (string.IsNullOrWhiteSpace(Text("imoClass")) ||
            string.IsNullOrWhiteSpace(Text("unNumber"))))
            missing.Add("imoClass/unNumber");
        if (Flag("overweight") && string.IsNullOrWhiteSpace(Text("overweightNotes")))
            missing.Add("overweightNotes");
        if (Flag("requiresPickup") && string.IsNullOrWhiteSpace(Text("pickupAddress")))
            missing.Add("pickupAddress");

        return missing.ToArray();
    }
}
