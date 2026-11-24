using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dhole.Operations.Domain.Models;

namespace Dhole.Operations.Persistence;

/// <summary>Compare the persisted accepted snapshot with independent operational data.
/// Never fill missing operational fields from the quote before comparing.</summary>
public static class OperationsQuoteComparison
{
    public sealed record Row(string Field, string Label, string? Quoted, string? Current, string Status);
    public sealed record Result(bool HasSnapshot, string Fingerprint, IReadOnlyList<Row> Rows)
    {
        public IEnumerable<Row> Differences => Rows.Where(x => x.Status == "CHANGED");
    }
    private static JsonElement Parse(string? json)
    {
        try { using var doc = JsonDocument.Parse(json ?? "{}"); return doc.RootElement.Clone(); }
        catch (JsonException) { return default; }
    }
    private static JsonElement Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
    private static string? Text(JsonElement e, string name)
    {
        var value = Get(e, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() :
            value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
    }
    private static string? Label(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Guid.TryParse(value, out _) ||
            System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Z][A-Z0-9]+-\d{4}-\d+$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return null;
        return value.Trim();
    }
    private static decimal? Number(JsonElement e, string name) =>
        decimal.TryParse(Text(e, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;
    private static string? Quantity(decimal? value, string unit) =>
        value.HasValue ? value.Value.ToString("0.####", CultureInfo.InvariantCulture) + " " + unit : null;
    private static decimal? CurrentQuantity(JsonElement e, string valueKey, string unitKey, string normalKey,
        string canonical, string alternate, decimal factor)
    {
        var amount = Number(e, valueKey);
        var unit = Text(e, unitKey)?.ToUpperInvariant();
        return amount.HasValue && unit == canonical ? amount :
            amount.HasValue && unit == alternate ? amount * factor : Number(e, normalKey);
    }
    private static string? Equipment(JsonElement data, string nameKey, string codeKey)
    {
        if (data.ValueKind != JsonValueKind.Array) return null;
        var values = new List<(string Name, decimal Count)>();
        foreach (var item in data.EnumerateArray())
        {
            var name = Label(Text(item, codeKey)) ?? Label(Text(item, nameKey));
            var count = Number(item, "quantity");
            if (name is null || !count.HasValue) return null;
            values.Add((name.ToUpperInvariant(), count.Value));
        }
        return values.Count == 0 ? "Sin equipos" : string.Join(", ", values.GroupBy(x => x.Name)
            .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Sum(v => v.Count)
                .ToString("0.####", CultureInfo.InvariantCulture) + " × " + x.Key));
    }
    private static string? IncotermCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(raw.ToUpperInvariant(),
            @"\b(EXW|FCA|FAS|FOB|CFR|CIF|CPT|CIP|DAP|DPU|DDP)\b");
        return match.Success ? match.Value : null;
    }
    private static string? Term(JsonElement e) =>
        IncotermCode(Text(e, "incoterm")) ?? IncotermCode(Text(e, "incotermName")) ??
        Label(Text(e, "incotermName")) ?? Label(Text(e, "incoterm"));

    public static string? CargoDescription(JsonElement cargo)
    {
        var lines = Parse(Text(cargo, "cargoLinesJson"));
        if (lines.ValueKind != JsonValueKind.Array) return null;
        return lines.EnumerateArray().Select(line => Label(Text(line, "description")) ??
            Label(Text(line, "Description"))).FirstOrDefault(name => name is not null);
    }
    public static Result Compare(OperationsCase operation, OperationsAcceptedQuoteSnapshot? snapshot)
    {
        if (snapshot is null) return new(false, "", []);
        var quote = Parse(snapshot.SnapshotJson);
        var current = Parse(operation.OpeningDataJson);
        if (quote.ValueKind != JsonValueKind.Object) return new(false, "", []);
        var route = Get(quote, "route");
        var cargo = Get(quote, "cargo");
        var providers = Get(quote, "providers");
        var rows = new List<Row>();
        void Add(string key, string label, string? quoted, string? actual, bool knownEqual = false)
        {
            quoted = Label(quoted); actual = Label(actual);
            var status = knownEqual ? "MATCH" : quoted is null || actual is null ? "UNCONFIRMED" :
                string.Equals(quoted, actual, StringComparison.OrdinalIgnoreCase) ? "MATCH" : "CHANGED";
            rows.Add(new(key, label, quoted, actual, status));
        }
        Add("shipmentType", "Tipo de embarque", Text(quote, "shipmentMode")?.ToUpperInvariant(), operation.ModalityCode.ToUpperInvariant());
        var quoteIncoterm = Text(quote, "incoterm");
        var actualIncoterm = Text(current, "incoterm");
        Add("incoterm", "Incoterm", Term(quote), Term(current),
            !string.IsNullOrWhiteSpace(quoteIncoterm) && string.Equals(quoteIncoterm, actualIncoterm, StringComparison.OrdinalIgnoreCase));
        Add("pol", "POL · Origen", Text(route, "polName"), Text(current, "pol"));
        Add("poe", "POE · Entrada", Text(route, "poeName"), Text(current, "poe"));
        Add("pod", "POD · Destino", Text(route, "podName"), Text(current, "pod"));
        Add("cargoDescription", "Descripción de mercancía", CargoDescription(cargo), Text(current, "cargoDescription"));
        Add("packageCount", "Bultos", Quantity(Number(cargo, "packages"), "bultos"),
            Quantity(Number(current, "packageCount") ?? Number(current, "packages"), "bultos"));
        Add("weightValue", "Peso (kg)", Quantity(Number(cargo, "weightKg"), "kg"),
            Quantity(CurrentQuantity(current, "weightValue", "weightUnit", "weightKg", "KG", "LB", 0.45359237m), "kg"));
        Add("volumeValue", "Volumen (CBM)", Quantity(Number(cargo, "volumeCbm"), "CBM"),
            Quantity(CurrentQuantity(current, "volumeValue", "volumeUnit", "volumeCbm", "CBM", "CFT", 0.028316846592m), "CBM"));
        Add("equipment", "Equipos y cantidades", Equipment(Get(quote, "equipment"), "equipmentName", "equipmentCode"),
            Equipment(Get(current, "equipment"), "equipmentName", "equipmentType"));
        Add("carrierReference", "Naviera", Text(providers, "carrierName"), Text(current, "carrierReference"));
        Add("agentReference", "Agente / co-loader", Text(providers, "agentName"), Text(current, "agentReference"));
        Add("pickupAddress", "Dirección de recolecta", Text(quote, "pickupAddress"), Text(current, "pickupAddress"));
        // Optional fields absent in both records are not a discrepancy.
        rows.RemoveAll(x => x.Quoted is null && x.Current is null && x.Status != "MATCH" &&
            x.Field is "pickupAddress" or "carrierReference" or "agentReference" or "equipment" or "cargoDescription");
        var changes = rows.Where(x => x.Status == "CHANGED").ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { snapshot.QuoteId, snapshot.AcceptedOptionId,
                snapshot.CommercialRevision, changes }))));
        return new(true, fingerprint, rows);
    }

    public static string Severity(Result comparison, string? reviewStatus) =>
        !comparison.HasSnapshot ? "UNKNOWN" :
        comparison.Differences.Any() ? reviewStatus switch {
            "RESOLVED_NO_PRICE_CHANGE" => comparison.Rows.Any(x => x.Status == "UNCONFIRMED") ? "YELLOW" : "GREEN",
            "PENDING_PRICING" => "YELLOW",
            _ => "RED"
        } : comparison.Rows.Any(x => x.Status == "UNCONFIRMED") ? "YELLOW" : "GREEN";
}
