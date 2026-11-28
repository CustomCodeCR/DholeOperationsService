using System.Globalization;
using System.Text.RegularExpressions;

namespace Dhole.Operations.Domain.Services;

/// <summary>Compute estimated duration without treating a local calendar date
/// as a UTC instant. No rounding or port timezone is assumed.</summary>
public static class OperationsOriginTransitDuration
{
    public sealed record Result(string State, decimal? Days, string? Precision);
    private static bool Date(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool Instant(string value, out DateTimeOffset instant)
    {
        instant = default;
        // A timestamp without an explicit offset is ambiguous across ports.
        return Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|[+-]\d{2}:\d{2})$",
            RegexOptions.CultureInvariant) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out instant);
    }

    public static Result Calculate(string? etd, string? eta)
    {
        if (string.IsNullOrWhiteSpace(etd) || string.IsNullOrWhiteSpace(eta))
            return new("PENDING", null, null);
        if (Date(etd, out var departureDate) && Date(eta, out var arrivalDate))
        {
            var days = arrivalDate.DayNumber - departureDate.DayNumber;
            return days < 0 ? new("INVALID", null, "DATE") : new("CALCULATED", days, "DATE");
        }
        if (Instant(etd, out var departure) && Instant(eta, out var arrival))
        {
            var ticks = (arrival - departure).Ticks;
            return ticks < 0 ? new("INVALID", null, "INSTANT") :
                new("CALCULATED", (decimal)ticks / TimeSpan.TicksPerDay, "INSTANT");
        }
        return new("INVALID", null, null);
    }
}
