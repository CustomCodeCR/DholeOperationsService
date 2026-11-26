using Dhole.Operations.Application.Cases;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Cases;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.IntegrationTests;

[TestClass]
public sealed class OperationsManualDirectOriginTests
{
    [TestMethod]
    public async Task ManualOpeningPersistsPendingOriginWithoutReviewers()
    {
        var connection = Environment.GetEnvironmentVariable("DHOLE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) Assert.Inconclusive("DHOLE_TEST_POSTGRES required");
        await using var db = new OperationsDbContext(new DbContextOptionsBuilder<OperationsDbContext>()
            .UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();
        var result = await new CreateManualCaseHandler(new OperationsCaseStore(db)).HandleAsync(
            new CreateManualCaseCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "FCL", 1, 1m, Guid.NewGuid(), SendDirectlyToOrigin: true), CancellationToken.None);
        Assert.AreEqual("OPEN", result.Status);
        Assert.IsTrue(await db.CaseStages.AnyAsync(s => s.Id == result.InitialStageId &&
            s.DepartmentCode == "COORDINACION_ORIGEN" && s.Status == "PENDING"));
        Assert.IsTrue(await db.CaseStages.AnyAsync(s => s.CaseId == result.Id &&
            s.DepartmentCode == "APERTURAS" && s.Status == "SKIPPED"));
        Assert.IsFalse(await db.ApertureValidations.AnyAsync(v => v.CaseStage.CaseId == result.Id));
    }
    [TestMethod]
    public async Task ManualOpeningReservesQuoteLinkWithExistingIdtra()
    {
        var connection = Environment.GetEnvironmentVariable("DHOLE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) Assert.Inconclusive("DHOLE_TEST_POSTGRES required");
        await using var db = new OperationsDbContext(new DbContextOptionsBuilder<OperationsDbContext>()
            .UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();
        var option = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var result = await new CreateManualCaseHandler(new OperationsCaseStore(db)).HandleAsync(
            new CreateManualCaseCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "FCL", 1, 1m, actor, SendDirectlyToOrigin: true, QuoteRateId: option), CancellationToken.None);
        var link = await db.CaseQuoteLinks.SingleAsync(x => x.CaseId == result.Id);
        Assert.AreEqual("PENDING_LINK", link.Status);
        Assert.AreEqual(option, link.QuoteOptionId);
        Assert.AreEqual(actor, link.LinkedBy);
        Assert.IsFalse(link.IsPrimary);
        var operation = await db.Cases.SingleAsync(x => x.Id == result.Id);
        Assert.AreEqual(result.Idtra, operation.Idtra);
        Assert.IsNull(operation.SourceQuoteId);
    }
}
