using System.Data;
using System.Text.Json;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Domain.Services;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Persistence.Cases;

/// <summary>Assign one active review at a time, choosing the second reviewer after the first approval.</summary>
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
        var decisionKey = "apertures-decision:" + caseId.ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({decisionKey}, 0));", ct);
        // Re-read under the same lock as the decision endpoint.
        var operation = await db.Cases.SingleOrDefaultAsync(c => c.Id == caseId && c.CompanyId == company.Value, ct);
        if (operation is null || operation.Status != "PENDING_VALIDATION" || operation.CreatedBy == Guid.Empty)
            return false;
        if (operation.ApertureType != "AUTOMATIC") return false;
        var stage = await db.CaseStages.SingleOrDefaultAsync(x => x.CaseId == caseId && x.DepartmentCode == "APERTURAS", ct);
        if (stage is null || stage.Status is not ("PENDING" or "PENDING_VALIDATION")) return false;
        var existing = await db.ApertureValidations.AsNoTracking().Where(v => v.CaseStageId == stage.Id)
            .OrderBy(v => v.Slot).ToArrayAsync(ct);
        var legacyPair = existing.Length == 2 && existing.All(v => v.Status == "PENDING" &&
            v.ReviewedAtUtc == null && v.Comment == null);
        var secondReview = existing.Length == 1 && existing[0].Slot == 1 &&
            existing[0].Status == "APPROVED";
        if (existing.Length != 0 && !legacyPair && !secondReview) return false;
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
                v.CaseStage.Status == "PENDING_VALIDATION" && v.CaseStage.Case.Status == "PENDING_VALIDATION" &&
                v.CaseStageId != stage.Id && !db.ApertureValidations.Any(previous => previous.CaseStageId == v.CaseStageId && previous.Status == "PENDING" && previous.Slot < v.Slot))
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
        Guid selectedTeam;
        Guid selectedUser;
        if (secondReview)
        {
            var first = existing[0];
            AperturePoolMember? next = null;
            foreach (var candidate in candidates.Where(x => x.TeamId == first.TeamId && x.UserId != first.UserId)
                .OrderBy(x => (x.ActivePoints + operation.ComplexityPoints / 2m) / x.CapacityPoints)
                .ThenByDescending(x => x.RulePriority).ThenBy(x => x.UserId))
            {
                if (await eligible(candidate.UserId, ct)) { next = candidate; break; }
            }
            if (next is null) return false;
            selectedTeam = next.TeamId;
            selectedUser = next.UserId;
        }
        else
        {
            // Verify two eligible users without reserving the second slot.
            AperturePoolPair? pair;
            while ((pair = OperationsDualPoolScoring.Select(candidates, operation.ComplexityPoints)) is not null)
            {
                var first = await eligible(pair.FirstUserId, ct);
                var second = await eligible(pair.SecondUserId, ct);
                if (first && second) break;
                candidates.RemoveAll(x => (x.UserId == pair.FirstUserId && !first) || (x.UserId == pair.SecondUserId && !second));
            }
            if (pair is null) return false;
            selectedTeam = pair.TeamId;
            selectedUser = pair.FirstUserId;
        }
        // Only replace untouched legacy assignments, preserving the audit trail and all decisions.
        if (legacyPair)
            await db.ApertureValidations.Where(v => v.CaseStageId == stage.Id &&
                v.Status == "PENDING" && v.ReviewedAtUtc == null && v.Comment == null).ExecuteDeleteAsync(ct);
        var assignment = new OperationsApertureValidation {
            CaseStageId = stage.Id, TeamId = selectedTeam, UserId = selectedUser,
            Slot = (short)(secondReview ? 2 : 1), AssignedBy = operation.CreatedBy
        };
        db.ApertureValidations.Add(assignment);
        stage.TeamId = selectedTeam;
        stage.Status = "PENDING_VALIDATION";
        stage.Version++;
        operation.Version++;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        // Existing schema requires a causal user ID: retain the creator of the opening,
        // explicitly distinguish the system worker from a human assignment.
        OperationsAuditWriter.Case(db, caseId, stage.Id, operation.CreatedBy,
            "operations.aperture.validation.assigned",
            legacyPair ? existing.Select(x => new { x.Id, x.Slot, x.UserId, x.Status }).ToArray() : null,
            new {
                teamId = selectedTeam, assignmentSource = "AutomaticPool",
                slots = new[] { new { assignment.Slot, assignment.UserId } }
            });
        db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.aperture.validation.assigned.v1",
            JsonSerializer.Serialize(new { caseId, stageId = stage.Id, operation.CompanyId, version = operation.Version })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
