using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dhole.Operations.Domain.Services;

/// <summary>Original measurements stay independent of their normalized counterparts.
/// Actual weight is written only by the Origin workflow, never by Aperturas.</summary>
public static class OperationsCargoMeasurements
{
    private static readonly string[] OriginKeys = ["actualWeightValue", "actualWeightUnit", "actualWeightKg", "equipmentUnits"];
    private static readonly string[] LegacyWeightKeys = ["weightValue", "weightUnit", "weightKg"];
    public static JsonObject Parse(string json) => JsonNode.Parse(json) as JsonObject
        ?? throw new ArgumentException("Opening data must be an object.");
    private static string? Text(JsonNode? node) => node is JsonValue ? node.ToString().Trim() : null;
    public static decimal? Positive(JsonNode? node) =>
        decimal.TryParse(Text(node), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
        && amount > 0 && amount <= 1000000000000m ? amount : null;

    public static decimal ToKg(decimal value, string unit) => value > 0 && value <= 1000000000000m
        ? unit switch { "KG" => value, "LB" => value * 0.45359237m, _ => throw new ArgumentException("Invalid weight unit.") }
        : throw new ArgumentException("Invalid weight.");

    private static void Normalize(JsonObject data, string valueKey, string unitKey, string normalizedKey,
        IReadOnlyDictionary<string, decimal> factors)
    {
        if (string.IsNullOrWhiteSpace(Text(data[valueKey]))) return;
        var value = Positive(data[valueKey]) ?? throw new ArgumentException("Invalid measurement: " + valueKey);
        var unit = Text(data[unitKey])?.ToUpperInvariant() ?? "";
        if (!factors.TryGetValue(unit, out var factor)) throw new ArgumentException("Invalid measurement unit: " + unitKey);
        data[unitKey] = unit;
        data[normalizedKey] = value * factor;
    }

    public static void NormalizeOpening(JsonObject data)
    {
        Normalize(data, "estimatedWeightValue", "estimatedWeightUnit", "estimatedWeightKg",
            new Dictionary<string, decimal> { ["KG"] = 1, ["LB"] = 0.45359237m });
        Normalize(data, "volumeValue", "volumeUnit", "volumeCbm",
            new Dictionary<string, decimal> { ["CBM"] = 1, ["CFT"] = 0.028316846592m });
        foreach (var axis in new[] { "length", "width", "height" })
            Normalize(data, axis + "Value", axis + "Unit", axis + "Cm",
                new Dictionary<string, decimal> { ["CM"] = 1, ["M"] = 100, ["IN"] = 2.54m });
    }

    public static void RejectOriginOnCreate(JsonObject data)
    {
        if (OriginKeys.Any(data.ContainsKey) ||
            !string.IsNullOrWhiteSpace(Text(data["weightValue"])) ||
            !string.IsNullOrWhiteSpace(Text(data["weightKg"])))
            throw new ArgumentException("Actual weight and individual equipment belong to Origin.");
    }

    public static void PreserveOriginFields(JsonObject incoming, JsonObject previous)
    {
        foreach (var key in OriginKeys.Concat(LegacyWeightKeys))
        {
            var old = previous[key];
            var next = incoming[key];
            if (next is not null && !string.IsNullOrWhiteSpace(Text(next)) &&
                !JsonNode.DeepEquals(old, next))
                throw new ArgumentException("Origin-only field: " + key);
            // Objects and arrays must also be protected, including null or missing data.
            if (next is JsonArray or JsonObject && !JsonNode.DeepEquals(old, next))
                throw new ArgumentException("Origin-only field: " + key);
            if (previous.ContainsKey(key)) incoming[key] = old?.DeepClone();
            else incoming.Remove(key);
        }
        if (previous.ContainsKey("quotedWeightKg"))
            incoming["quotedWeightKg"] = previous["quotedWeightKg"]?.DeepClone();
        else incoming.Remove("quotedWeightKg");
    }

    public static void ConfirmWeight(JsonObject data, decimal value, string unit)
    {
        unit = unit.Trim().ToUpperInvariant();
        var normalized = ToKg(value, unit);
        if (data["estimatedWeightValue"] is null && Positive(data["quotedWeightKg"]) is decimal quoted)
        {
            data["estimatedWeightValue"] = quoted;
            data["estimatedWeightUnit"] = "KG";
            data["estimatedWeightKg"] = quoted;
        }
        data["actualWeightValue"] = value;
        data["actualWeightUnit"] = unit;
        data["actualWeightKg"] = normalized;
    }
}
