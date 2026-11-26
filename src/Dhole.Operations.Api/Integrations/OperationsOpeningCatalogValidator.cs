using System.Net.Http.Headers;
using System.Text.Json;
using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.Api.Integrations;

/// <summary>Checks active Config catalog identities; client-provided terminal labels cannot authorize a route.</summary>
public sealed class OperationsOpeningCatalogValidator(IHttpClientFactory factory, IConfiguration configuration)
{
    public sealed record Resolution(string? ErrorCode, int StatusCode, string[] Fields);
    private sealed record Selection(string Group, string Field, string Id, string Code, string Label, string? Terminal);
    private static string Text(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";

    public async Task<Resolution> ValidateAsync(string openingData, string authorization, CancellationToken ct)
    {
        if (!Uri.TryCreate(configuration["Config:ApiBaseUrl"], UriKind.Absolute, out var baseUrl) ||
            (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(baseUrl.UserInfo))
            return new("CONFIG_NOT_CONFIGURED", 503, []);
        if (!AuthenticationHeaderValue.TryParse(authorization, out var bearer) ||
            !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            return new("AUTHORIZATION_REQUIRED", 401, []);

        try
        {
            using var document = JsonDocument.Parse(openingData);
            var data = document.RootElement;
            var terminal = Text(data, "transportType").ToUpperInvariant() switch { "AIR" => "APT", "ROAD" => "SD", _ => "CY" };
            var selections = new List<Selection>
            {
                new("pol", "pol", Text(data, "polCatalogId"), Text(data, "polCatalogCode"), Text(data, "pol"), terminal),
                new("poe", "poe", Text(data, "poeCatalogId"), Text(data, "poeCatalogCode"), Text(data, "poe"), terminal)
            };
            var pricingParity = data.TryGetProperty("openingValidationVersion", out var revision) && revision.ValueKind == JsonValueKind.Number && revision.TryGetInt32(out var rule) && rule >= 3;
            if (!pricingParity || (terminal == "CY" && Text(data, "pod").Length > 0))
                selections.Add(new("pod", "pod", Text(data, "podCatalogId"), Text(data, "podCatalogCode"), Text(data, "pod"), "SD"));
            var shipment = Text(data, "shipmentType").ToUpperInvariant();
            var warehouseRequired = pricingParity && (rule >= 5
                ? OperationsOpeningDraftPolicy.RequiresManualWarehouse(shipment,
                    Text(data, "incoterm") + " " + Text(data, "incotermName"), rule)
                : string.Equals(Text(data, "incoterm"), "FCA", StringComparison.OrdinalIgnoreCase));
            var warehouseGroups = new[] { "pricing-warehouses", "warehouses", "whs", "fca-warehouses" };
            if (warehouseRequired && Text(data, "warehouseId").Length > 0)
            {
                var group = Text(data, "warehouseCatalogGroup");
                if (!warehouseGroups.Contains(group)) return new("APERTURE_CATALOG_INVALID", 409, ["whsName"]);
                selections.Add(new(group, "whsName", Text(data, "warehouseId"), Text(data, "warehouseCatalogCode"), Text(data, "whsName"), null));
            }
            if ((shipment is "FCL" or "FTL") && data.TryGetProperty("equipment", out var equipment) && equipment.ValueKind == JsonValueKind.Array)
            {
                var group = shipment == "FTL" ? "land-equipment-sizes" : "container-types";
                foreach (var row in equipment.EnumerateArray())
                    selections.Add(new(group, "equipment", Text(row, "equipmentCatalogId"), Text(row, "equipmentCatalogCode"), Text(row, "equipmentType"), null));
            }
            if (pricingParity && revision.GetInt32() >= 4)
            {
                if (!data.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Array ||
                    !data.TryGetProperty("serviceSelections", out var serviceSelections) || serviceSelections.ValueKind != JsonValueKind.Array)
                    return new("APERTURE_CATALOG_INVALID", 409, ["services"]);
                var serviceIds = services.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToArray();
                var serviceRows = serviceSelections.EnumerateArray().ToArray();
                if (serviceIds.Length == 0 || serviceIds.Length != serviceRows.Length ||
                    serviceIds.Distinct().Count() != serviceIds.Length ||
                    !serviceIds.ToHashSet().SetEquals(serviceRows.Select(x => Text(x, "id"))))
                    return new("APERTURE_CATALOG_INVALID", 409, ["services"]);
                foreach (var row in serviceRows)
                    selections.Add(new("pricing-services", "services", Text(row, "id"), Text(row, "code"), Text(row, "label"), null));
                if (data.TryGetProperty("providerSelections", out var providers) && providers.ValueKind == JsonValueKind.Array)
                {
                    var seen = new HashSet<string>();
                    foreach (var row in providers.EnumerateArray())
                    {
                        var field = Text(row, "field");
                        var group = Text(row, "group");
                        if (!seen.Add(field) || !((field == "carrierReference" && group == "carriers" && terminal == "CY" && (revision.GetInt32() < 5 || shipment == "FCL")) ||
                            (field == "agentReference" && group == "agents")) ||
                            Text(row, "label") != Text(data, field))
                            return new("APERTURE_CATALOG_INVALID", 409, [field]);
                        selections.Add(new(group, field, Text(row, "id"), Text(row, "code"), Text(row, "label"), null));
                    }
                    foreach (var field in new[] { "carrierReference", "agentReference" })
                        if (Text(data, field).Length > 0 && !seen.Contains(field))
                            return new("APERTURE_CATALOG_INVALID", 409, [field]);
                }
            }
            var invalidIdentity = selections.Where(x => !Guid.TryParse(x.Id, out var id) || id == Guid.Empty || string.IsNullOrWhiteSpace(x.Code) || x.Code.Length > 200)
                .Select(x => x.Field).Distinct().ToArray();
            if (invalidIdentity.Length > 0) return new("APERTURE_CATALOG_INVALID", 409, invalidIdentity);
            if (selections.Count > 53) return new("APERTURE_CATALOG_INVALID", 409, ["equipment"]);

            // Searching by code bypasses Config's cached group snapshot.
            var client = factory.CreateClient("config-operations-opening");
            var invalid = new List<string>();
            // An address fallback is only permitted when no WHS catalog has active entries.
            if (warehouseRequired && Text(data, "warehouseId").Length == 0)
            {
                foreach (var group in warehouseGroups)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl.ToString().TrimEnd('/') + "/"), $"api/config/catalog-items/select?catalogGroupSlug={group}"));
                    request.Headers.Authorization = bearer;
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode) return new("CONFIG_UNAVAILABLE", 503, []);
                    using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    var list = Unwrap(json.RootElement);
                    if (list.ValueKind != JsonValueKind.Array) return new("CONFIG_INVALID_CONTRACT", 503, []);
                    if (list.EnumerateArray().Any(item => item.TryGetProperty("isActive", out var active) && active.ValueKind == JsonValueKind.True))
                        return new("APERTURE_CATALOG_INVALID", 409, ["whsName"]);
                }
            }
            foreach (var selection in selections)
            {
                var relative = $"api/config/catalog-items/select?catalogGroupSlug={Uri.EscapeDataString(selection.Group)}&search={Uri.EscapeDataString(selection.Code)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl.ToString().TrimEnd('/') + "/"), relative));
                request.Headers.Authorization = bearer;
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) return new("CONFIG_UNAVAILABLE", 503, []);
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var list = Unwrap(json.RootElement);
                if (list.ValueKind != JsonValueKind.Array) return new("CONFIG_INVALID_CONTRACT", 503, []);
                if (!list.EnumerateArray().Any(item => Matches(item, selection)))
                    invalid.Add(selection.Field);
            }
            return invalid.Count == 0 ? new(null, 200, []) : new("APERTURE_CATALOG_INVALID", 409, invalid.Distinct().ToArray());
        }
        catch (HttpRequestException) { return new("CONFIG_UNAVAILABLE", 503, []); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new("CONFIG_TIMEOUT", 503, []); }
        catch (JsonException) { return new("CONFIG_INVALID_CONTRACT", 503, []); }
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        for (var i = 0; i < 4 && root.ValueKind == JsonValueKind.Object; i++)
        {
            if (root.TryGetProperty("data", out var data)) root = data;
            else if (root.TryGetProperty("value", out var value)) root = value;
            else if (root.TryGetProperty("result", out var result)) root = result;
            else if (root.TryGetProperty("items", out var items)) root = items;
            else break;
        }
        return root;
    }

    private static bool Matches(JsonElement item, Selection selection)
    {
        if (!Guid.TryParse(Text(item, "id"), out var itemId) || !Guid.TryParse(selection.Id, out var selectedId) || itemId != selectedId ||
            !item.TryGetProperty("isActive", out var active) || active.ValueKind != JsonValueKind.True ||
            !string.Equals(Text(item, "code"), selection.Code, StringComparison.OrdinalIgnoreCase))
            return false;
        var label = new[] { Text(item, "value"), Text(item, "label"), Text(item, "code") }.FirstOrDefault(x => x.Length > 0);
        if (!string.Equals(label, selection.Label, StringComparison.OrdinalIgnoreCase)) return false;
        if (selection.Terminal is null) return true;
        var fallback = selection.Group == "pod" ? "SD" : "CY";
        var configured = "";
        try
        {
            var raw = Text(item, "metadataJson");
            using var metadata = JsonDocument.Parse(raw.Length > 0 ? raw : "{}");
            configured = Text(metadata.RootElement, "terminalType").ToUpperInvariant();
        }
        catch (JsonException) { /* Same fallback as Pricing catalog selector. */ }
        if (configured is not ("CY" or "SD" or "APT"))
        {
            var parts = Text(item, "code").ToUpperInvariant().Split('_');
            configured = new[] { "CY", "SD", "APT" }.FirstOrDefault(part => parts.Contains(part)) ?? fallback;
        }
        return configured == selection.Terminal;
    }
}
