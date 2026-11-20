using System.Text.Json;

namespace Dhole.Operations.Application.Cases;

/// <summary>
/// Typed manual/accepted-QUO aperture capture. Missing information is valid in
/// DRAFT; the same rules evaluate completeness before validation. External
/// catalog references are snapshots, not a second CRM/Pricing master.
/// </summary>
public sealed class ApertureDraft
{
    public string? ClientPoReference { get; set; }
    public Guid? CrmDivisionId { get; set; }
    public string? ShipmentTypeCode { get; set; }
    public string? TransportTypeCode { get; set; }
    public string? IncotermCode { get; set; }
    public string? PolCode { get; set; }
    public string? PoeCode { get; set; }
    public string? PodCode { get; set; }
    public string? CargoDescription { get; set; }
    public decimal? WeightValue { get; set; }
    public string? WeightUnit { get; set; }
    public decimal? WeightKg { get; set; }
    public decimal? VolumeValue { get; set; }
    public string? VolumeUnit { get; set; }
    public decimal? VolumeCbm { get; set; }
    public decimal? PackageCount { get; set; }
    public string? PackageTypeCode { get; set; }
    public bool RequiresPickup { get; set; }
    public string? PickupAddress { get; set; }
    public string? PickupContact { get; set; }
    public string? PickupWindow { get; set; }
    public string? WarehouseName { get; set; }
    public string? ConsolidationReference { get; set; }
    public bool IsImo { get; set; }
    public string? ImoClass { get; set; }
    public string? UnNumber { get; set; }
    public bool? Stackable { get; set; }
    public bool IsOverweight { get; set; }
    public string? OverweightNotes { get; set; }
    public string? CarrierCode { get; set; }
    public string? AgentCode { get; set; }
    public string? MerchantCode { get; set; }
    public string? BookingReference { get; set; }
    public string? CollectionReference { get; set; }
    public DateTimeOffset? EstimatedDepartureAtUtc { get; set; }
    public DateTimeOffset? EstimatedArrivalAtUtc { get; set; }
    public string? Observations { get; set; }
    public List<ApertureEquipmentLine> Equipment { get; set; } = [];
    public List<string> ServiceCodes { get; set; } = [];
    public List<Guid> OperationalContactIds { get; set; } = [];
}
public sealed class ApertureEquipmentLine
{
    public string EquipmentCode { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public static class ApertureDraftRules
{
    // Exact factors mandated by the Aperturas v1.2 specification.
    private const decimal PoundsToKilograms = 0.45359237m;
    private const decimal CubicFeetToCubicMetres = 0.028316846592m;
    public static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<string> ValidateDraft(ApertureDraft? details)
    {
        if (details is null) return [];
        var errors = new List<string>();
        static bool Above(string? text, int limit) => text is { Length: > 0 } && text.Length > limit;
        foreach (var pair in new[] {
            (details.ClientPoReference, 120, "clientPoReference"),
            (details.ShipmentTypeCode, 40, "shipmentTypeCode"),
            (details.TransportTypeCode, 30, "transportTypeCode"),
            (details.IncotermCode, 10, "incotermCode"),
            (details.PolCode, 80, "polCode"),
            (details.PoeCode, 80, "poeCode"),
            (details.PodCode, 80, "podCode"),
            (details.CargoDescription, 4000, "cargoDescription"),
            (details.PickupAddress, 1000, "pickupAddress"),
            (details.PickupContact, 250, "pickupContact"),
            (details.PickupWindow, 150, "pickupWindow"),
            (details.WarehouseName, 200, "warehouseName"),
            (details.ConsolidationReference, 120, "consolidationReference"),
            (details.ImoClass, 20, "imoClass"),
            (details.UnNumber, 20, "unNumber"),
            (details.OverweightNotes, 2000, "overweightNotes"),
            (details.CarrierCode, 100, "carrierCode"),
            (details.AgentCode, 100, "agentCode"),
            (details.MerchantCode, 100, "merchantCode"),
            (details.BookingReference, 120, "bookingReference"),
            (details.CollectionReference, 120, "collectionReference"),
            (details.Observations, 10000, "observations")
        })
            if (Above(pair.Item1, pair.Item2)) errors.Add(pair.Item3);
        if (details.WeightValue is < 0 || details.VolumeValue is < 0 ||
            details.PackageCount is < 0) errors.Add("cargoQuantities");
        if (details.WeightValue.HasValue && details.WeightUnit is not ("KG" or "LB"))
            errors.Add("weightUnit");
        if (details.VolumeValue.HasValue && details.VolumeUnit is not ("CBM" or "CFT"))
            errors.Add("volumeUnit");
        if (details.Equipment is null || details.Equipment.Count > 100 ||
            details.Equipment.Any(x => x.EquipmentCode.Length > 30 ||
                (x.Quantity < 0)))
            errors.Add("equipment");
        if (details.ServiceCodes is null || details.ServiceCodes.Count > 120 ||
            details.ServiceCodes.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 60))
            errors.Add("serviceCodes");
        if (details.OperationalContactIds is null || details.OperationalContactIds.Count > 50 ||
            details.OperationalContactIds.Any(x => x == Guid.Empty))
            errors.Add("operationalContactIds");
        if (details.EstimatedDepartureAtUtc.HasValue &&
            details.EstimatedArrivalAtUtc.HasValue &&
            details.EstimatedDepartureAtUtc > details.EstimatedArrivalAtUtc)
            errors.Add("estimatedDates");
        return errors;
    }

    public static ApertureDraft Normalize(ApertureDraft details)
    {
        var errors = ValidateDraft(details);
        if (errors.Count != 0)
            throw new ArgumentException("INVALID_APERTURE_FIELDS: " + string.Join(", ", errors));
        details.WeightKg = details.WeightValue.HasValue
            ? decimal.Round(details.WeightValue.Value *
                (details.WeightUnit == "LB" ? PoundsToKilograms : 1m), 3,
                MidpointRounding.AwayFromZero) : null;
        details.VolumeCbm = details.VolumeValue.HasValue
            ? decimal.Round(details.VolumeValue.Value *
                (details.VolumeUnit == "CFT" ? CubicFeetToCubicMetres : 1m), 6,
                MidpointRounding.AwayFromZero) : null;
        details.Equipment = details.Equipment
            .Where(x => !string.IsNullOrWhiteSpace(x.EquipmentCode) && x.Quantity > 0)
            .GroupBy(x => x.EquipmentCode.Trim().ToUpperInvariant())
            .Select(g => new ApertureEquipmentLine {
                EquipmentCode = g.Key, Quantity = g.Sum(x => x.Quantity)
            }).ToList();
        details.ServiceCodes = details.ServiceCodes
            .Select(x => x.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal).ToList();
        details.OperationalContactIds = details.OperationalContactIds.Distinct().ToList();
        return details;
    }

    public static IReadOnlyList<string> BlockingFields(string modality, ApertureDraft? details)
    {
        details ??= new ApertureDraft();
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(details.CargoDescription)) missing.Add("cargoDescription");
        if (string.IsNullOrWhiteSpace(details.TransportTypeCode)) missing.Add("transportTypeCode");
        if (string.IsNullOrWhiteSpace(details.ShipmentTypeCode)) missing.Add("shipmentTypeCode");
        if (details.WeightKg is null or <= 0) missing.Add("weight");
        if (details.PackageCount is null or <= 0) missing.Add("packageCount");
        if (details.ServiceCodes.Count == 0) missing.Add("serviceCodes");
        if (modality == "FCL" && details.Equipment.Sum(x => x.Quantity) == 0)
            missing.Add("equipment");
        if (details.IsImo && (string.IsNullOrWhiteSpace(details.ImoClass) ||
            string.IsNullOrWhiteSpace(details.UnNumber))) missing.Add("imoClassAndUnNumber");
        if (details.IsOverweight && string.IsNullOrWhiteSpace(details.OverweightNotes))
            missing.Add("overweightNotes");
        if (details.RequiresPickup &&
            (string.IsNullOrWhiteSpace(details.PickupAddress) ||
             string.IsNullOrWhiteSpace(details.PickupContact)))
            missing.Add("pickup");
        if (details.IncotermCode == "FCA" && modality is ("LCL" or "LTL") &&
            string.IsNullOrWhiteSpace(details.WarehouseName)) missing.Add("warehouseName");
        // Air and ground modalities do not require an ocean carrier.
        if (details.TransportTypeCode == "MARITIME" &&
            string.IsNullOrWhiteSpace(details.CarrierCode)) missing.Add("carrierCode");
        return missing;
    }
}
