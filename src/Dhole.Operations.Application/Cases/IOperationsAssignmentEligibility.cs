namespace Dhole.Operations.Application.Cases;

public interface IOperationsAssignmentEligibility
{
    Task<bool> IsEligibleAsync(Guid userId, CancellationToken ct);
}
