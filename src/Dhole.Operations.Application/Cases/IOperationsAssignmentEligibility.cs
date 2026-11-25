namespace Dhole.Operations.Application.Cases;

public interface IOperationsAssignmentEligibility
{
    Task<bool> IsEligibleAsync(Guid userId, CancellationToken ct);

    async Task<OperationsAssignmentRecipient?> GetRecipientAsync(Guid userId, CancellationToken ct)
        => await IsEligibleAsync(userId, ct) ? new OperationsAssignmentRecipient(null) : null;
}

public sealed record OperationsAssignmentRecipient(string? Email);
