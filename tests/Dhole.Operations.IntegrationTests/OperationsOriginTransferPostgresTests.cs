using Dhole.Operations.Application.Cases;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Cases;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.IntegrationTests;

[TestClass]
public sealed class OperationsOriginTransferPostgresTests
{
    private sealed class EligibleUsers : IOperationsAssignmentEligibility
    {
        public Task<bool> IsEligibleAsync(Guid userId, CancellationToken ct) => Task.FromResult(true);
    }

    private static OperationsDbContext Db()
    {
        var connection = Environment.GetEnvironmentVariable("DHOLE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Inconclusive("DHOLE_TEST_POSTGRES is required.");
        return new OperationsDbContext(new DbContextOptionsBuilder<OperationsDbContext>()
            .UseNpgsql(connection).Options);
    }

    [TestMethod]
    public async Task ValidatedOriginStageUsesExistingPoolWithoutDuplicateAssignmentsOrProgress()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        var company = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        var created = await new CreateManualCaseHandler(new OperationsCaseStore(db))
            .HandleAsync(new CreateManualCaseCommand(company, Guid.NewGuid(),
                Guid.NewGuid(), "FCL", 1, 2m, reviewer, "CN", "CR"),
                CancellationToken.None);
        var operation = await db.Cases.SingleAsync(x => x.Id == created.Id);
        operation.Status = "APERTURA_VALIDADA";
        var origin = new OperationsCaseStage {
            CaseId = created.Id, DepartmentCode = "COORDINACION_ORIGEN", Status = "PENDING"
        };
        db.CaseStages.Add(origin);
        var team = new OperationsTeam {
            CompanyId = company, DepartmentCode = "COORDINACION_ORIGEN",
            Name = "Origen-" + Guid.NewGuid().ToString("N"),
            Members = [new OperationsTeamMember { UserId = operatorId,
                IsAvailable = true, CapacityPoints = 10m }]
        };
        db.Teams.Add(team);
        db.AssignmentRules.Add(new OperationsAssignmentRule {
            TeamId = team.Id, DepartmentCode = "COORDINACION_ORIGEN",
            ModalityCode = "FCL", OriginCountryCode = "CN", IsActive = true
        });
        await db.SaveChangesAsync();

        var handler = new AssignStageHandler(new OperationsCaseStore(db, new EligibleUsers()));
        var first = await handler.HandleAsync(
            new AssignStageCommand(created.Id, origin.Id, reviewer), CancellationToken.None);
        Assert.IsNotNull(first);
        Assert.AreEqual(team.Id, first.TeamId);
        Assert.AreEqual(operatorId, first.UserId);
        var second = await handler.HandleAsync(
            new AssignStageCommand(created.Id, origin.Id, reviewer), CancellationToken.None);
        Assert.IsNull(second, "Retries must not create another stage assignment.");

        await using var verify = Db();
        Assert.AreEqual(1, await verify.StageAssignments.CountAsync(x =>
            x.CaseStageId == origin.Id && x.ReleasedAtUtc == null));
        var progress = await verify.OriginProgress.SingleAsync(x => x.CaseStageId == origin.Id);
        Assert.AreEqual("ORIGIN_COORDINATION", progress.StatusCode);
        Assert.AreEqual("READY", await verify.CaseStages.Where(x => x.Id == origin.Id)
            .Select(x => x.Status).SingleAsync());
        // payload_json is PostgreSQL jsonb. A string.Contains inside the
        // EF SQL predicate becomes LIKE(jsonb), which PostgreSQL rejects.
        var events = await verify.OutboxMessages
            .Where(x => x.EventName == "operations.origin.assigned.v1")
            .Select(x => x.PayloadJson).ToArrayAsync();
        Assert.AreEqual(1, events.Count(json =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("caseId").GetGuid() == created.Id;
        }));
    }

    [TestMethod]
    public async Task NoPoolLeavesValidatedOriginUnassignedForLaterReconciliation()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        var created = await new CreateManualCaseHandler(new OperationsCaseStore(db))
            .HandleAsync(new CreateManualCaseCommand(Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), "LTL", 1, 1m, Guid.NewGuid()),
                CancellationToken.None);
        var origin = new OperationsCaseStage {
            CaseId = created.Id, DepartmentCode = "COORDINACION_ORIGEN", Status = "PENDING"
        };
        db.CaseStages.Add(origin);
        await db.SaveChangesAsync();
        var result = await new AssignStageHandler(new OperationsCaseStore(db))
            .HandleAsync(new AssignStageCommand(created.Id, origin.Id, Guid.NewGuid()),
                CancellationToken.None);
        Assert.IsNull(result);
        Assert.AreEqual("PENDING", await db.CaseStages.AsNoTracking()
            .Where(x => x.Id == origin.Id).Select(x => x.Status).SingleAsync());
    }
}
