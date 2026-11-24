using System.Data;
using System.Text.Json;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Domain.Services;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Persistence.Cases;

/// <summary>Durable automatic Aperturas pool; same company lock and pair scoring as the explicit assignment endpoint.</summary>
public static class OperationsAutomaticAperturePool
{
    public static async Task<bool> TryAssignAsync(OperationsDbContext db, Guid caseId,
        Func<Guid, CancellationToken, Task<bool>> eligible, CancellationToken ct)
    {
        var company = await db.Cases.AsNoTracking().Where(c => c.Id == caseId)
            .Select(c => (Guid?)c.CompanyId).SingleOrDefaultAsync(ct);
        if (!company.HasValue) return false;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var key = "apertures-pool:" + company.Value.ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));", ct);
        // Re-read after taking the lock, never assign to a case already validated or edited into a draft.
        var operation = await db.Cases.SingleOrDefaultAsync(c => c.Id == caseId && c.CompanyId == company.Value, ct);
        if (operation is null || operation.Status != "PENDING_VALIDATION" || operation.CreatedBy == Guid.Empty)
            return false;
        if (operation.ApertureType == "MANUAL" && OperationsOpeningDraftPolicy.MissingFields(
                operation.ModalityCode, operation.OpeningDataJson).Length > 0) return false;
        var stage = await db.CaseStages.SingleOrDefaultAsync(x => x.CaseId == caseId && x.DepartmentCode == "APERTURAS", ct);
        if (stage is null || stage.Status is not ("PENDING" or "PENDING_VALIDATION") ||
            await db.ApertureValidations.AnyAsync(x => x.CaseStageId == stage.Id, ct)) return false;
        var rules = await db.AssignmentRules.AsNoTracking().Where(r => r.IsActive &&
            r.Team.IsActive && r.Team.CompanyId == operation.CompanyId && r.DepartmentCode == "APERTURAS" &&
            (r.ModalityCode == null || r.ModalityCode == operation.ModalityCode) &&
            (r.OriginCountryCode == null || r.OriginCountryCode == operation.OriginCountryCode) &&
            (r.DestinationCountryCode == null || r.DestinationCountryCode == operation.DestinationCountryCode))
            .Select(r => new { r.TeamId, r.Priority }).ToArrayAsync(ct);
        var teamIds = rules.Select(r => r.TeamId).Distinct().ToArray();
        var teamsQuery = db.Teams.AsNoTracking().Include(t => t.Members).Where(t =>
            t.IsActive && t.CompanyId == operation.CompanyId && t.DepartmentCode == "APERTURAS");
        if (stage.TeamId.HasValue) teamsQuery = teamsQuery.Where(t => t.Id == stage.TeamId.Value);
        else if (teamIds.Length > 0) teamsQuery = teamsQuery.Where(t => teamIds.Contains(t.Id));
        // A deliberately inactive or nonmatching configured rule must not silently become a wildcard.
        else if (await db.AssignmentRules.AnyAsync(r => r.Team.CompanyId == operation.CompanyId &&
            r.DepartmentCode == "APERTURAS", ct)) return false;
        var teams = await teamsQuery.ToArrayAsync(ct);
        var workloads = await db.ApertureValidations.AsNoTracking()
            .Where(v => v.Status == "PENDING" && v.CaseStage.Case.CompanyId == operation.CompanyId &&
                v.CaseStage.Status == "PENDING_VALIDATION" && v.CaseStage.Case.Status == "PENDING_VALIDATION")
            .GroupBy(v => v.UserId).Select(gr => new { UserId = gr.Key,
                Points = gr.Sum(v => v.CaseStage.Case.ComplexityPoints / 2m) })
            .ToDictionaryAsync(x => x.UserId, x => x.Points, ct);
        var stageWorkloads = await db.StageAssignments.AsNoTracking()
            .Where(v => v.ReleasedAtUtc == null && v.CaseStage.Case.CompanyId == operation.CompanyId &&
                v.CaseStage.Status != "COMPLETED" && v.CaseStage.Status != "SKIPPED")
            .GroupBy(v => v.UserId).Select(gr => new { UserId = gr.Key, Points = gr.Sum(v => v.AssignedPoints) })
            .ToDictionaryAsync(x => x.UserId, x => x.Points, ct);
        var candidates = teams.SelectMany(team => team.Members
            .Where(member => member.IsAvailable && member.CapacityPoints > 0)
            .Select(member => new AperturePoolMember(team.Id, member.UserId, member.CapacityPoints,
                workloads.GetValueOrDefault(member.UserId) + stageWorkloads.GetValueOrDefault(member.UserId),
                rules.Where(r => r.TeamId == team.Id).Select(r => r.Priority).DefaultIfEmpty(0).Max()))).ToList();
        AperturePoolPair? pair;
        while ((pair = OperationsDualPoolScoring.Select(candidates, operation.ComplexityPoints)) is not null)
        {
            var first = await eligible(pair.FirstUserId, ct);
            var second = await eligible(pair.SecondUserId, ct);
            if (first && second) break;
            candidates.RemoveAll(x => (x.UserId == pair.FirstUserId && !first) || (x.UserId == pair.SecondUserId && !second));
        }
        if (pair is null) return false;
        var assignments = new[] {
            new OperationsApertureValidation { CaseStageId = stage.Id, TeamId = pair.TeamId,
                UserId = pair.FirstUserId, Slot = 1, AssignedBy = operation.CreatedBy },
            new OperationsApertureValidation { CaseStageId = stage.Id, TeamId = pair.TeamId,
                UserId = pair.SecondUserId, Slot = 2, AssignedBy = operation.CreatedBy }
        };
        db.ApertureValidations.AddRange(assignments);
        stage.TeamId = pair.TeamId;
        stage.Status = "PENDING_VALIDATION";
        stage.Version++;
        operation.Version++;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        // Existing schema requires a causal user ID: retain the creator of the opening,
        // explicitly distinguish the system worker from a human assignment.
        OperationsAuditWriter.Case(db, caseId, stage.Id, operation.CreatedBy,
            "operations.aperture.validation.assigned", null, new {
                teamId = pair.TeamId, assignmentSource = "AutomaticPool",
                slots = assignments.Select(x => new { x.Slot, x.UserId }).ToArray()
            });
        db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.aperture.validation.assigned.v1",
            JsonSerializer.Serialize(new { caseId, stageId = stage.Id, operation.CompanyId, version = operation.Version })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
