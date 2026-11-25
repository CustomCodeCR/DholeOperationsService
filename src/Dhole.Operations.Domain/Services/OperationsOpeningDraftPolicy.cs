using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Dhole.Operations.Domain.Services;

/// <summary>
/// Same server-side conditional rules for the completeness preview and the
/// final Aperturas gate. Incomplete records are allowed as PostgreSQL drafts.
/// A confirmed commercial value is never changed by this evaluator.
/// </summary>
public static class OperationsOpeningDraftPolicy
{
    public sealed record FieldStatus(string FieldCode, string CompletionStatus, string? Reason = null);

    public static string[] MissingFields(string modality, string? json, bool manualOpening = false) =>
        EvaluateFields(modality, json, manualOpening)
            .Where(x => x.CompletionStatus == "PENDIENTE_BLOQUEANTE")
            .Select(x => x.FieldCode).ToArray();

    public static IReadOnlyList<FieldStatus> EvaluateFields(string modality, string? json, bool manualOpening = false)
    {
        var fields = new List<FieldStatus>();
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [new("openingData", "PENDIENTE_BLOQUEANTE", "Invalid opening object")];

            var data = document.RootElement;
            var revision = data.TryGetProperty("openingValidationVersion", out var ruleVersion) && ruleVersion.ValueKind == JsonValueKind.Number && ruleVersion.TryGetInt32(out var parsedVersion) ? parsedVersion : 0;
            var strictManual = manualOpening || revision >= 2;
            var pricingParity = revision >= 3;
            bool ValidPoints(string key) =>
                data.TryGetProperty(key, out var points) && points.ValueKind == JsonValueKind.Array &&
                points.GetArrayLength() > 0 && points.EnumerateArray().All(point =>
                    point.ValueKind == JsonValueKind.Object &&
                    point.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(address.GetString()) &&
                    point.TryGetProperty("cargoCondition", out var condition) && condition.ValueKind == JsonValueKind.String &&
                    condition.GetString() is "FiscalCargo" or "NationalizedCargo");
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
            var term = Regex.Match((Text("incoterm") + " " + Text("incotermName")).ToUpperInvariant(),
                @"\b(EXW|FCA|FAS|FOB|CFR|CIF|CPT|CIP|DAP|DPU|DDP)\b", RegexOptions.CultureInvariant);
            var incoterm = term.Success ? term.Value : null;
            var nonMaritime = transport is "AIR" or "AIRCONSOL" or "AEREO" or "AÉREO" or "TERRESTRIAL" or "LAND" or "ROAD" or "TERRESTRE" ||
                (string.IsNullOrWhiteSpace(transport) && (shipment is "AIR" or "AIRCONSOL" or "FTL" or "LTL"));
            bool Positive(string key) =>
                data.TryGetProperty(key, out var value) &&
                decimal.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() :
                    value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null,
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && amount > 0;
            var direction = Text("operationDirection")?.ToUpperInvariant();
            if (strictManual)
            {
                var compatible = transport switch
                {
                    "MARITIME" => (shipment is "FCL" or "LCL") && mode == shipment,
                    "AIR" => shipment == "AIR" && mode == shipment,
                    "ROAD" => (shipment is "FTL" or "LTL") && mode == shipment,
                    _ => false
                };
                Required("shipmentType", compatible, "Shipment must be compatible with transport and case modality");
                Required("operationDirection", direction is "IMPORT" or "EXPORT" or "OTHER");
                var terminal = transport switch { "AIR" => "APT", "ROAD" => "SD", "MARITIME" => "CY", _ => "" };
                Required("poe", !string.IsNullOrWhiteSpace(Text("poe")));
                Required("polTerminalType", terminal.Length > 0 && Text("polTerminalType") == terminal, "Origin terminal incompatible with transport");
                Required("poeTerminalType", terminal.Length > 0 && Text("poeTerminalType") == terminal, "Entry terminal incompatible with transport");
                if (!pricingParity || (transport == "MARITIME" && HasValue("pod")))
                    Required("podTerminalType", Text("podTerminalType") == "SD", "Final destination must be a delivery location");
                else if (transport is "AIR" or "ROAD")
                    Required("pod", !HasValue("pod") && !HasValue("podCatalogId"), "Air and road use only POL and POE");
                else NotApplicable("podTerminalType", "Optional maritime POD not selected");
                Required("estimatedWeight", Positive("estimatedWeightValue") && Text("estimatedWeightUnit") is "KG" or "LB");
                if (shipment == "AIR")
                    Required("dimensions", Positive("lengthCm") && Positive("widthCm") && Positive("heightCm"));
                if (pricingParity && (Flag("requiresDelivery") || incoterm is "DAP" or "DPU" or "DDP"))
                    Required("deliveryLocations", ValidPoints("deliveryLocations"), "Add the agreed delivery point");
                else if (incoterm is "DAP" or "DPU" or "DDP")
                    Required("destinationAddress", !string.IsNullOrWhiteSpace(Text("destinationAddress")));
                Required("portHandlingMode", transport == "MARITIME" || !HasValue("portHandlingMode"), "Port handling is not applicable to this transport");
                if (manualOpening)
                    Required("actualWeightInput", !HasValue("weightValue") && !HasValue("weightKg"), "Actual weight is confirmed by origin coordination");
            }

            Required("cargoDescription", !string.IsNullOrWhiteSpace(Text("cargoDescription")));
            Required("pol", !string.IsNullOrWhiteSpace(Text("pol")));
            if (!strictManual && Text("source") is "PRICING" or "PRICING_HISTORICAL")
            {
                Required("poe", !string.IsNullOrWhiteSpace(Text("poe")));
                if (nonMaritime || !HasValue("pod")) NotApplicable("pod", "No separate final destination selected");
                else Required("pod", !string.IsNullOrWhiteSpace(Text("pod")));
            }
            else if (!pricingParity) Required("pod", !string.IsNullOrWhiteSpace(Text("pod")));
            else if (transport == "MARITIME" && !HasValue("pod")) NotApplicable("pod", "Optional maritime final destination");
            Required("incoterm", incoterm is not null && !(nonMaritime && (incoterm is "FAS" or "FOB" or "CFR" or "CIF")),
                "A recognized Incoterm applicable to the transport is required");

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
            else if (strictManual && data.TryGetProperty("equipment", out var incompatibleEquipment) &&
                !(incompatibleEquipment.ValueKind == JsonValueKind.Array && incompatibleEquipment.GetArrayLength() == 0))
                Required("equipment", false, "This shipment must not contain full containers or trailers");
            else NotApplicable("equipment", "Shipment does not require full equipment");

            // Historical cases retain the original warehouse rule; new manual
            // FCA collections validate collection details instead of a fictitious WHS.
            if (incoterm == "FCA" && (pricingParity || !strictManual || !Flag("requiresPickup")))
                Required("whsName", pricingParity ? (HasValue("warehouseId") || HasValue("warehouseAddress")) && HasValue("whsName") : HasValue("whsName") || (!strictManual && HasValue("warehouseId")));
            else NotApplicable("whsName", "Incoterm does not require FCA warehouse");

            var pickup = (strictManual && incoterm == "EXW" && direction == "IMPORT") || Flag("requiresPickup") ||
                (!HasValue("requiresPickup") && !string.IsNullOrWhiteSpace(Text("pickupAddress")));
            if (pricingParity && (incoterm == "EXW" || pickup))
                Required("pickupLocations", ValidPoints("pickupLocations"), "Add a collection point as in Pricing");
            // Pickup contact and time window are not opening requirements. Preserve any
            // historical JSON values without evaluating them as completeness fields.
            if (pickup)
            {
                Required("pickupAddress", !string.IsNullOrWhiteSpace(Text("pickupAddress")));
            }
            else
            {
                NotApplicable("pickupAddress", "No collection service selected");
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
                Deferred("stackable", data.TryGetProperty("stackable", out var stacking) &&
                    (stacking.ValueKind is JsonValueKind.True or JsonValueKind.False), "Origin may confirm stacking");
            else NotApplicable("stackable", "Stackability is only applicable to LCL/LTL");

            // Booking and transport references can be confirmed after opening.
            var air = shipment is "AIR" or "AIRCONSOL" || transport == "AIR";
            var maritime = !nonMaritime && (shipment is "FCL" or "LCL" || transport == "MARITIME");
            if (maritime || air) Deferred("bookingNumber", !string.IsNullOrWhiteSpace(Text("bookingNumber")), "Origin confirms booking");
            else NotApplicable("bookingNumber", "No maritime or air booking");
            if (maritime) Deferred("blNumber", !string.IsNullOrWhiteSpace(Text("blNumber")), "Documentation confirms BL");
            else NotApplicable("blNumber", "Not a maritime shipment");
            if (maritime && shipment == "FCL") Deferred("containerNumber", !string.IsNullOrWhiteSpace(Text("containerNumber")), "Origin confirms container");
            else NotApplicable("containerNumber", "No dedicated maritime container");
            if (air) Deferred("airline", !string.IsNullOrWhiteSpace(Text("airline")) || !string.IsNullOrWhiteSpace(Text("carrierReference")), "Origin confirms airline");
            else NotApplicable("airline", "Not an air shipment");

            // Real weight belongs exclusively to Coordinación Origen on manual openings.
            Deferred("actualWeight", false, "Coordinación Origen confirms actual weight");
            if (strictManual && (shipment is "LCL" or "LTL" or "AIR"))
                Required("volume", Positive("volumeValue") && Text("volumeUnit") is "CBM" or "CFT");
            else
                Deferred("volume", Positive("volume") || Positive("volumeCbm") || Positive("volumeValue"),
                    "Volume may be confirmed during origin coordination");
            if (shipment is "LCL" or "LTL")
                Deferred("consolidation", HasValue("consolidationReference") || HasValue("consolidation"),
                    "Consolidation can be confirmed by origin");
            else NotApplicable("consolidation", "Consolidation not required for this shipment");
            if (nonMaritime)
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
