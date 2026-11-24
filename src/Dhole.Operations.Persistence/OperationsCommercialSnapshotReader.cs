using System.Text.Json;

namespace Dhole.Operations.Persistence;

/// <summary>Read-only projections of the immutable accepted commercial snapshot.
/// No raw snapshot is returned: financial data remains restricted to the cost endpoint.</summary>
public static class OperationsCommercialSnapshotReader
{
    private static JsonElement Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
    private static string? Text(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    private static decimal? Number(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDecimal(out var n) ? n : null;
    private static decimal? Multiply(decimal? a, decimal? b)
    {
        try { return a * b; } catch (OverflowException) { return null; }
    }
    private static JsonElement Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
        catch (JsonException) { return default; }
    }
    public static string? ClientName(string? json) => Text(Read(json), "clientName");
    public static object? Summary(string? json)
    {
        var e = Read(json);
        if (e.ValueKind != JsonValueKind.Object) return null;
        var route = Get(e, "route");
        var providers = Get(e, "providers");
        var cargo = Get(e, "cargo");
        return new {
            incoterm = Text(e, "incoterm"), incotermName = Text(e, "incotermName"),
            provenance = Text(e, "operationType"), shipmentType = Text(e, "shipmentMode"),
            pol = Text(route, "polName"), poe = Text(route, "poeName"), pod = Text(route, "podName"),
            pickupAddress = Text(e, "pickupAddress"), transitTime = Text(e, "transitTime"),
            quoteReferences = Text(e, "otherReferences"),
            executiveName = Text(e, "commercialExecutiveName"), clientName = Text(e, "clientName"),
            carrierReference = Text(providers, "carrierName"), agentReference = Text(providers, "agentName"),
            weightValue = Number(cargo, "weightKg"), volumeValue = Number(cargo, "volumeCbm"),
            packageCount = Number(cargo, "packages"), validFrom = Text(e, "validFrom"), validTo = Text(e, "validTo"),
            freeDays = Number(e, "freeDays")
        };
    }
    public static IReadOnlyList<SnapshotCost> Costs(string? json, bool canSeeSale)
    {
        var e = Read(json);
        var lines = Get(e, "costLines");
        if (lines.ValueKind != JsonValueKind.Array) return [];
        return lines.EnumerateArray().Select((line, index) => {
            var quantity = Number(line, "quantity");
            var cost = Number(line, "unitCost");
            var sale = canSeeSale ? Number(line, "unitSale") : null;
            return new SnapshotCost(index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Text(line, "description"), Text(line, "costType"), Text(line, "supplierName"),
                Text(line, "currencyCode"), Text(line, "currencyName"), quantity, cost,
                Multiply(quantity, cost), Multiply(quantity, sale));
        }).ToArray();
    }
    public sealed record SnapshotCost(string Id, string? Description, string? CostCode, string? SupplierName,
        string? CurrencyCode, string? CurrencyName, decimal? Quantity, decimal? UnitCost,
        decimal? CostAmount, decimal? SaleAmount);
}
