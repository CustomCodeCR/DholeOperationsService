using System.Data;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Api.Integrations;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Domain.Services;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>Two independent, individually audited Aperturas validation decisions per case.</summary>
public static class OperationsTeamValidationEndpoints
{
    public sealed record AssignReviewersRequest(Guid? TeamId);
    public sealed record ReviewDecisionRequest(string Decision, string? Comment);

    public static IEndpointRouteBuilder MapOperationsTeamValidationEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization();
        api.MapGet("/apertures/validation-queue", async (
            HttpContext http, OperationsDbContext db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.read"))
                return Results.Forbid();
            var company = OperationsCaseAccess.CompanyId(http.User);
            var readAll = OperationsCaseAccess.HasScope(http.User, "operations.case.read-all");
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) && company is null) return Results.Forbid();
            var query = db.Cases.AsNoTracking()
                .Where(c => c.Stages.Any(s => s.DepartmentCode == "APERTURAS"));
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User))
                query = query.Where(c => c.CompanyId == company);
            if (!readAll)
                query = query.Where(c => c.CreatedBy == actor.Value ||
                    c.Stages.Any(s => db.ApertureValidations.Any(v =>
                        v.CaseStageId == s.Id && v.UserId == actor.Value)) ||
                    c.Stages.Any(s => s.TeamId.HasValue && db.TeamMembers.Any(m =>
                        m.TeamId == s.TeamId && m.UserId == actor.Value)));
            var n = Math.Max(page ?? 1, 1);
            var size = Math.Clamp(pageSize ?? 25, 1, 100);
            return Results.Ok(new {
                page = n, pageSize = size, total = await query.CountAsync(ct),
                items = await query.OrderByDescending(c => c.CreatedAtUtc).ThenBy(c => c.Id)
                    .Skip((n - 1) * size).Take(size).Select(c => new {
                        c.Id, c.Idtra, c.CompanyId, c.AccountId, c.ModalityCode,
                        c.Status, c.Priority, c.CreatedAtUtc,
                        Validators = db.ApertureValidations.Where(v => v.CaseStage.CaseId == c.Id)
                            .OrderBy(v => v.Slot)
                            .Select(v => new { v.Id, v.Slot, v.UserId, v.Status }).ToArray()
                    }).ToArrayAsync(ct)
            });
        });

        api.MapGet("/cases/{caseId:guid}/aperture-validations", async (
            Guid caseId, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(caseId, http.User, ct)) return Results.Forbid();
            var stage = await db.CaseStages.AsNoTracking()
                .Where(s => s.CaseId == caseId && s.DepartmentCode == "APERTURAS")
                .Select(s => new { s.Id, s.Status, s.TeamId }).SingleOrDefaultAsync(ct);
            if (stage is null) return Results.NotFound();
            var validators = await db.ApertureValidations.AsNoTracking()
                .Where(v => v.CaseStageId == stage.Id).OrderBy(v => v.Slot)
                .Select(v => new { v.Id, v.TeamId, v.UserId, v.Slot, v.Status,
                    v.AssignedAtUtc, v.ReviewedAtUtc, v.Comment }).ToArrayAsync(ct);
            return Results.Ok(new { stage, requiredReviewers = 2, validators });
        });

        api.MapPost("/cases/{caseId:guid}/aperture-validations/assign", async (
            Guid caseId, AssignReviewersRequest input, HttpContext http,
            OperationsDbContext db, OperationsAuthUserVerifier auth, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null ||
                !OperationsCaseAccess.HasScope(http.User, "operations.aperture.validation.assign") ||
                !OperationsCaseAccess.HasScope(http.User, "operations.team.manage"))
                return Results.Forbid();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var operation = await db.Cases.SingleOrDefaultAsync(c => c.Id == caseId, ct);
            if (operation is null) return Results.NotFound();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) &&
                OperationsCaseAccess.CompanyId(http.User) != operation.CompanyId) return Results.Forbid();
            var key = "apertures-pool:" + operation.CompanyId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));", ct);
            var stage = await db.CaseStages.SingleOrDefaultAsync(s =>
                s.CaseId == caseId && s.DepartmentCode == "APERTURAS", ct);
            if (stage is null) return Results.NotFound();
            if (operation.Status is "CANCELLED" or "COMPLETED" or "APERTURA_VALIDADA" ||
                stage.Status is "COMPLETED" or "SKIPPED" or "CHANGES_REQUIRED")
                return Results.Conflict(new { code = "APERTURE_NOT_ASSIGNABLE" });
            var existing = await db.ApertureValidations.AsNoTracking()
                .Where(x => x.CaseStageId == stage.Id).OrderBy(x => x.Slot)
                .Select(x => new { x.Id, x.UserId, x.Slot, x.Status }).ToArrayAsync(ct);
            if (existing.Length == 2)
                return Results.Ok(new { stageId = stage.Id, assigned = existing, duplicate = true });
            if (existing.Length != 0)
                return Results.Conflict(new { code = "INCOMPLETE_VALIDATION_PAIR" });

            var rules = await db.AssignmentRules.AsNoTracking().Where(r =>
                r.IsActive && r.Team.IsActive && r.Team.CompanyId == operation.CompanyId &&
                r.DepartmentCode == "APERTURAS" &&
                (r.ModalityCode == null || r.ModalityCode == operation.ModalityCode) &&
                (r.OriginCountryCode == null || r.OriginCountryCode == operation.OriginCountryCode) &&
                (r.DestinationCountryCode == null ||
                    r.DestinationCountryCode == operation.DestinationCountryCode))
                .Select(r => new { r.TeamId, r.Priority }).ToArrayAsync(ct);
            var teamIds = rules.Select(r => r.TeamId).Distinct().ToArray();
            var teamsQuery = db.Teams.AsNoTracking().Include(t => t.Members).Where(t =>
                t.IsActive && t.CompanyId == operation.CompanyId && t.DepartmentCode == "APERTURAS");
            if (input.TeamId.HasValue)
                teamsQuery = teamsQuery.Where(t => t.Id == input.TeamId.Value);
            else if (teamIds.Length > 0)
                teamsQuery = teamsQuery.Where(t => teamIds.Contains(t.Id));
            var teams = await teamsQuery.ToArrayAsync(ct);
            var workloads = await db.ApertureValidations.AsNoTracking()
                .Where(v => v.Status == "PENDING" && v.CaseStage.Case.CompanyId == operation.CompanyId)
                .GroupBy(v => v.UserId).Select(g => new {
                    UserId = g.Key, Points = g.Sum(v => v.CaseStage.Case.ComplexityPoints / 2m)
                }).ToDictionaryAsync(x => x.UserId, x => x.Points, ct);
            var candidates = teams.SelectMany(team => team.Members
                .Where(member => member.IsAvailable && member.CapacityPoints > 0)
                .Select(member => new AperturePoolMember(team.Id, member.UserId,
                    member.CapacityPoints, workloads.GetValueOrDefault(member.UserId),
                    rules.Where(r => r.TeamId == team.Id).Select(r => r.Priority)
                        .DefaultIfEmpty(0).Max()))).ToList();
            AperturePoolPair? pair;
            try
            {
                while ((pair = OperationsDualPoolScoring.Select(candidates,
                    operation.ComplexityPoints)) is not null)
                {
                    var validFirst = await auth.IsEligibleAsync(pair.FirstUserId,
                        http.Request.Headers.Authorization.ToString(), ct, requireReview: true);
                    var validSecond = await auth.IsEligibleAsync(pair.SecondUserId,
                        http.Request.Headers.Authorization.ToString(), ct, requireReview: true);
                    if (validFirst && validSecond) break;
                    candidates.RemoveAll(x => (x.UserId == pair.FirstUserId && !validFirst) ||
                        (x.UserId == pair.SecondUserId && !validSecond));
                }
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (HttpRequestException) {
                return Results.Problem(statusCode: 503, title: "Auth unavailable, validation not assigned");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) {
                return Results.Problem(statusCode: 503, title: "Auth timed out, validation not assigned");
            }
            if (pair is null)
                return Results.Conflict(new { code = "POOL_REQUIRES_TWO_ELIGIBLE_DISTINCT_USERS" });

            var assignments = new[] {
                new OperationsApertureValidation { CaseStageId = stage.Id,
                    TeamId = pair.TeamId, UserId = pair.FirstUserId, Slot = 1,
                    AssignedBy = actor.Value },
                new OperationsApertureValidation { CaseStageId = stage.Id,
                    TeamId = pair.TeamId, UserId = pair.SecondUserId, Slot = 2,
                    AssignedBy = actor.Value }
            };
            db.ApertureValidations.AddRange(assignments);
            stage.TeamId = pair.TeamId;
            stage.Status = "PENDING_VALIDATION";
            stage.Version++;
            operation.Status = "PENDING_VALIDATION";
            operation.Version++;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OperationsAuditWriter.Case(db, caseId, stage.Id, actor.Value,
                "operations.aperture.validation.assigned", null,
                new { teamId = pair.TeamId, slots = assignments.Select(a =>
                    new { a.Slot, a.UserId }).ToArray() });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { stageId = stage.Id, teamId = pair.TeamId,
                assigned = assignments.Select(a => new { a.Id, a.Slot, a.UserId, a.Status }), duplicate = false });
        });

        api.MapPost("/cases/{caseId:guid}/aperture-validations/{validationId:guid}/decision", async (
            Guid caseId, Guid validationId, ReviewDecisionRequest input, HttpContext http,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null ||
                !OperationsCaseAccess.HasScope(http.User, "operations.case.read") ||
                !OperationsCaseAccess.HasScope(http.User, "operations.aperture.validation.review"))
                return Results.Forbid();
            var decision = input.Decision?.Trim().ToUpperInvariant();
            if (decision is not ("APPROVED" or "REJECTED") ||
                input.Comment?.Length > 2000 ||
                (decision == "REJECTED" && string.IsNullOrWhiteSpace(input.Comment)))
                return Results.BadRequest(new { code = "VALIDATION_DECISION_INVALID" });
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var key = "apertures-decision:" + caseId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));", ct);
            var validation = await db.ApertureValidations.SingleOrDefaultAsync(v =>
                v.Id == validationId && v.CaseStage.CaseId == caseId, ct);
            if (validation is null) return Results.NotFound();
            if (validation.UserId != actor.Value) return Results.Forbid();
            if (validation.Status == decision)
                return Results.Ok(new { validation.Id, validation.Status, duplicate = true });
            if (validation.Status != "PENDING")
                return Results.Conflict(new { code = "VALIDATION_ALREADY_RESOLVED" });
            var stage = await db.CaseStages.SingleAsync(s => s.Id == validation.CaseStageId, ct);
            var operation = await db.Cases.SingleAsync(c => c.Id == caseId, ct);
            if (stage.Status != "PENDING_VALIDATION" || operation.Status != "PENDING_VALIDATION")
                return Results.Conflict(new { code = "APERTURE_REVIEW_CLOSED" });
            var count = await db.ApertureValidations.CountAsync(v =>
                v.CaseStageId == stage.Id, ct);
            if (count != 2) return Results.Conflict(new { code = "TWO_VALIDATORS_REQUIRED" });
            validation.Status = decision!;
            validation.Comment = input.Comment?.Trim();
            validation.ReviewedAtUtc = DateTimeOffset.UtcNow;
            var otherApproved = await db.ApertureValidations.AnyAsync(v =>
                v.CaseStageId == stage.Id && v.Id != validation.Id && v.Status == "APPROVED", ct);
            var bothApproved = decision == "APPROVED" && otherApproved;
            if (decision == "REJECTED")
            {
                stage.Status = "CHANGES_REQUIRED";
                operation.Status = "APERTURA_REQUIERE_CAMBIOS";
            }
            else if (bothApproved)
            {
                stage.Status = "COMPLETED";
                stage.CompletedAtUtc = DateTimeOffset.UtcNow;
                operation.Status = "APERTURA_VALIDADA";
                // Origin starts only after BOTH reviewers approve the same opening.
                if (!await db.CaseStages.AnyAsync(s =>
                    s.CaseId == caseId && s.DepartmentCode == "COORDINACION_ORIGEN", ct))
                    db.CaseStages.Add(new OperationsCaseStage {
                        CaseId = caseId, DepartmentCode = "COORDINACION_ORIGEN", Status = "PENDING"
                    });
            }
            stage.Version++;
            operation.Version++;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OperationsAuditWriter.Case(db, caseId, stage.Id, actor.Value,
                "operations.aperture.validation.reviewed",
                new { validationId, status = "PENDING" },
                new { validationId, decision, slot = validation.Slot, bothApproved });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { validation.Id, validation.Status,
                stageStatus = stage.Status, operationStatus = operation.Status,
                bothApproved, duplicate = false });
        });

        api.MapPatch("/teams/{teamId:guid}/active", async (
            Guid teamId, SetTeamActiveRequest input, HttpContext http,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.team.manage"))
                return Results.Forbid();
            var team = await db.Teams.SingleOrDefaultAsync(t => t.Id == teamId, ct);
            if (team is null) return Results.NotFound();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) &&
                OperationsCaseAccess.CompanyId(http.User) != team.CompanyId) return Results.Forbid();
            if (team.IsActive == input.IsActive) return Results.Ok(new { team.Id, team.IsActive, duplicate = true });
            OperationsAuditWriter.Team(db, teamId, actor.Value, "operations.team.active.changed",
                new { team.IsActive }, new { input.IsActive });
            team.IsActive = input.IsActive;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { team.Id, team.IsActive });
        });

        api.MapDelete("/teams/{teamId:guid}/members/{userId:guid}", async (
            Guid teamId, Guid userId, HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.team.manage"))
                return Results.Forbid();
            var team = await db.Teams.SingleOrDefaultAsync(t => t.Id == teamId, ct);
            if (team is null) return Results.NotFound();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) &&
                OperationsCaseAccess.CompanyId(http.User) != team.CompanyId) return Results.Forbid();
            var member = await db.TeamMembers.SingleOrDefaultAsync(m =>
                m.TeamId == teamId && m.UserId == userId, ct);
            if (member is null) return Results.NoContent();
            var pending = await db.ApertureValidations.AnyAsync(v =>
                v.TeamId == teamId && v.UserId == userId && v.Status == "PENDING", ct);
            if (pending) return Results.Conflict(new { code = "MEMBER_HAS_PENDING_VALIDATIONS" });
            OperationsAuditWriter.Team(db, teamId, actor.Value, "operations.team.member.removed",
                new { member.UserId, member.CapacityPoints, member.IsSupervisor, member.IsAvailable },
                null);
            db.TeamMembers.Remove(member);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        api.MapGet("/teams/{teamId:guid}/audit", async (
            Guid teamId, HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.audit.read") ||
                !OperationsCaseAccess.HasScope(http.User, "operations.team.manage"))
                return Results.Forbid();
            var team = await db.Teams.AsNoTracking().SingleOrDefaultAsync(t => t.Id == teamId, ct);
            if (team is null) return Results.NotFound();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) &&
                OperationsCaseAccess.CompanyId(http.User) != team.CompanyId) return Results.Forbid();
            return Results.Ok(await db.TeamAuditEntries.AsNoTracking()
                .Where(a => a.TeamId == teamId).OrderByDescending(a => a.OccurredAtUtc)
                .Take(100).Select(a => new { a.EventId, a.ActionCode, a.ActorUserId,
                    a.BeforeJson, a.AfterJson, a.OccurredAtUtc }).ToArrayAsync(ct));
        });

        api.MapGet("/cases/{caseId:guid}/audit", async (
            Guid caseId, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.audit.read") ||
                !await access.CanReadAsync(caseId, http.User, ct)) return Results.Forbid();
            return Results.Ok(await db.CaseAuditEntries.AsNoTracking()
                .Where(a => a.CaseId == caseId).OrderByDescending(a => a.OccurredAtUtc)
                .Take(100).Select(a => new { a.EventId, a.ActionCode, a.ActorUserId,
                    a.BeforeJson, a.AfterJson, a.OccurredAtUtc }).ToArrayAsync(ct));
        });

        return app;
    }
    public sealed record SetTeamActiveRequest(bool IsActive);
}
