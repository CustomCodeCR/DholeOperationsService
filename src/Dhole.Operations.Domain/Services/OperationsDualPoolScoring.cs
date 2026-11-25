namespace Dhole.Operations.Domain.Services;

/// <summary>Weighted assignment picks TWO DISTINCT active users in ONE team.</summary>
public sealed record AperturePoolMember(Guid TeamId, Guid UserId,
    decimal CapacityPoints, decimal ActivePoints, int RulePriority);
public sealed record AperturePoolPair(Guid TeamId, Guid FirstUserId, Guid SecondUserId,
    decimal FirstRelativeLoad, decimal SecondRelativeLoad);

public static class OperationsDualPoolScoring
{
    // Single-review policy; retain the legacy pair helper for historical callers/tests.
    public static AperturePoolMember? SelectSingle(IReadOnlyCollection<AperturePoolMember> members, decimal points)
    {
        if (points <= 0) throw new ArgumentOutOfRangeException(nameof(points));
        return members.Where(x => x.TeamId != Guid.Empty && x.UserId != Guid.Empty && x.CapacityPoints > 0)
            .OrderBy(x => (x.ActivePoints + points) / x.CapacityPoints)
            .ThenByDescending(x => x.RulePriority).ThenBy(x => x.TeamId).ThenBy(x => x.UserId)
            .FirstOrDefault();
    }

    public static AperturePoolPair? Select(IReadOnlyCollection<AperturePoolMember> members, decimal points)
    {
        if (points <= 0) throw new ArgumentOutOfRangeException(nameof(points));
        return members.Where(x => x.TeamId != Guid.Empty && x.UserId != Guid.Empty &&
                                  x.CapacityPoints > 0)
            .GroupBy(x => x.TeamId)
            .Select(team =>
            {
                var distinct = team.GroupBy(x => x.UserId)
                    .Select(g => g.OrderByDescending(x => x.RulePriority).First())
                    .Select(x => new {
                        x.UserId, x.RulePriority,
                        Load = (x.ActivePoints + points / 2m) / x.CapacityPoints
                    })
                    .OrderBy(x => x.Load).ThenBy(x => x.UserId).Take(2).ToArray();
                return distinct.Length == 2
                    ? new {
                        Pair = new AperturePoolPair(team.Key, distinct[0].UserId,
                            distinct[1].UserId, distinct[0].Load, distinct[1].Load),
                        Score = distinct[0].Load + distinct[1].Load,
                        Priority = Math.Max(distinct[0].RulePriority, distinct[1].RulePriority)
                    }
                    : null;
            })
            .Where(x => x is not null)
            .OrderBy(x => x!.Score).ThenByDescending(x => x!.Priority)
            .ThenBy(x => x!.Pair.TeamId)
            .Select(x => x!.Pair)
            .FirstOrDefault();
    }
}
