using System.Globalization;

namespace Dhole.Operations.Domain.Services;

/// <summary>Resolve a country only from explicit persisted route country labels/codes, never the customer or a guessed city.</summary>
public static class OperationsOriginRouting
{
    private static readonly Dictionary<string, string> SpanishNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Estados Unidos"] = "US", ["Estados Unidos de América"] = "US", ["USA"] = "US",
        ["China"] = "CN", ["Costa Rica"] = "CR", ["Panamá"] = "PA", ["México"] = "MX",
        ["España"] = "ES", ["Alemania"] = "DE", ["Italia"] = "IT", ["Francia"] = "FR",
        ["Reino Unido"] = "GB", ["Japón"] = "JP", ["Corea del Sur"] = "KR",
        ["Taiwán"] = "TW", ["Vietnam"] = "VN", ["Brasil"] = "BR", ["Países Bajos"] = "NL",
        ["Bélgica"] = "BE", ["Canadá"] = "CA", ["Colombia"] = "CO", ["Perú"] = "PE",
        ["República Dominicana"] = "DO", ["India"] = "IN", ["Turquía"] = "TR",
        ["Emiratos Árabes Unidos"] = "AE", ["Hong Kong"] = "HK", ["Singapur"] = "SG"
    };
    private static readonly RegionInfo[] Regions = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(c => new RegionInfo(c.Name)).DistinctBy(r => r.TwoLetterISORegionName).ToArray();

    public static string? Country(string? explicitCode, string? routeLabel)
    {
        if (!string.IsNullOrWhiteSpace(explicitCode))
        {
            var code = explicitCode.Trim().ToUpperInvariant();
            if (Regions.Any(r => r.TwoLetterISORegionName == code)) return code;
        }
        var parts = (routeLabel ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var label = parts[^1];
        if (SpanishNames.TryGetValue(label, out var mapped)) return mapped;
        var matches = Regions.Where(r => string.Equals(r.EnglishName, label, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.NativeName, label, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.TwoLetterISORegionName, label, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.ThreeLetterISORegionName, label, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.TwoLetterISORegionName).Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static bool IsSpecialized(string modality) => modality is "FCL" or "LCL" or "AIR";

    public const string UnitedStatesTeam = "Origen · Estados Unidos";
    public const string FclTeam = "Origen · FCL internacional";
    public const string LclAirTeam = "Origen · LCL y Aéreo internacional";
    public const string CentralAmericaTeam = "Origen · Centroamérica";
    public const string TransitTeam = "Origen · Tránsito";
    public const string FtlTeam = "Origen · FTL";
    public const string LtlTeam = "Origen · LTL";

    private static readonly HashSet<string> CentralAmerica = new(StringComparer.Ordinal)
        { "BZ", "GT", "SV", "HN", "NI", "CR", "PA" };

    /// <summary>Manual intake routing precedes operator load: transit, US, intraregional, then shipment.</summary>
    public static string? ManualTeamName(string modality, string? origin, string? destination, string? direction)
    {
        var mode = modality.Trim().ToUpperInvariant();
        origin = Country(origin, null);
        destination = Country(destination, null);
        direction = direction?.Trim().ToUpperInvariant();
        if (origin is null || destination is null || direction is not ("IMPORT" or "EXPORT" or "OTHER") ||
            mode is not ("FCL" or "LCL" or "AIR" or "FTL" or "LTL")) return null;
        if (direction == "OTHER" && origin != destination) return TransitTeam;
        if (origin == "US" || destination == "US") return UnitedStatesTeam;
        if (CentralAmerica.Contains(origin) && CentralAmerica.Contains(destination)) return CentralAmericaTeam;
        return mode switch
        {
            "FCL" => FclTeam,
            "LCL" or "AIR" => LclAirTeam,
            "FTL" => FtlTeam,
            "LTL" => LtlTeam,
            _ => null
        };
    }
}
