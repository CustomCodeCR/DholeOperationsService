using System.Text.Json;

namespace Dhole.Operations.Domain.Services;

/// <summary>
/// Same server-side conditional rules for the completeness preview and the
/// final Aperturas gate. Incomplete records are allowed as PostgreSQL drafts.
/// A confirmed commercial value is never changed by this evaluator.
/// </summary>
public static class OperationsOpeningDraftPolicy
{
    public sealed record FieldStatus(string FieldCode, string CompletionStatus, string? Reason = null);

    public static string[] MissingFields(string modality, string? json) =>
        EvaluateFields(modality, json)
            .Where(x => x.CompletionStatus == "PENDIENTE_BLOQUEANTE")
            .Select(x => x.FieldCode).ToArray();

    public static IReadOnlyList<FieldStatus> EvaluateFields(string modality, string? json)
    {
        var fields = new List<FieldStatus>();
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [new("openingData", "PENDIENTE_BLOQUEANTE", "Invalid opening object")];

            var data = document.RootElement;
            string? Text(string key) =>
                data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()?.Trim() : null;
            bool Flag(string key) => data.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.True;
            bool HasValue(string key) =>
                data.TryGetProperty(key, out var value) &&
                value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
                (value.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(value.GetString()));
            void Required(string key, bool satisfied, string? reason = null) =>
                fields.Add(new(key, satisfied ? "CONFIRMADO" : "PENDIENTE_BLOQUEANTE",
                    satisfied ? null : reason));
            void Deferred(string key, bool satisfied, string reason) =>
                fields.Add(new(key, satisfied ? "CONFIRMADO" : "PENDIENTE_PERMITIDO",
                    satisfied ? null : reason));
            void NotApplicable(string key, string reason) =>
                fields.Add(new(key, "NO_APLICA", reason));

            var mode = modality?.Trim().ToUpperInvariant() ?? "";
            var shipment = (Text("shipmentType") ?? mode).ToUpperInvariant();
            var transport = Text("transportType")?.ToUpperInvariant();
            var incoterm = Text("incoterm")?.ToUpperInvariant();
            Required("cargoDescription", !string.IsNullOrWhiteSpace(Text("cargoDescription")));
            Required("pol", !string.IsNullOrWhiteSpace(Text("pol")));
            Required("pod", !string.IsNullOrWhiteSpace(Text("pod")));
            Required("incoterm", !string.IsNullOrWhiteSpace(incoterm));

            var requiresEquipment = shipment is "FCL" or "FTL" ||
                (mode is "FCL" or "FTL");
            if (requiresEquipment)
            {
                var valid = data.TryGetProperty("equipment", out var equipment) &&
                    equipment.ValueKind == JsonValueKind.Array &&
                    equipment.GetArrayLength() > 0 &&
                    equipment.EnumerateArray().All(x =>
                        x.ValueKind == JsonValueKind.Object &&
                        x.TryGetProperty("quantity", out var count) &&
                        count.ValueKind == JsonValueKind.Number &&
                        count.TryGetInt32(out var quantity) && quantity > 0 &&
                        x.TryGetProperty("equipmentType", out var type) &&
                        type.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(type.GetString()));
                Required("equipment", valid, "Each equipment line needs a type and positive quantity");
            }
            else NotApplicable("equipment", "Shipment does not require full equipment");

            // FCA requires a real warehouse, including but not limited to LCL/LTL.
            if (incoterm == "FCA")
                Required("whsName", !string.IsNullOrWhiteSpace(Text("whsName")));
            else NotApplicable("whsName", "Incoterm does not require FCA warehouse");

            var pickup = Flag("requiresPickup");
            if (pickup)
            {
                Required("pickupAddress", !string.IsNullOrWhiteSpace(Text("pickupAddress")));
                Required("pickupContact", !string.IsNullOrWhiteSpace(Text("pickupContact")));
                Required("pickupWindow", !string.IsNullOrWhiteSpace(Text("pickupWindow")));
            }
            else
            {
                NotApplicable("pickupAddress", "No collection service selected");
                NotApplicable("pickupContact", "No collection service selected");
                NotApplicable("pickupWindow", "No collection service selected");
            }

            if (Flag("isImo"))
            {
                Required("imoClass", !string.IsNullOrWhiteSpace(Text("imoClass")));
                Required("unNumber", !string.IsNullOrWhiteSpace(Text("unNumber")));
            }
            else
            {
                NotApplicable("imoClass", "Cargo is not declared IMO");
                NotApplicable("unNumber", "Cargo is not declared IMO");
            }

            if (Flag("overweight"))
                Required("overweightNotes", !string.IsNullOrWhiteSpace(Text("overweightNotes")));
            else NotApplicable("overweightNotes", "No overweight declared");

            if (shipment is "LCL" or "LTL")
                Deferred("stackable", HasValue("stackable"), "Origin may confirm stacking");
            else NotApplicable("stackable", "Stackability is only applicable to LCL/LTL");

            // Real weight belongs exclusively to Coordinación Origen on manual openings.
            Deferred("actualWeight", false, "Coordinación Origen confirms actual weight");
            Deferred("volume", HasValue("volume") || HasValue("volumeCbm"),
                "Volume may be confirmed during origin coordination");
            if (shipment is "LCL" or "LTL")
                Deferred("consolidation", HasValue("consolidation"),
                    "Consolidation can be confirmed by origin");
            else NotApplicable("consolidation", "Consolidation not required for this shipment");
            if (transport is "AIR" or "AEREO" or "AÉREO" or "TERRESTRIAL" or "LAND")
                NotApplicable("carrierReference", "Maritime carrier is not applicable");
            else
                Deferred("carrierReference", HasValue("carrierReference"),
                    "Carrier may be confirmed by origin");

            return fields;
        }
        catch (JsonException)
        {
            return [new("openingData", "PENDIENTE_BLOQUEANTE", "Invalid opening JSON")];
        }
        finally { document?.Dispose(); }
    }
}
