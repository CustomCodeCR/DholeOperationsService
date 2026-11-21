using Dhole.Operations.Application.Cases;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Cases;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.IntegrationTests;

/// <summary>Real PostgreSQL regression: migration, IDTRA concurrency and weighted pool.</summary>
[TestClass]
public sealed class OperationsPostgresTests
{
    private static string? Connection => Environment.GetEnvironmentVariable("DHOLE_TEST_POSTGRES");

    [TestMethod]
    public async Task IdtraSequenceIsSafeUnderConcurrentManualOpenings()
    {
        if (string.IsNullOrWhiteSpace(Connection)) Assert.Inconclusive("DHOLE_TEST_POSTGRES is required for PostgreSQL integration tests.");
        await using (var migrateDb = NewDb()) await migrateDb.Database.MigrateAsync();

        var companyId = Guid.NewGuid();
        var openings = Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var db = NewDb();
            var command = new CreateManualCaseCommand(companyId,
                Guid.NewGuid(), Guid.NewGuid(), "FCL", 1, 1m, Guid.NewGuid());
            return await new CreateManualCaseHandler(new OperationsCaseStore(db))
                .HandleAsync(command, CancellationToken.None);
        });
        var results = await Task.WhenAll(openings);

        Assert.AreEqual(12, results.Select(x => x.Idtra).Distinct().Count());
        // Series are global across companies; tests must not assume the first issued value is 1.
        var assignedNumbers = results.Select(x => {
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(x.Idtra, @"^IDTRA\d{5,}$"));
            return long.Parse(x.Idtra["IDTRA".Length..]);
        })
            .Order().ToArray();
        Assert.AreEqual(12, assignedNumbers.Distinct().Count());
        Assert.IsTrue(assignedNumbers[0] >= 1);
        for (var i = 1; i < assignedNumbers.Length; i++)
            Assert.AreEqual(assignedNumbers[i - 1] + 1, assignedNumbers[i]);

        await using var anotherCompanyDb = NewDb();
        var otherCompany = await new CreateManualCaseHandler(new OperationsCaseStore(anotherCompanyDb))
            .HandleAsync(new CreateManualCaseCommand(Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), "FCL", 1, 1m, Guid.NewGuid()), CancellationToken.None);
        Assert.IsFalse(results.Any(x => x.Idtra == otherCompany.Idtra),
            "Cases belonging to different companies must never share a visible IDTRA.");

        await using var verification = NewDb();
        Assert.AreEqual(12, await verification.Cases.CountAsync(x => x.CompanyId == companyId));
        var expectedIds = results.Select(x => x.Id).ToHashSet();
        var payloads = await verification.OutboxMessages.AsNoTracking()
            .Where(x => x.EventName == "operations.case.created.v1")
            .Select(x => x.PayloadJson).ToArrayAsync();
        var matchingEvents = payloads.Count(payload =>
        {
            using var json = System.Text.Json.JsonDocument.Parse(payload);
            return Guid.TryParse(json.RootElement.GetProperty("caseId").GetString(), out var id)
                && expectedIds.Contains(id);
        });
        Assert.AreEqual(12, matchingEvents);
    }

    [TestMethod]
    public async Task PoolAssignsLeastLoadedMemberAndDoesNotDoubleAssign()
    {
        if (string.IsNullOrWhiteSpace(Connection)) Assert.Inconclusive("DHOLE_TEST_POSTGRES is required for PostgreSQL integration tests.");
        await using var db = NewDb();
        await db.Database.MigrateAsync();
        var companyId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var team = new OperationsTeam
        {
            CompanyId = companyId, DepartmentCode = "APERTURAS", Name = "Pool-" + Guid.NewGuid().ToString("N"),
            Members = [
                new OperationsTeamMember { UserId = first, CapacityPoints = 2m },
                new OperationsTeamMember { UserId = second, CapacityPoints = 10m }
            ]
        };
        db.Teams.Add(team);
        db.AssignmentRules.Add(new OperationsAssignmentRule
        {
            TeamId = team.Id, DepartmentCode = "APERTURAS", ModalityCode = "FCL", IsActive = true
        });
        await db.SaveChangesAsync();

        var caseResult = await new CreateManualCaseHandler(new OperationsCaseStore(db))
            .HandleAsync(new CreateManualCaseCommand(companyId,
                Guid.NewGuid(), Guid.NewGuid(), "FCL", 1, 2m, Guid.NewGuid()), CancellationToken.None);
        var handler = new AssignStageHandler(new OperationsCaseStore(db));
        var assigned = await handler.HandleAsync(
            new AssignStageCommand(caseResult.Id, caseResult.InitialStageId, Guid.NewGuid()),
            CancellationToken.None);

        Assert.IsNotNull(assigned);
        Assert.AreEqual(second, assigned.UserId);
        Assert.AreEqual(team.Id, assigned.TeamId);

        var repeated = await handler.HandleAsync(
            new AssignStageCommand(caseResult.Id, caseResult.InitialStageId, Guid.NewGuid()),
            CancellationToken.None);
        Assert.IsNull(repeated);
        Assert.AreEqual(1, await db.StageAssignments.CountAsync(
            x => x.CaseStageId == caseResult.InitialStageId && x.ReleasedAtUtc == null));
    }

    private static OperationsDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<OperationsDbContext>().UseNpgsql(Connection!).Options;
        return new OperationsDbContext(opts);
    }
}
