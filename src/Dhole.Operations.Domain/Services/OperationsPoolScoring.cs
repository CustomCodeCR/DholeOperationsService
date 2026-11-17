namespace Dhole.Operations.Domain.Services;

/// <summary>Configurable-point workload distribution. Never compare only raw case counts.</summary>
public static class OperationsPoolScoring
{
    public static decimal ProjectedRelativeLoad(decimal activePoints, decimal incomingPoints, decimal capacityPoints)
    {
        if (activePoints < 0 || incomingPoints <= 0 || capacityPoints <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityPoints), "Workload and capacity must be valid positive values.");
        return (activePoints + incomingPoints) / capacityPoints;
    }
}
