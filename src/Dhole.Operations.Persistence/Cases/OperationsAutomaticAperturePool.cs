using System.Data;
using System.Text.Json;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Domain.Services;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Persistence.Cases;

/// <summary>Assign one authorized reviewer; reconcile untouched legacy pairs and already approved openings.</summary>
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
        // An already recorded approval satisfies the new policy. Never erase a decision.
        var approved = existing.FirstOrDefault(v => v.Status == "APPROVED");
        if (approved is not null && existing.All(v => v.Status is "APPROVED" or "PENDING"))
        {
            var origin = await db.CaseStages.SingleOrDefaultAsync(s => s.CaseId == caseId &&
                s.DepartmentCode == "COORDINACION_ORIGEN", ct);
            if (origin is null)
            {
                origin = new OperationsCaseStage { CaseId = caseId,
                    DepartmentCode = "COORDINACION_ORIGEN", Status = "PENDING" };
                db.CaseStages.Add(origin);
            }
            await db.ApertureValidations.Where(v => v.CaseStageId == stage.Id && v.Status == "PENDING" &&
                v.ReviewedAtUtc == null && v.Comment == null).ExecuteDeleteAsync(ct);
            stage.Status = "COMPLETED";
            stage.CompletedAtUtc = DateTimeOffset.UtcNow;
            stage.Version++;
            operation.Status = "APERTURA_VALIDADA";
            operation.Version++;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OperationsAuditWriter.Case(db, caseId, stage.Id, approved.UserId,
                "operations.aperture.validation.policy-reconciled",
                existing.Select(v => new { v.Id, v.Slot, v.UserId, v.Status }).ToArray(),
                new { approved.Id, requiredReviewers = 1, originStageId = origin.Id, source = "SingleReviewPolicy" });
            db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.case.validated.v1",
                JsonSerializer.Serialize(new { caseId, stageId = stage.Id, originStageId = origin.Id,
                    actorId = approved.UserId, operation.CompanyId, version = operation.Version })));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        if (existing.Length != 0 && !legacyPair) return false;
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
                Points = gr.Sum(v => v.CaseStage.Case.ComplexityPoints) })
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
        AperturePoolMember? selected;
        while ((selected = OperationsDualPoolScoring.SelectSingle(candidates, operation.ComplexityPoints)) is not null)
        {
            if (await eligible(selected.UserId, ct)) break;
            candidates.RemoveAll(x => x.UserId == selected.UserId);
        }
        if (selected is null) return false;
        var selectedTeam = selected.TeamId;
        var selectedUser = selected.UserId;
        // Only replace untouched legacy assignments, preserving the audit trail and all decisions.
        if (legacyPair)
            await db.ApertureValidations.Where(v => v.CaseStageId == stage.Id &&
                v.Status == "PENDING" && v.ReviewedAtUtc == null && v.Comment == null).ExecuteDeleteAsync(ct);
        var assignment = new OperationsApertureValidation {
            CaseStageId = stage.Id, TeamId = selectedTeam, UserId = selectedUser,
            Slot = 1, AssignedBy = operation.CreatedBy
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
        // Match Notifications' existing 24-hour backlog safety policy. Do not
        // resend notifications for untouched legacy reviewer pairs or old imports.
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        if (!legacyPair && await db.AcceptedQuoteSnapshots.AsNoTracking().AnyAsync(
                s => s.CaseId == caseId && s.AcceptedAtUtc >= cutoff, ct))
            db.OutboxMessages.Add(OperationsOutboxFactory.CreateOpeningAssignedNotification(
                caseId, operation.Idtra, assignment.Id, selectedUser));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
