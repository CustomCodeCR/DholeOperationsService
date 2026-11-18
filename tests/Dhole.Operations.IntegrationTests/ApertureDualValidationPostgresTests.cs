using Dhole.Operations.Application.Cases;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Cases;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.IntegrationTests;

[TestClass]
public sealed class ApertureDualValidationPostgresTests
{
    private static OperationsDbContext Db()
    {
        var connection = Environment.GetEnvironmentVariable("DHOLE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Inconclusive("DHOLE_TEST_POSTGRES is required.");
        return new OperationsDbContext(new DbContextOptionsBuilder<OperationsDbContext>()
            .UseNpgsql(connection).Options);
    }

    [TestMethod]
    public async Task TwoDistinctReviewersAreEnforcedByPostgresAndAuditHistoryPersists()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        CollectionAssert.Contains((await db.Database.GetAppliedMigrationsAsync()).ToList(),
            "202611180100_ApertureDualValidation208");
        var company = Guid.NewGuid();
        var created = await new CreateManualCaseHandler(new OperationsCaseStore(db))
            .HandleAsync(new CreateManualCaseCommand(company, "00000",
                Guid.NewGuid(), Guid.NewGuid(), "FCL", 1, 1m, Guid.NewGuid()), CancellationToken.None);
        var team = new OperationsTeam { CompanyId = company, DepartmentCode = "APERTURAS",
            Name = "Validadores " + Guid.NewGuid().ToString("N") };
        db.Teams.Add(team);
        var reviewer = Guid.NewGuid();
        db.ApertureValidations.Add(new OperationsApertureValidation {
            CaseStageId = created.InitialStageId, TeamId = team.Id,
            UserId = reviewer, Slot = 1, AssignedBy = Guid.NewGuid()
        });
        db.ApertureValidations.Add(new OperationsApertureValidation {
            CaseStageId = created.InitialStageId, TeamId = team.Id,
            UserId = Guid.NewGuid(), Slot = 2, AssignedBy = Guid.NewGuid()
        });
        db.TeamAuditEntries.Add(new OperationsTeamAuditEntry {
            TeamId = team.Id, EventId = Guid.NewGuid(),
            ActorUserId = Guid.NewGuid(), ActionCode = "operations.team.created"
        });
        await db.SaveChangesAsync();
        Assert.IsTrue(await db.CaseAuditEntries.AnyAsync(a =>
            a.CaseId == created.Id && a.ActionCode == "operations.case.created"));
        // CustomCodeFramework maps PayloadJson as jsonb; avoid PostgreSQL LIKE on jsonb.
        var auditPayloads = await db.OutboxMessages
            .Where(o => o.EventName == "audit.event.registered")
            .Select(o => o.PayloadJson).ToListAsync();
        Assert.IsTrue(auditPayloads.Any(p => p.Contains("operations.case.created",
            StringComparison.Ordinal)));
        Assert.AreEqual(2, await db.ApertureValidations.CountAsync(v =>
            v.CaseStageId == created.InitialStageId));
        Assert.AreEqual(1, await db.TeamAuditEntries.CountAsync(a => a.TeamId == team.Id));
        db.ApertureValidations.Add(new OperationsApertureValidation {
            CaseStageId = created.InitialStageId, TeamId = team.Id,
            UserId = reviewer, Slot = 2, AssignedBy = Guid.NewGuid()
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
