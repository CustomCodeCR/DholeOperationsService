using Dhole.Operations.Application.Cases;
using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsFoundationTests
{
    [TestMethod]
    public async Task ExplicitIdtraSeriesIsRequiredAndNeverGuessed()
    {
        var handler = new CreateManualCaseHandler(new NoopStore());
        var command = new CreateManualCaseCommand(
            Guid.NewGuid(), "XX000", Guid.NewGuid(), Guid.NewGuid(), "FCL", 1, 3m, Guid.NewGuid());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => handler.HandleAsync(command, CancellationToken.None));
    }

    [TestMethod]
    public void WorkloadIsWeightedByConfiguredCapacity()
    {
        Assert.AreEqual(0.75m, OperationsPoolScoring.ProjectedRelativeLoad(5m, 1m, 8m));
        Assert.AreEqual(0.30m, OperationsPoolScoring.ProjectedRelativeLoad(2m, 1m, 10m));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            OperationsPoolScoring.ProjectedRelativeLoad(1m, 1m, 0m));
    }

    [TestMethod]
    public async Task DocumentReferenceRequiresActualStorageId()
    {
        var handler = new RegisterDocumentHandler(new NoopStore());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => handler.HandleAsync(
            new RegisterDocumentCommand(Guid.NewGuid(), "BL", Guid.Empty,
                "hbl.pdf", "application/pdf", null, Guid.NewGuid()), CancellationToken.None));
    }

    private sealed class NoopStore : IOperationsCaseStore
    {
        public Task<CaseCreatedResult> CreateManualAsync(CreateManualCaseCommand c, CancellationToken ct) =>
            throw new InvalidOperationException("Store should not be reached on invalid command.");
        public Task<StageAssignedResult?> AssignStageAsync(AssignStageCommand c, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<DocumentRegisteredResult> RegisterDocumentAsync(RegisterDocumentCommand c, CancellationToken ct) =>
            throw new InvalidOperationException("Store should not be reached on invalid command.");
        public Task<DocumentExtractionQueuedResult> QueueDocumentExtractionAsync(QueueDocumentExtractionCommand c, CancellationToken ct) =>
            throw new NotImplementedException();
    }
}
