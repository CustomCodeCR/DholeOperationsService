using System.Security.Claims;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.IntegrationTests;

[TestClass]
public sealed class OperationsStageAccessPostgresTests
{
    private static ClaimsPrincipal User(Guid userId, Guid companyId, string scopes = "operations.case.read operations.origin.update") =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("company_id", companyId.ToString()),
            new Claim("scope", scopes)
        }, "integration-test"));

    [TestMethod]
    public async Task StageEditRequiresCurrentTeamMembershipEvenWhenAnotherStageKeepsCaseVisible()
    {
        var connection = Environment.GetEnvironmentVariable("DHOLE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Inconclusive("DHOLE_TEST_POSTGRES is required.");
        await using var db = new OperationsDbContext(new DbContextOptionsBuilder<OperationsDbContext>()
            .UseNpgsql(connection!).Options);
        await db.Database.MigrateAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var actor = Guid.NewGuid();
        var company = Guid.NewGuid();
        var originTeam = new OperationsTeam { CompanyId = company, DepartmentCode = "COORDINACION_ORIGEN", Name = "Origin-" + Guid.NewGuid() };
        var transitTeam = new OperationsTeam { CompanyId = company, DepartmentCode = "TRANSITO", Name = "Transit-" + Guid.NewGuid() };
        var originMember = new OperationsTeamMember { UserId = actor, Team = originTeam, TeamId = originTeam.Id };
        var transitMember = new OperationsTeamMember { UserId = actor, Team = transitTeam, TeamId = transitTeam.Id };
        var operation = new OperationsCase
        {
            CompanyId = company, AccountId = Guid.NewGuid(), ExecutiveUserId = actor,
            Idtra = "IDTRA" + Guid.NewGuid().ToString("N")[..24], SeriesCode = "IDTRA",
            ModalityCode = "FCL", Status = "APERTURA_VALIDADA", CreatedBy = actor
        };
        var origin = new OperationsCaseStage { Case = operation, CaseId = operation.Id,
            TeamId = originTeam.Id, DepartmentCode = "COORDINACION_ORIGEN", Status = "READY" };
        var transit = new OperationsCaseStage { Case = operation, CaseId = operation.Id,
            TeamId = transitTeam.Id, DepartmentCode = "TRANSITO", Status = "READY" };
        var originAssignment = new OperationsStageAssignment { CaseStage = origin, CaseStageId = origin.Id,
            TeamId = originTeam.Id, UserId = actor, AssignedBy = actor, AssignedPoints = 1m };
        var transitAssignment = new OperationsStageAssignment { CaseStage = transit, CaseStageId = transit.Id,
            TeamId = transitTeam.Id, UserId = actor, AssignedBy = actor, AssignedPoints = 1m };
        db.AddRange(originTeam, transitTeam, originMember, transitMember, operation, origin, transit, originAssignment, transitAssignment);
        await db.SaveChangesAsync();

        var access = new OperationsCaseAccess(db);
        var user = User(actor, company);
        Assert.IsTrue(await access.CanEditAssignedStageAsync(origin.Id, user, CancellationToken.None));
        Assert.IsFalse(await access.CanEditAssignedStageAsync(origin.Id, User(actor, Guid.NewGuid()), CancellationToken.None));
        Assert.IsFalse(await access.CanEditAssignedStageAsync(origin.Id, User(actor, company, "operations.origin.update"), CancellationToken.None));
        Assert.IsFalse(await access.CanEditAssignedStageAsync(Guid.NewGuid(), user, CancellationToken.None));

        originTeam.IsActive = false;
        await db.SaveChangesAsync();
        Assert.IsTrue(await access.CanReadAsync(operation.Id, user, CancellationToken.None));
        Assert.IsFalse(await access.CanEditAssignedStageAsync(origin.Id, user, CancellationToken.None));

        originTeam.IsActive = true;
        await db.SaveChangesAsync();
        Assert.IsTrue(await access.CanEditAssignedStageAsync(origin.Id, user, CancellationToken.None));

        // A completed stage cannot become editable solely because an old assignment survives.
        origin.Status = "COMPLETED";
        await db.SaveChangesAsync();
        Assert.IsFalse(await access.CanEditAssignedStageAsync(origin.Id, user, CancellationToken.None));
        origin.Status = "READY";
        await db.SaveChangesAsync();

        db.TeamMembers.Remove(originMember);
        await db.SaveChangesAsync();
        Assert.IsTrue(await access.CanReadAsync(operation.Id, user, CancellationToken.None),
            "The transit assignment still grants legitimate access to the IDTRA.");
        Assert.IsFalse(await access.CanEditAssignedStageAsync(origin.Id, user, CancellationToken.None),
            "Revoked membership must defeat the stale origin assignment.");

        await tx.RollbackAsync();
    }
}
