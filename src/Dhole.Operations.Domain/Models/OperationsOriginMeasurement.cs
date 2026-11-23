namespace Dhole.Operations.Domain.Models;

/// <summary>Actual measured quantities from Origin. Quoted values remain in immutable Pricing snapshot.</summary>
public sealed class OperationsOriginMeasurement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public decimal? OriginalWeight { get; set; }
    public string? WeightUnit { get; set; }
    public decimal? WeightKg { get; set; }
    public decimal? OriginalVolume { get; set; }
    public string? VolumeUnit { get; set; }
    public decimal? VolumeCbm { get; set; }
    public Guid ConfirmedBy { get; set; }
    public DateTimeOffset ConfirmedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public long Version { get; set; } = 1;
}
