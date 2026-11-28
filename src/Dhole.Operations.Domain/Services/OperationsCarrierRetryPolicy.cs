namespace Dhole.Operations.Domain.Services;

/// <summary>One bounded retry decision per failed read. The caller must persist
/// attempts and next-at timestamps and enforce a provider/account quota.
/// No failure result instructs the caller to erase existing observations.</summary>
public static class OperationsCarrierRetryPolicy
{
    public sealed record Decision(string State, DateTimeOffset? NextAttemptAt, bool RequiresReview);
    public const int MaxAttempts = 5;
    public static Decision AfterReadFailure(int? statusCode, bool timedOut, int attempts,
        DateTimeOffset now, DateTimeOffset? retryAfter, double jitter)
    {
        if (attempts < 1 || !double.IsFinite(jitter) || jitter < 0 || jitter > 1)
            throw new ArgumentOutOfRangeException(nameof(attempts));
        if (statusCode is 401 or 403)
            return new("AUTHORIZATION_REQUIRED", null, true);
        if (statusCode is 404 or 422)
            return new("REVIEW_REQUIRED", null, true);
        if (!(timedOut || statusCode is null or 408 or 429 or >= 500 and <= 599))
            return new("ERROR", null, true);
        if (attempts >= MaxAttempts)
            return new("RETRY_EXHAUSTED", null, true);
        var delay = TimeSpan.FromSeconds(Math.Min(900, 30 * Math.Pow(2, attempts - 1) * (1 + jitter)));
        var next = now.Add(delay);
        if (retryAfter.HasValue && retryAfter.Value > next) next = retryAfter.Value;
        return new(statusCode == 429 ? "RATE_LIMITED" : "RETRY_PENDING", next, false);
    }
}
