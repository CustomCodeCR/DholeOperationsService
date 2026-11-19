namespace Dhole.Operations.Domain.Services;

/// <summary>Input gate for a single all-or-nothing team membership batch.</summary>
public static class OperationsTeamMemberBatchPolicy
{
    public const int MaximumBatchSize = 100;

    public static string? Validate(IReadOnlyCollection<Guid>? userIds, decimal capacityPoints)
    {
        if (userIds is null || userIds.Count == 0 || userIds.Count > MaximumBatchSize)
            return "TEAM_BATCH_SIZE_INVALID";
        if (userIds.Any(id => id == Guid.Empty))
            return "TEAM_BATCH_USER_INVALID";
        if (userIds.Distinct().Count() != userIds.Count)
            return "TEAM_BATCH_DUPLICATE_USER";
        if (capacityPoints <= 0 || capacityPoints > 10000)
            return "TEAM_BATCH_CAPACITY_INVALID";
        return null;
    }
}
