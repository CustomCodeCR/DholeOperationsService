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

    public static bool RequiresManualWarehouse(string shipment, string? incoterm, int revision)
    {
        var mode = shipment.Trim().ToUpperInvariant();
        var term = Regex.Match((incoterm ?? "").ToUpperInvariant(),
            @"\b(EXW|FCA|FAS|FOB|CFR|CIF|CPT|CIP|DAP|DPU|DDP)\b", RegexOptions.CultureInvariant);
        return (mode is "LCL" or "LTL") ||
            (revision >= 6 && (mode is "FCL" or "FTL") && term.Value == "FCA");
    }

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
            var basicManual = manualOpening && revision >= 5;
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
                Required("transportType", transport is "MARITIME" or "AIR" or "ROAD", "Select a compatible transport");
                Required("shipmentType", compatible, "Shipment must be compatible with transport and case modality");
                Required("operationDirection", direction is "IMPORT" or "EXPORT" or "OTHER");
                var terminal = transport switch { "AIR" => "APT", "ROAD" => "SD", "MARITIME" => "CY", _ => "" };
                Required("poe", !string.IsNullOrWhiteSpace(Text("poe")));
                Required("polTerminalType", terminal.Length > 0 && Text("polTerminalType") == terminal, "Origin terminal incompatible with transport");
                Required("poeTerminalType", terminal.Length > 0 && Text("poeTerminalType") == terminal, "Entry terminal incompatible with transport");
                if (!pricingParity || (transport == "MARITIME" && HasValue("pod")))
                    Required("podTerminalType", Text("podTerminalType") == "SD", "Final destination must be a delivery location");
                else if (transport is "AIR" or "ROAD")
                {
                    if (HasValue("pod") || HasValue("podCatalogId")) Required("pod", false, "Air and road use only POL and POE");
                    else NotApplicable("pod", "Air and road use only POL and POE");
                }
                else NotApplicable("podTerminalType", "Optional maritime POD not selected");
                Required("estimatedWeight", Positive("estimatedWeightValue") && Text("estimatedWeightUnit") is "KG" or "LB");
                if (shipment == "AIR" && basicManual)
                    Deferred("dimensions", Positive("lengthCm") && Positive("widthCm") && Positive("heightCm"), "Origin confirms dimensions");
                else if (shipment == "AIR")
                    Required("dimensions", Positive("lengthCm") && Positive("widthCm") && Positive("heightCm"));
                else NotApplicable("dimensions", "Air cargo dimensions are not required for this shipment");
                if (basicManual && (Flag("requiresDelivery") || incoterm is "DAP" or "DPU" or "DDP"))
                    Deferred("deliveryLocations", ValidPoints("deliveryLocations"), "Origin confirms delivery");
                else if (pricingParity && (Flag("requiresDelivery") || incoterm is "DAP" or "DPU" or "DDP"))
                    Required("deliveryLocations", ValidPoints("deliveryLocations"), "Add the agreed delivery point");
                else if (incoterm is "DAP" or "DPU" or "DDP")
                    Required("destinationAddress", !string.IsNullOrWhiteSpace(Text("destinationAddress")));
                if (transport == "MARITIME") Deferred("portHandlingMode", HasValue("portHandlingMode"), "Origin can confirm port handling");
                else if (HasValue("portHandlingMode")) Required("portHandlingMode", false, "Port handling is not applicable to this transport");
                else NotApplicable("portHandlingMode", "Not a maritime shipment");
                if (manualOpening && (HasValue("weightValue") || HasValue("weightKg")))
                    Required("actualWeightInput", false, "Actual weight is confirmed by origin coordination");
            }

            if (basicManual) Deferred("cargoDescription", HasValue("cargoDescription"), "Origin confirms cargo description");
            else Required("cargoDescription", !string.IsNullOrWhiteSpace(Text("cargoDescription")));
            Required("pol", !string.IsNullOrWhiteSpace(Text("pol")));
            if (!strictManual && Text("source") is "PRICING" or "PRICING_HISTORICAL")
            {
                Required("poe", !string.IsNullOrWhiteSpace(Text("poe")));
                if (nonMaritime || !HasValue("pod")) NotApplicable("pod", "No separate final destination selected");
                else Required("pod", !string.IsNullOrWhiteSpace(Text("pod")));
            }
            else if (!pricingParity) Required("pod", !string.IsNullOrWhiteSpace(Text("pod")));
            else if (transport == "MARITIME")
            {
                if (HasValue("pod")) Required("pod", !string.IsNullOrWhiteSpace(Text("pod")));
                else NotApplicable("pod", "Optional maritime final destination");
            }
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

            // V6 manual openings require WHS for consolidated cargo and full equipment under FCA.
            // Existing V5 records retain their original contract.
            var requiresLinkedWarehouse = basicManual && revision >= 7 && RequiresManualWarehouse(shipment, incoterm, revision);
            if (requiresLinkedWarehouse)
            {
                Required("whsName", Guid.TryParse(Text("warehouseId"), out var warehouseId) && warehouseId != Guid.Empty && HasValue("whsName"));
                Required("agentReference", Guid.TryParse(Text("agentId"), out var agentId) && agentId != Guid.Empty && HasValue("agentReference"));
            }
            else if (basicManual && RequiresManualWarehouse(shipment, incoterm, revision))
                Required("whsName", (HasValue("warehouseId") || HasValue("warehouseAddress")) && HasValue("whsName"));
            else if (basicManual) NotApplicable("whsName", "Warehouse applies to consolidated shipments or FCA full equipment");
            else if (incoterm == "FCA" && (pricingParity || !strictManual || !Flag("requiresPickup")))
                Required("whsName", pricingParity ? (HasValue("warehouseId") || HasValue("warehouseAddress")) && HasValue("whsName") : HasValue("whsName") || (!strictManual && HasValue("warehouseId")));
            else NotApplicable("whsName", "Incoterm does not require FCA warehouse");

            var pickup = incoterm == "EXW" || Flag("requiresPickup") ||
                (!HasValue("requiresPickup") && !string.IsNullOrWhiteSpace(Text("pickupAddress")));
            if (basicManual && (incoterm == "EXW" || pickup))
                Deferred("pickupLocations", ValidPoints("pickupLocations"), "Origin confirms collection");
            else if (pricingParity && (incoterm == "EXW" || pickup))
                Required("pickupLocations", ValidPoints("pickupLocations"), "Add a collection point as in Pricing");
            else if (pickup) Deferred("pickupLocations", ValidPoints("pickupLocations"), "Collection details may be confirmed");
            // Pickup contact and time window are not opening requirements. Preserve any
            // historical JSON values without evaluating them as completeness fields.
            if (pickup)
            {
                if (basicManual) Deferred("pickupAddress", HasValue("pickupAddress"), "Origin confirms collection");
                else Required("pickupAddress", !string.IsNullOrWhiteSpace(Text("pickupAddress")));
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

            Deferred("stackable", data.TryGetProperty("stackable", out var stacking) &&
                (stacking.ValueKind is JsonValueKind.True or JsonValueKind.False), "Stacking may need confirmation");

            // Optional opening fields retain their own state without adding mandatory business rules.
            bool NonEmptyArray(string key) => data.TryGetProperty(key, out var array) &&
                array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0;
            foreach (var key in new[] { "provenance", "clientPoReference", "crmDivisionId", "cargoNature",
                "cabysCode", "packageType", "agentReference", "etm", "eta", "observations" })
                if (key != "agentReference" || !requiresLinkedWarehouse)
                    Deferred(key, !string.IsNullOrWhiteSpace(Text(key)), "Optional opening information");
            if (basicManual) Required("packageCount", Positive("packageCount") && decimal.TryParse(data.GetProperty("packageCount").ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var packages) && packages == decimal.Truncate(packages));
            else Deferred("packageCount", Positive("packageCount"), "Origin can confirm package count");
            Deferred("contactIds", NonEmptyArray("contactIds"), "Operational contacts may be registered later");
            if (manualOpening && revision >= 4)
                Required("services", NonEmptyArray("services") && NonEmptyArray("serviceSelections"), "Select the agreed services before sending to Origin");
            else Deferred("services", NonEmptyArray("services"), "Optional service information");
            if (shipment is "FTL" or "LTL") Deferred("roadCarrierReference", HasValue("roadCarrierReference"), "Origin confirms road carrier");
            else NotApplicable("roadCarrierReference", "Not a road shipment");
            Deferred("isImo", data.TryGetProperty("isImo", out var imoFlag) && imoFlag.ValueKind is JsonValueKind.True or JsonValueKind.False, "Cargo classification may need confirmation");
            Deferred("overweight", data.TryGetProperty("overweight", out var overweightFlag) && overweightFlag.ValueKind is JsonValueKind.True or JsonValueKind.False, "Origin confirms overweight");
            Deferred("supportDocuments", NonEmptyArray("supportDocuments"), "Documents can be supplied later");
            if (!strictManual)
            {
                Required("shipmentType", !string.IsNullOrWhiteSpace(shipment));
                Deferred("operationDirection", HasValue("operationDirection") || HasValue("provenance"), "Operation direction can be reviewed");
            }

            // Booking and transport references can be confirmed after opening.
            var air = shipment is "AIR" or "AIRCONSOL" || transport == "AIR";
            var maritime = !nonMaritime && (shipment is "FCL" or "LCL" || transport == "MARITIME");
            if (maritime) Deferred("haulageMode", Text("haulageMode") is "Merchant" or "Carrier" || HasValue("merchantReference"), "Origin confirms destination haulage");
            else NotApplicable("haulageMode", "No maritime destination haulage");
            if (maritime || air) Deferred("bookingNumber", !string.IsNullOrWhiteSpace(Text("bookingNumber")), "Origin confirms booking");
            else NotApplicable("bookingNumber", "No maritime or air booking");
            if (maritime) Deferred("blNumber", !string.IsNullOrWhiteSpace(Text("blNumber")), "Documentation confirms BL");
            else NotApplicable("blNumber", "Not a maritime shipment");
            if (maritime && shipment == "FCL") Deferred("containerNumber", !string.IsNullOrWhiteSpace(Text("containerNumber")), "Origin confirms container");
            else NotApplicable("containerNumber", "No dedicated maritime container");
            if (air) Deferred("airline", !string.IsNullOrWhiteSpace(Text("airline")) || !string.IsNullOrWhiteSpace(Text("carrierReference")), "Origin confirms airline");
            else NotApplicable("airline", "Not an air shipment");

            // Real weight belongs exclusively to Coordinación Origen on manual openings.
            Deferred("actualWeight", Positive("actualWeightValue") && Text("actualWeightUnit") is "KG" or "LB", "Coordinación Origen confirms actual weight");
            if (basicManual || (strictManual && (shipment is "LCL" or "LTL" or "AIR")))
                Required("volume", Positive("volumeValue") && Text("volumeUnit") is "CBM" or "CFT");
            else
                Deferred("volume", Positive("volume") || Positive("volumeCbm") || Positive("volumeValue"),
                    "Volume may be confirmed during origin coordination");
            if (shipment is "LCL" or "LTL")
                Deferred("consolidation", HasValue("consolidationReference") || HasValue("consolidation"),
                    "Consolidation can be confirmed by origin");
            else NotApplicable("consolidation", "Consolidation not required for this shipment");
            if (basicManual && shipment == "FCL") Required("carrierReference", HasValue("carrierReference"));
            else if (basicManual) NotApplicable("carrierReference", "Carrier is captured at opening only for FCL");
            else if (nonMaritime)
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
