using Dhole.Operations.Domain.Models;

namespace Dhole.Operations.Persistence;

public static class OperationsCaseQueryOrdering
{
    /// <summary>Numeric sequence across current and historical IDTRA formats, with stable pagination.</summary>
    public static IOrderedQueryable<OperationsCase> OrderByIdtraDescending(this IQueryable<OperationsCase> query) =>
        query.OrderByDescending(c => c.SequenceNumber).ThenByDescending(c => c.CreatedAtUtc).ThenBy(c => c.Id);
}
