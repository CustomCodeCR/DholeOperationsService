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
            lclGroupAgentReference = Text(providers, "lclGroupAgentName"),
            merchantReference = Text(providers, "merchantName"),
            warehouseId = Text(e, "warehouseId"), carrierRoute = Text(e, "carrierRoute"),
            cargoDescription = OperationsQuoteComparison.CargoDescription(cargo),
            weightValue = Number(cargo, "weightKg"), volumeValue = Number(cargo, "volumeCbm"),
            packageCount = Number(cargo, "packages"), palletCount = Number(cargo, "pallets"),
            chargeableQuantity = Number(cargo, "chargeableQuantity"), validFrom = Text(e, "validFrom"), validTo = Text(e, "validTo"),
            freeDays = Number(e, "freeDays"),
            weightUnit = "KG", volumeUnit = "CBM",
            supportDocuments = DocumentReferences(e),
            services = Get(e, "services").ValueKind == JsonValueKind.Array ? Get(e, "services").EnumerateArray()
                .Select(x => Text(x, "serviceName")).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() : [],
            equipment = Get(e, "equipment").ValueKind == JsonValueKind.Array ? Get(e, "equipment").EnumerateArray()
                .Select(x => new { equipmentType = Text(x, "equipmentCode"), equipmentName = Text(x, "equipmentName"), quantity = Number(x, "quantity") }).ToArray() : []
        };
    }
    private static IReadOnlyList<object> DocumentReferences(JsonElement e)
    {
        var ids = Get(e, "documentStorageIds");
        if (ids.ValueKind != JsonValueKind.Array) return [];
        return ids.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => Guid.TryParse(x.GetString(), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct()
            .Select(id => (object)new { id, categoryLabel = "Adjunto de Pricing", fileName = "Adjunto de Pricing", storageId = id })
            .ToArray();
    }
    public static object? Conditions(string? json)
    {
        var e = Read(json);
        if (e.ValueKind != JsonValueKind.Object) return null;
        return new { includes = Text(e, "includes"), excludes = Text(e, "excludes"), subjectTo = Text(e, "subjectTo") };
    }
    public static IReadOnlyList<SnapshotCost> Costs(string? json, bool canSeeSale)
    {
        var e = Read(json);
        var lines = Get(e, "costLines");
        if (lines.ValueKind != JsonValueKind.Array) return [];
        return lines.EnumerateArray().Select((line, index) => {
            var quantity = Number(line, "commercialQuantity") ?? Number(line, "quantity");
            var cost = Number(line, "commercialUnitCost") ?? Number(line, "unitCost");
            var sale = canSeeSale ? Number(line, "commercialUnitSale") ?? Number(line, "unitSale") : null;
            return new SnapshotCost(Text(line, "rateDetailId") ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Text(line, "description"), Text(line, "costType"), Text(line, "supplierName"),
                Text(line, "currencyCode"), Text(line, "currencyName"), quantity, cost,
                Number(line, "commercialCostSubtotal") ?? Multiply(quantity, cost),
                canSeeSale ? Number(line, "commercialSaleSubtotal") ?? Multiply(quantity, sale) : null,
                Text(line, "costDetailType"), Text(line, "chargeBasis"),
                Text(line, "sourceCostId"), Text(line, "supplierId"),
                canSeeSale ? Number(line, "commercialTaxAmount") : null,
                canSeeSale ? Number(line, "commercialSaleTotal") : null);
        }).ToArray();
    }
    /// <summary>Provisional, fail-closed Origin projection. Ownership is supplied
    /// by trusted server configuration for this case snapshot, never by a request.
    /// Only stable unique Pricing row IDs and verified Origin categories qualify.
    /// No sale, tax, margin, utility or commercial total exists in this DTO.</summary>
    public static IReadOnlyList<OperationalCost> OriginCosts(string? json, IReadOnlySet<Guid> authorizedLineIds)
    {
        if (authorizedLineIds.Count == 0) return [];
        var rows = Costs(json, false)
            .Select(row => (Row: row, Id: Guid.TryParse(row.Id, out var id) ? id : Guid.Empty))
            .Where(x => x.Id != Guid.Empty)
            .GroupBy(x => x.Id)
            .Where(g => g.Count() == 1 && authorizedLineIds.Contains(g.Key))
            .Select(g => g.Single());
        return rows.Where(x => x.Row.CostDetailType is "OriginCharge" or "Freight" or "AgentCharge" or "InlandTransport" &&
                x.Row.CostAmount is >= 0m && x.Row.Quantity is > 0m &&
                !string.IsNullOrWhiteSpace(x.Row.CurrencyCode) && !string.IsNullOrWhiteSpace(x.Row.ChargeBasis))
            .Select(x => new OperationalCost(x.Id, x.Row.Description, x.Row.SupplierName,
                x.Row.CurrencyCode!, x.Row.ChargeBasis!, x.Row.Quantity!.Value,
                x.Row.CostAmount!.Value, x.Row.CostDetailType!, "PENDING"))
            .ToArray();
    }

    // Historical acceptance can enumerate multiple IDTRAs before all have been
    // materialized locally. Invalid JSON / invalid cardinality also fail closed.
    public static bool HasUnambiguousHistoricalIdtra(string? json, string idtra)
    {
        var root = Read(json);
        if (root.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(idtra)) return false;
        var multiple = Get(root, "historicalIdtras");
        if (multiple.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            if (multiple.ValueKind != JsonValueKind.Array) return false;
            var ids = multiple.EnumerateArray().ToArray();
            if (ids.Length != 1 || ids[0].ValueKind != JsonValueKind.String ||
                !string.Equals(ids[0].GetString(), idtra, StringComparison.Ordinal)) return false;
        }
        var single = Get(root, "historicalIdtra");
        return single.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
            single.ValueKind == JsonValueKind.String &&
            string.Equals(single.GetString(), idtra, StringComparison.Ordinal);
    }

    public sealed record OperationalCost(Guid Id, string? Description, string? SupplierName,
        string CurrencyCode, string ChargeBasis, decimal Quantity, decimal QuotedAmount,
        string CostDetailType, string Status);

    public sealed record SnapshotCost(string Id, string? Description, string? CostCode, string? SupplierName,
        string? CurrencyCode, string? CurrencyName, decimal? Quantity, decimal? UnitCost,
        decimal? CostAmount, decimal? SaleAmount, string? CostDetailType, string? ChargeBasis,
        string? SourceCostId, string? SupplierId, decimal? TaxAmount, decimal? SaleTotal);
}
